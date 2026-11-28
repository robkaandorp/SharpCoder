using System.Net;
using System.Text;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Delayed-timer and in-window deadline proofs. The clock advances while the gated operation is
/// parked; the callback is intentionally NOT fired. Cleanup always releases and joins outstanding work.
/// </summary>
public sealed class WebRepairDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendLateStatus_WithElapsedDeadlineAndDelayedCallback_ReturnsTimeout(HttpStatusCode status)
    {
        await using var window = await OpenWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        window.Clock.Advance(Deadline + TimeSpan.FromSeconds(1));
        Assert.False(window.Call.IsCompleted); // send is outstanding before late response release
        window.Handler.CompleteWith(status, new StringContent(SecretSentinel));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.False(timer.Fired);
    }

    [Theory]
    [InlineData("OCE")]
    [InlineData("IO")]
    [InlineData("HTTP")]
    [InlineData("InvalidOperation")]
    [InlineData("TimeoutMarker")]
    [InlineData("NestedTimeoutMarker")]
    public async Task SendLateSecretFault_WithElapsedDeadlineAndDelayedCallback_ReturnsTimeout(string fault)
    {
        await using var window = await OpenWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        window.Clock.Advance(Deadline);
        Assert.False(window.Call.IsCompleted); // cancellation-ignoring send remains outstanding
        window.Handler.CompleteWith(Fault(fault));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
        Assert.False(timer.Fired);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("OCE")]
    [InlineData("IO")]
    [InlineData("HTTP")]
    [InlineData("InvalidOperation")]
    [InlineData("TimeoutMarker")]
    [InlineData("NestedTimeoutMarker")]
    public async Task StreamOpenLateReturnOrThrow_WithElapsedDeadline_ReturnsTimeout(string outcome)
    {
        await using var window = await OpenWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var content = window.CompleteWithGatedOpen();
        await content.WaitForOpenAsync();
        window.Clock.Advance(Deadline);
        Assert.False(window.Call.IsCompleted); // real ReadAsStreamAsync is parked and never abandoned
        if (outcome == "return") content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        else content.Release(Fault(outcome));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.False(timer.Fired);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task StreamOpenBeforeExpiry_ReturnsSuccess_AndOpenWasReallyReached()
    {
        await using var window = await OpenWindow.StartAsync();
        await window.RendezvousAsync();
        var content = window.CompleteWithGatedOpen();
        await content.WaitForOpenAsync();
        content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{\"title\":\"T\"}")));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("T", result.GetRawData()!.GetRawTitle());
    }

    [Fact]
    public async Task StreamOpenBeforeExpiry_SecretIoFault_IsTransportFailureWithoutLeak()
    {
        await using var window = await OpenWindow.StartAsync();
        await window.RendezvousAsync();
        var content = window.CompleteWithGatedOpen();
        await content.WaitForOpenAsync();
        content.Release(Fault("IO"));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.TransportFailure);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task StreamOpen_CallerCancellationWins_AndNoAbandonmentWhileParked()
    {
        await using var window = await OpenWindow.StartAsync();
        await window.RendezvousAsync();
        var content = window.CompleteWithGatedOpen();
        await content.WaitForOpenAsync();
        window.Clock.Advance(Deadline);
        window.Caller.Cancel();
        Assert.False(window.Call.IsCompleted); // operation is still parked: must not be abandoned
        content.Release(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(window.Caller.Token, thrown.CancellationToken);
        Assert.True(content.Entered.IsCompleted);
    }

    [Fact]
    public async Task ReadLateReturn_WithElapsedDeadlineAndDelayedCallback_ReturnsTimeout()
    {
        await using var window = await OpenWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "delayed-callback late read");
        window.Clock.Advance(Deadline);
        Assert.False(window.Call.IsCompleted); // parked read is not abandoned at expiry
        body.ReleaseRead(1);
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.False(timer.Fired);
    }

    [Theory]
    [InlineData("OCE")]
    [InlineData("IO")]
    [InlineData("HTTP")]
    [InlineData("InvalidOperation")]
    [InlineData("TimeoutMarker")]
    [InlineData("NestedTimeoutMarker")]
    public async Task ReadLateThrow_WithElapsedDeadlineAndDelayedCallback_ReturnsTimeout(string fault)
    {
        await using var window = await OpenWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "delayed-callback late throw");
        window.Clock.Advance(Deadline);
        Assert.False(window.Call.IsCompleted); // parked stream read is not abandoned at expiry
        body.ReleaseRead(Fault(fault));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
        Assert.False(timer.Fired);
    }

    [Fact]
    public async Task CompletedBody_ExpiryDuringDisposalBeforeParsing_RemainsSuccess()
    {
        var clock = new ManualWebTimeProvider();
        var body = new DisposalGatedBodyStream("{\"title\":\"T\",\"content\":\"C\",\"links\":[\"L\"]}");
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new PreopenedStreamContent(body));
        var client = Create(Options(handler, timeProvider: clock, deadline: Deadline));
        try
        {
            var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
            var timer = await clock.WaitForTimerAsync();
            await body.WaitForReadReturnsAsync(2); // complete payload AND EOF have both returned
            await body.DisposeEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Advance(Deadline); // in-window expiry AFTER EOF but BEFORE parsing
            Assert.False(timer.Fired);
            Assert.False(call.IsCompleted);
            body.ReleaseDispose();
            var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Equal("T", result.GetRawData()!.GetRawTitle());
            Assert.Equal("C", result.GetRawData()!.GetRawContent());
        }
        finally
        {
            body.ReleaseDispose();
        }
    }

    [Fact]
    public async Task EofStillPending_WhenDeadlineElapses_ReturnsTimeout()
    {
        await using var window = await OpenWindow.StartAsync();
        await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "pending EOF");
        window.Clock.Advance(Deadline);
        Assert.False(window.Call.IsCompleted); // no abandonment while EOF is parked
        body.ReleaseRead(0);
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
    }

    private static Exception Fault(string kind) => kind switch
    {
        "OCE" => new OperationCanceledException(SecretSentinel),
        "IO" => new IOException(SecretSentinel),
        "HTTP" => new HttpRequestException(SecretSentinel),
        "TimeoutMarker" => new WebToolTimeoutException(SecretSentinel, new InvalidOperationException(SecretSentinel)),
        "NestedTimeoutMarker" => new HttpRequestException(SecretSentinel,
            new IOException(SecretSentinel, new WebToolTimeoutException(SecretSentinel))),
        _ => new InvalidOperationException(SecretSentinel),
    };

    private sealed class OpenWindow : IAsyncDisposable
    {
        internal ManualWebTimeProvider Clock { get; } = new();
        internal GatedWebHandler Handler { get; } = new();
        internal CancellationTokenSource Caller { get; } = new();
        internal GatedOpenContent? OpenContent { get; private set; }
        internal GatedWebStream? Body { get; private set; }
        internal Task<WebToolResult<WebFetchData>> Call { get; private set; } = null!;

        internal static async Task<OpenWindow> StartAsync()
        {
            var window = new OpenWindow();
            try
            {
                var client = Create(Options(window.Handler, timeProvider: window.Clock, deadline: Deadline));
                window.Call = client.FetchAsync("https://page.example.test", window.Caller.Token);
                await window.Handler.WaitForEntryAsync("repair deadline window");
                return window;
            }
            catch
            {
                await window.DisposeAsync();
                throw;
            }
        }

        internal async Task<ManualWebTimer> RendezvousAsync()
        {
            var timer = await Clock.WaitForTimerAsync();
            Assert.True(Handler.Entered.IsCompleted);
            Assert.Equal(Deadline, timer.DueTime);
            Assert.False(timer.Fired);
            return timer;
        }

        internal GatedOpenContent CompleteWithGatedOpen()
        {
            var content = new GatedOpenContent();
            OpenContent = content;
            Handler.CompleteWith(HttpStatusCode.OK, content);
            return content;
        }

        internal GatedWebStream CompleteWithGatedBody()
        {
            var body = new GatedWebStream();
            Body = body;
            Handler.CompleteWith(HttpStatusCode.OK, new StreamContent(body));
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            // Failure-safe: release gates, cancel caller, then join the outstanding production task.
            Handler.ReleaseWithFallback();
            OpenContent?.ReleaseFallback();
            Body?.ReleaseAllPending();
            Caller.Cancel();
            try { await Call.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
            catch { }
            Caller.Dispose();
        }
    }
}
