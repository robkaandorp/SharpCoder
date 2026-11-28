using System.Net;
using System.Text;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Deterministic cooperative-deadline matrix. Every test rendezvous on BOTH the exact production
/// timer registered with the test-owned manual <see cref="TimeProvider"/> AND the gated handler/read
/// operation before advancing or releasing. No narrow wall-clock margins are used.
/// </summary>
public sealed class OllamaWebClientDeadlineTests
{
    private static readonly TimeSpan StandardDeadline = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A reusable fixture: manual clock, gated handler, caller CTS, and a started client call.
    /// Cleanup is deterministic and unblocks both the handler and any pending read.
    /// </summary>
    private sealed class HeaderWindow : IAsyncDisposable
    {
        internal ManualWebTimeProvider Clock { get; } = new();
        internal GatedWebHandler Handler { get; } = new();
        internal CancellationTokenSource Caller { get; } = new();
        internal GatedWebStream? Body { get; private set; }
        internal Task<WebToolResult<WebFetchData>> Call { get; private set; } = null!;

        internal static async Task<HeaderWindow> StartAsync(TimeSpan? deadline = null)
        {
            var window = new HeaderWindow();
            var options = Options(window.Handler, timeProvider: window.Clock, deadline: deadline ?? StandardDeadline);
            var client = Create(options);
            window.Call = client.FetchAsync("https://page.example.test", window.Caller.Token);
            await window.Handler.WaitForEntryAsync("fetch");
            return window;
        }

        internal async Task<ManualWebTimer> RendezvousAsync()
        {
            // Both sides must be live: the gated send has been entered AND production has
            // registered its one-shot deadline timer with the exact due time/period/state.
            var timer = await Clock.WaitForTimerAsync();
            Assert.True(Handler.Entered.IsCompleted, "Positive control: the gated handler was not reached.");
            Assert.Equal(StandardDeadline, timer.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
            Assert.IsType<CancellationTokenSource>(timer.State);
            Assert.False(timer.Fired);
            return timer;
        }

        internal OwningGatedContent? Content { get; private set; }

        internal GatedWebStream CompleteWithGatedBody(HttpStatusCode status = HttpStatusCode.OK)
        {
            var stream = new GatedWebStream();
            Body = stream;
            var content = new OwningGatedContent(stream);
            Content = content;
            Handler.CompleteWith(status, content);
            return stream;
        }

        public async ValueTask DisposeAsync()
        {
            // Deterministic cleanup on success AND failure paths: never leave a handler/read or a
            // production task outstanding.
            Handler.ReleaseWithFallback();
            Body?.ReleaseAllPending();
            Caller.Cancel();
            try { await Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* bounded diagnostics already asserted above */ }
            Caller.Dispose();
        }
    }

    [Fact]
    public async Task Timer_IsExactProductionRegistration_OneShotAndLinkedToDeadlineCts()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        Assert.Single(window.Clock.RegisteredTimers);
        Assert.True(window.Handler.Entered.IsCompleted); // positive control

        // Advance fake time WITHOUT firing: the deadline must not be inferred from elapsed time.
        window.Clock.Advance(TimeSpan.FromHours(1));
        Assert.False(timer.Fired);
        Assert.False(window.Call.IsCompleted);

        window.Handler.CompleteWithJson("{}");
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); // positive control: the operation really completed
        Assert.True(timer.Disposed);   // timer disposed deterministically
    }

    [Fact]
    public async Task DeadlineStartsAfterCredential_AndCredentialResolutionIsOutsideTheTimerWindow()
    {
        // A credential accessor that runs on its own task while production waits must not consume
        // deadline time: no timer is registered until it has supplied a valid credential. The
        // accessor signals entry, then waits on an explicit gate; production is blocked in the
        // accessor's continuation WITHOUT blocking the test's synchronization context.
        var clock = new ManualWebTimeProvider();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accessorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = FetchOk();
        var options = Options(handler, timeProvider: clock, accessor: () =>
        {
            accessorEntered.TrySetResult(true);
            return Task.Run(async () => await gate.Task.ConfigureAwait(false)).GetAwaiter().GetResult();
        });
        var client = Create(options);
        var call = Task.Run(() => client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken));

        var entered = await Task.WhenAny(accessorEntered.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(accessorEntered.Task, entered);
        // Bounded diagnostic wait: give production time to (incorrectly) register a timer while
        // the credential is still gated; it must not do so.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(clock.RegisteredTimers);
        Assert.Equal(0, handler.SendCount);

        gate.TrySetResult(Credential);
        var timer = await clock.WaitForTimerAsync();
        Assert.Equal(StandardDeadline, timer.DueTime);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task LateNonSuccessResponse_AfterExpiry_ReturnsTimeoutNotHttpFailure()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        timer.Fire();
        Assert.True(timer.Fired);
        // Release a non-success response AFTER expiry; the body is never read.
        window.Handler.CompleteWith(HttpStatusCode.BadGateway, new StringContent(SecretSentinel));

        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task LateSuccessResponse_AfterExpiry_ReturnsTimeoutAndBodyIsNeverRead()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        timer.Fire();
        var body = window.CompleteWithGatedBody();

        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.Equal(0, body.ReadCount);
        Assert.NotNull(window.Content);
        await window.Content.WaitForDisposalAsync("late success after expiry");
        body.ReleaseAllPending();
    }

    [Fact]
    public async Task NonSuccessResponse_BeforeExpiry_IsImmediateHttpFailure()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        Assert.False(timer.Fired);
        window.Handler.CompleteWith(HttpStatusCode.Forbidden, new StringContent(SecretSentinel));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.HttpFailure);
        AssertSafeMessage(result);
        Assert.True(timer.Disposed);
    }

    /// <summary>
    /// The full late-return/late-throw fault matrix. For each named exception class we cover BOTH
    /// before expiry (normal classification) and after expiry (Timeout wins). A late unrelated
    /// InvalidOperationException is included: it is an unexpected host exception that the expired
    /// deadline must override.
    /// </summary>
    public static TheoryData<string, bool> FaultMatrix()
    {
        var data = new TheoryData<string, bool>();
        foreach (var key in Faults.Keys)
        {
            data.Add(key, false);
            data.Add(key, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(FaultMatrix))]
    public async Task LateThrow_AppliesCallerThenDeadlinePrecedence_BeforeInspectingType(
        string faultKey, bool expired)
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        if (expired) timer.Fire();
        Assert.True(window.Handler.Entered.IsCompleted); // positive control
        window.Handler.CompleteWith(Faults[faultKey]());

        WebToolResult<WebFetchData> result;
        if (faultKey == "UnexpectedInvalidOperation" && !expired)
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Contains(SecretSentinel, thrown.Message, StringComparison.Ordinal);
            return;
        }

        result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (expired)
        {
            AssertFailure(result, WebToolErrorKind.Timeout);
        }
        else
        {
            var expected = faultKey is "TimeoutMarker" or "NestedTimeoutMarker"
                ? WebToolErrorKind.Timeout
                : WebToolErrorKind.TransportFailure;
            AssertFailure(result, expected);
        }
        AssertSafeMessage(result);
    }

    private static readonly Dictionary<string, Func<Exception>> Faults = new(StringComparer.Ordinal)
    {
        ["UnrelatedOCE"] = () => new OperationCanceledException(SecretSentinel),
        ["IOException"] = () => new IOException(SecretSentinel),
        ["HttpRequestException"] = () => new HttpRequestException(SecretSentinel),
        ["TimeoutMarker"] = () => new WebToolTimeoutException(SecretSentinel, new Exception(SecretSentinel)),
        ["UnexpectedInvalidOperation"] = () => new InvalidOperationException(SecretSentinel),
        ["NestedTimeoutMarker"] = () => new HttpRequestException(
            SecretSentinel, new IOException(SecretSentinel, new WebToolTimeoutException(SecretSentinel))),
    };

    [Fact]
    public async Task LateThrow_NestedTimeoutMarker_BeforeExpiry_MapsToTimeout()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        Assert.False(timer.Fired);
        window.Handler.CompleteWith(Faults["NestedTimeoutMarker"]());
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task CallerCancellation_AlwaysWins_EvenAfterDeadlineExpired()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        timer.Fire(); // deadline expired first
        window.Caller.Cancel(); // caller cancels afterwards: caller still wins
        window.Handler.CompleteWithJson("{}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellation_BeforeDeadline_PropagatesCallerLinkedIdentity()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        Assert.False(timer.Fired);
        window.Caller.Cancel();
        window.Handler.CompleteWithJson("{}");

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(window.Caller.Token, thrown.CancellationToken);
    }

    [Fact]
    public async Task CallerCancellation_OverridesLateUnrelatedThrow()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        window.Caller.Cancel();
        window.Handler.CompleteWith(new IOException(SecretSentinel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellation_OverridesLateUnexpectedThrow()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        window.Caller.Cancel();
        window.Handler.CompleteWith(new InvalidOperationException(SecretSentinel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BodyReadTimeout_ExpiredDeadlineOverridesLateReadReturn()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "body read must be reached before the deadline fires");
        Assert.True(window.Handler.Entered.IsCompleted); // positive control for the handler phase

        timer.Fire();
        body.ReleaseRead(1); // late read return, non-zero bytes
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task BodyReadTimeout_ExpiredDeadlineOverridesLateReadThrowOfEveryNamedClass()
    {
        foreach (var key in Faults.Keys)
        {
            await using var window = await HeaderWindow.StartAsync();
            var timer = await window.RendezvousAsync();
            var body = window.CompleteWithGatedBody();
            await body.WaitForReadCountAsync(1, key);
            timer.Fire();
            body.ReleaseRead(Faults[key]());

            var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            AssertFailure(result, WebToolErrorKind.Timeout);
            AssertSafeMessage(result);
            Assert.True(body.Disposed);
        }
    }

    [Fact]
    public async Task BodyReadFault_BeforeExpiry_ClassifiesByType()
    {
        foreach (var (key, expected) in new[]
                 {
                     ("UnrelatedOCE", WebToolErrorKind.TransportFailure),
                     ("IOException", WebToolErrorKind.TransportFailure),
                     ("HttpRequestException", WebToolErrorKind.TransportFailure),
                     ("TimeoutMarker", WebToolErrorKind.Timeout),
                 })
        {
            await using var window = await HeaderWindow.StartAsync();
            var timer = await window.RendezvousAsync();
            Assert.False(timer.Fired);
            var body = window.CompleteWithGatedBody();
            await body.WaitForReadCountAsync(1, key);
            body.ReleaseRead(Faults[key]());
            var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            AssertFailure(result, expected);
            AssertSafeMessage(result);
            Assert.True(body.Disposed);
        }
    }

    [Fact]
    public async Task BodyReadUnexpectedFault_BeforeExpiry_MayPropagateForHostDiagnostics()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "unexpected read fault");
        var fault = new InvalidOperationException(SecretSentinel);
        body.ReleaseRead(fault);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task BodyReadCallerCancellation_WinsOverExpiredDeadlineAndLateRead()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "caller cancellation over late read");
        timer.Fire();
        window.Caller.Cancel();
        body.ReleaseRead(new IOException(SecretSentinel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task CompletedBodyExit_ValidJsonThenLateExpiry_ReturnsSuccess()
    {
        // Explicit, unambiguous positive control: read a valid body FULLY within the window, assert
        // the timer has NOT fired, then fire it and assert the parsed result is unchanged.
        var clock = new ManualWebTimeProvider();
        var bytes = Encoding.UTF8.GetBytes("{\"title\":\"T\",\"content\":\"C\",\"links\":[\"L\"]}");
        var stream = new GatedValidBodyStream(bytes);
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream));
        var client = Create(Options(handler, timeProvider: clock));
        var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        var timer = await clock.WaitForTimerAsync();
        await stream.WaitForCompletedReadAsync();

        Assert.True(stream.Completed); // positive control: full body read, including the EOF read
        Assert.False(timer.Fired);
        stream.ReleaseEof();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);

        timer.Fire();
        Assert.True(timer.Fired);
        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorKind);
        Assert.Equal(string.Empty, result.DisplayMessage);
        Assert.Equal("T", result.Data!.Title);
        Assert.Equal("C", result.Data.Content);
        Assert.Equal(new[] { "L" }, result.Data.Links);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task InfiniteDeadline_RegistersNoTimer_AndCallerCancellationStillWorks()
    {
        var clock = new ManualWebTimeProvider();
        var handler = new GatedWebHandler();
        using var caller = new CancellationTokenSource();
        var options = Options(handler, timeProvider: clock, deadline: Timeout.InfiniteTimeSpan);
        var client = Create(options);
        var call = client.FetchAsync("https://page.example.test", caller.Token);
        await handler.WaitForEntryAsync("infinite deadline");
        // Bounded diagnostic wait: assert that NO timer was registered for this call.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(clock.RegisteredTimers);
        Assert.True(handler.Entered.IsCompleted); // positive control

        handler.CompleteWithJson("{}");
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Empty(clock.RegisteredTimers);
    }

    [Fact]
    public async Task InfiniteDeadline_DoesNotDisableCallerCancellation()
    {
        var clock = new ManualWebTimeProvider();
        var handler = new GatedWebHandler();
        using var caller = new CancellationTokenSource();
        var client = Create(Options(handler, timeProvider: clock, deadline: Timeout.InfiniteTimeSpan));
        var call = client.FetchAsync("https://page.example.test", caller.Token);
        await handler.WaitForEntryAsync("infinite deadline caller cancellation");
        Assert.Empty(clock.RegisteredTimers);
        caller.Cancel();
        handler.CompleteWithJson("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BodyReadTimeout_WithOversizedStalledBody_ExpiredDeadlineStillWins()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        var body = window.CompleteWithGatedBody();
        await body.WaitForReadCountAsync(1, "oversized stalled body");
        timer.Fire();
        // A huge late chunk would otherwise be ResponseTooLarge; expired deadline must win.
        body.ReleaseRead(int.MaxValue);
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task HeaderTimeout_BeforeAnyBodyRead_ReturnsTimeout()
    {
        await using var window = await HeaderWindow.StartAsync();
        var timer = await window.RendezvousAsync();
        timer.Fire();
        window.Handler.CompleteWith(HttpStatusCode.OK, new StringContent("{}", Encoding.UTF8, "application/json"));
        var result = await window.Call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task Timeout_DisposesTimerLinkedTokensAndResponseDeterministically()
    {
        var clock = new ManualWebTimeProvider();
        var handler = new GatedWebHandler();
        var options = Options(handler, timeProvider: clock);
        var client = Create(options);
        var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        await handler.WaitForEntryAsync("disposal on timeout");
        var timer = await clock.WaitForTimerAsync();
        var body = new GatedWebStream();
        var content = new OwningGatedContent(body);
        timer.Fire();
        handler.CompleteWith(HttpStatusCode.OK, content);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.True(timer.Disposed);
        await content.WaitForDisposalAsync("timeout disposal");
        Assert.True(handler.Entered.IsCompleted);
        body.ReleaseAllPending();
    }
}

/// <summary>
/// Serves one complete JSON payload, then gates the terminal EOF read so the test can prove the
/// full body was read BEFORE the deadline fires.
/// </summary>
internal sealed class GatedValidBodyStream : Stream
{
    private readonly byte[] _bytes;
    private readonly TaskCompletionSource<bool> _completedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _served;
    private int _disposed;

    internal GatedValidBodyStream(byte[] bytes) => _bytes = bytes;

    internal bool Completed => _completedRead.Task.IsCompleted;
    internal bool Disposed => Volatile.Read(ref _disposed) != 0;

    internal async Task WaitForCompletedReadAsync()
    {
        var done = await Task.WhenAny(_completedRead.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(false);
        if (done != _completedRead.Task) throw new TimeoutException("Full body read was never reached.");
    }

    internal void ReleaseEof() => _eof.TrySetResult(0);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_served)
        {
            _served = true;
            _bytes.CopyTo(buffer);
            return _bytes.Length;
        }
        _completedRead.TrySetResult(true);
        return await _eof.Task.ConfigureAwait(false);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}
