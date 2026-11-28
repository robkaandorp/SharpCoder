using System.Net;
using System.Text;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Focused, named mutation-detection rows for iteration 1's critical guards. Each test uses a
/// positive control (the guarded operation or boundary was really reached) and asserts the exact
/// behavior that an isolated guard-removal mutant would destroy.
/// </summary>
public sealed class Iter1GuardContractTests
{
    [Fact]
    public async Task CredentialUnavailableGuard_MalformedCredential_SendsNothingAndReturnsSafeFailure()
    {
        await using var owned = new DeterministicTestOwnedResources();
        var handler = owned.Track(FetchOk());
        var client = Create(Options(handler, accessor: () => "not a b64token"));

        var result = await client.FetchAsync(
            "https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.CredentialUnavailable);
        AssertSafeMessage(result);
        Assert.Equal(0, handler.SendCount);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(SecretSentinel, result.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialUnavailableGuard_ThrowingAccessor_NoSendAndNoExceptionText()
    {
        await using var owned = new DeterministicTestOwnedResources();
        var handler = owned.Track(FetchOk());
        var calls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            calls++;
            throw new InvalidOperationException(SecretSentinel);
        }));

        var result = await client.FetchAsync(
            "https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.CredentialUnavailable);
        AssertSafeMessage(result);
        Assert.Equal(1, calls);
        Assert.Equal(0, handler.SendCount);
        Assert.DoesNotContain(SecretSentinel, result.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationGuard_BlankQueryAndUrl_NoCredentialAccessorOrSend()
    {
        await using var owned = new DeterministicTestOwnedResources();
        var handler = owned.Track(SearchOk());
        var calls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            calls++;
            return Credential;
        }));

        var blankQuery = await client.SearchAsync(" \t ", cancellationToken: TestContext.Current.CancellationToken);
        var blankUrl = await client.FetchAsync("\r\n", TestContext.Current.CancellationToken);

        AssertFailure(blankQuery, WebToolErrorKind.Validation);
        AssertFailure(blankUrl, WebToolErrorKind.Validation);
        AssertSafeMessage(blankQuery);
        AssertSafeMessage(blankUrl);
        Assert.Equal(0, calls);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ByteCapGuard_ExactBoundary_AcceptsAtCapAndRejectsOneByteOver()
    {
        // A 15-byte stream is split into four 4-byte reads. The EOF read is a POSITIVE CONTROL that
        // the production code reached the read boundary with the cap still permitting the payload.
        const string exact = "{\"results\":[]} ";
        Assert.Equal(15, Encoding.UTF8.GetByteCount(exact));
        const int cap = 15;
        var bytes = Encoding.UTF8.GetBytes(exact);
        await using var owned = new DeterministicTestOwnedResources();
        var stream = owned.Track(new ChunkedWebStream(bytes, chunkSize: 4));
        var handler = owned.Track(RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream)));
        var result = await Create(Options(handler, maxResponseBytes: cap))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Data!.Results);
        Assert.True(stream.ReadCount >= 5, "Positive control: the full body plus EOF was read.");
        Assert.True(stream.Disposed);

        var overCap = owned.Track(new ChunkedWebStream(Encoding.UTF8.GetBytes("{\"results\":[]}  "), chunkSize: 4));
        var overHandler = owned.Track(RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(overCap)));
        var over = await Create(Options(overHandler, maxResponseBytes: cap))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);

        AssertFailure(over, WebToolErrorKind.ResponseTooLarge);
        AssertSafeMessage(over);
        Assert.True(overCap.ReadCount >= 4, "Positive control: the crossing read was reached.");
        Assert.True(overCap.Disposed);
    }

    [Fact]
    public async Task NonSuccessNoBodyReadGuard_HeadersBeforeExpiry_ImmediateSafeHttpFailure()
    {
        await using var owned = new DeterministicTestOwnedResources();
        var neverRead = owned.Track(new NeverReadContent(SecretSentinel));
        var handler = owned.Track(RecordingWebHandler.With(HttpStatusCode.BadGateway, neverRead));
        var result = await Create(Options(handler)).FetchAsync(
            "https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.HttpFailure);
        AssertSafeMessage(result);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(0, neverRead.ReadAttempts);
        Assert.True(neverRead.Disposed);
        Assert.DoesNotContain(SecretSentinel, result.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeadlinePrecedenceGuard_LateNonSuccessHeaders_ExpiredDeadlineWins()
    {
        // Rendezvous on the exact production timer and gated send BEFORE firing the manual clock.
        await using var window = await HeaderWindowForGuard.StartAsync();
        var timer = await window.RendezvousAsync("late non-success precedence");
        Assert.False(timer.Fired);
        Assert.True(window.Handler.Entered.IsCompleted);

        timer.Fire();
        var content = window.CompleteWithCountingBody(HttpStatusCode.BadGateway, SecretSentinel);

        var result = await window.Call.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
        Assert.Equal(0, content.ReadAttempts);
        Assert.True(content.Disposed);
        Assert.True(timer.Fired);
        Assert.True(window.Handler.Entered.IsCompleted);
    }

    [Fact]
    public async Task DeadlinePrecedenceGuard_LateSuccessResponse_ExpiredDeadlineWinsWithoutOpeningBody()
    {
        await using var window = await HeaderWindowForGuard.StartAsync();
        var timer = await window.RendezvousAsync("late success precedence");
        Assert.False(timer.Fired);
        Assert.True(window.Handler.Entered.IsCompleted);

        timer.Fire();
        var content = window.CompleteWithCountingBody(HttpStatusCode.OK, "{}");

        var result = await window.Call.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
        Assert.Equal(0, content.ReadAttempts);
        Assert.False(content.ReadEntered.IsCompleted);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task DeadlinePrecedenceGuard_LateUnexpectedThrow_ExpiredDeadlineWins()
    {
        await using var window = await HeaderWindowForGuard.StartAsync();
        var timer = await window.RendezvousAsync("late unexpected throw precedence");
        Assert.False(timer.Fired);
        Assert.True(window.Handler.Entered.IsCompleted);

        timer.Fire();
        window.Handler.CompleteWith(new InvalidOperationException(SecretSentinel));

        var result = await window.Call.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task CallerCancellationGuard_WinsOverExpiredDeadline_AndPropagatesCallerToken()
    {
        await using var window = await HeaderWindowForGuard.StartAsync();
        var timer = await window.RendezvousAsync("caller cancellation precedence");
        timer.Fire();
        window.Caller.Cancel();
        window.Handler.CompleteWithJson("{}");

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(window.Caller.Token, thrown.CancellationToken);
        Assert.True(timer.Fired);
        Assert.True(window.Handler.Entered.IsCompleted);
    }

    [Fact]
    public async Task CompletedBodyGuard_SuccessParsedBeforeLaterExpiry_RemainsSuccess()
    {
        await using var window = await HeaderWindowForGuard.StartAsync();
        var timer = await window.RendezvousAsync("completed body exit");
        var body = window.CompleteWithValidGatedBody(
            "{\"title\":\"T\",\"content\":\"C\",\"links\":[\"L\"]}");
        await body.WaitForCompletedReadAsync();
        Assert.True(body.Completed);
        Assert.False(timer.Fired);

        body.ReleaseEof();
        var result = await window.Call.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        timer.Fire();

        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorKind);
        Assert.Equal("T", result.Data!.Title);
        Assert.Equal("C", result.Data.Content);
        Assert.Equal(new[] { "L" }, result.Data.Links);
        Assert.Empty(result.DisplayMessage);
    }

    /// <summary>
    /// A compact gated window with the same exact-timer/operation rendezvous and deterministic
    /// cleanup as the main deadline suite. It is intentionally local to these named mutant rows.
    /// </summary>
    private sealed class HeaderWindowForGuard : IAsyncDisposable
    {
        private HeaderWindowForGuard()
        {
        }

        internal ManualWebTimeProvider Clock { get; } = new();
        internal GatedWebHandler Handler { get; } = new();
        internal CancellationTokenSource Caller { get; } = new();
        internal CountingHttpContent? Content { get; private set; }
        internal GatedWebStream? Body { get; private set; }
        internal GatedValidBodyStream? ValidBody { get; private set; }
        internal Task<WebToolResult<WebFetchData>> Call { get; private set; } = null!;

        internal static async Task<HeaderWindowForGuard> StartAsync()
        {
            var window = new HeaderWindowForGuard();
            try
            {
                var client = Create(Options(
                    window.Handler,
                    timeProvider: window.Clock,
                    deadline: TimeSpan.FromSeconds(15)));
                window.Call = client.FetchAsync("https://page.example.test", window.Caller.Token);
                await window.Handler.WaitForEntryAsync("guard contract");
                return window;
            }
            catch
            {
                await window.DisposeAsync();
                throw;
            }
        }

        internal async Task<ManualWebTimer> RendezvousAsync(string because)
        {
            var timer = await Clock.WaitForTimerAsync();
            Assert.True(Handler.Entered.IsCompleted, because + ": gated send was not reached.");
            Assert.Equal(TimeSpan.FromSeconds(15), timer.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
            Assert.IsType<CancellationTokenSource>(timer.State);
            Assert.False(timer.Fired);
            return timer;
        }

        internal CountingHttpContent CompleteWithCountingBody(HttpStatusCode status, string body)
        {
            var content = new CountingHttpContent(body);
            Content = content;
            Handler.CompleteWith(status, content);
            return content;
        }

        internal GatedValidBodyStream CompleteWithValidGatedBody(string body)
        {
            var stream = new GatedValidBodyStream(Encoding.UTF8.GetBytes(body));
            ValidBody = stream;
            Handler.CompleteWith(HttpStatusCode.OK, new StreamContent(stream));
            return stream;
        }

        public async ValueTask DisposeAsync()
        {
            Handler.ReleaseWithFallback();
            Body?.ReleaseAllPending();
            ValidBody?.ReleaseEof();
            Caller.Cancel();
            try
            {
                await Call.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                Caller.Dispose();
            }
        }
    }
}
