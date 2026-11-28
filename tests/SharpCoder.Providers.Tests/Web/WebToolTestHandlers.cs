using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Immutable, bounded snapshot of one recorded request. Only safe fields are kept: the test
/// asserts Authorization and body locally, never logs raw backend content.
/// </summary>
internal sealed record RecordedWebRequest(
    HttpMethod Method,
    string Uri,
    string Scheme,
    string Host,
    string AbsolutePath,
    string? Authorization,
    string[] AuthorizationValues,
    string? InheritedMarker,
    string ContentType,
    string Body);

/// <summary>
/// A local, never-network handler that records requests and returns a programmable response. Each
/// test owns its instance; disposal is deterministic and observable.
/// </summary>
internal class RecordingWebHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly ConcurrentQueue<RecordedWebRequest> _requests = new();

    internal RecordingWebHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>Optional exact response override, preserving response metadata for protocol tests.</summary>
    internal Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

    internal int SendCount;
    internal bool Disposed;

    /// <summary>All recorded requests, in arrival order.</summary>
    internal IReadOnlyList<RecordedWebRequest> Requests => _requests.ToArray();

    /// <summary>Factory for a handler returning a fixed success JSON body.</summary>
    internal static RecordingWebHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(CreateResponse(status, new StringContent(body, Encoding.UTF8, "application/json"))));

    /// <summary>Factory for a handler returning a fixed status with a supplied content instance.</summary>
    internal static RecordingWebHandler With(HttpStatusCode status, HttpContent content) =>
        new((_, _) => Task.FromResult(CreateResponse(status, content)));

    internal static HttpResponseMessage CreateResponse(HttpStatusCode status, HttpContent content) =>
        new(status) { Content = content };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SendCount);

        string body = string.Empty;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var auth = request.Headers.TryGetValues("Authorization", out var values) ? values.ToArray() : [];
        var snapshot = new RecordedWebRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.RequestUri?.Scheme ?? string.Empty,
            request.RequestUri?.Host ?? string.Empty,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            auth,
            request.Headers.TryGetValues("X-Inherited-Authorization", out var inherited) ? string.Join("|", inherited) : null,
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            body);
        _requests.Enqueue(snapshot);

        var response = ResponseFactory is not null && this is not GatedWebHandler
            ? ResponseFactory(request)
            : await SendAsyncCore(request, cancellationToken, _respond).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Extension point for gates. The default delegates to the programmed responder; a gated
    /// handler overrides this to rendezvous and await an explicit release.
    /// </summary>
    protected virtual Task<HttpResponseMessage> SendAsyncCore(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        respond(request, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A handler whose response is released only by an explicit gate. Deadline tests rendezvous on
/// <see cref="Entered"/> (proving the operation was actually reached) before advancing the exact
/// production timer or cancelling the caller, then release via <see cref="CompleteAsync"/>.
/// </summary>
internal sealed class GatedWebHandler : RecordingWebHandler
{
    private readonly TaskCompletionSource<HttpResponseMessage> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GatedWebHandler()
        : base((_, _) => Task.FromResult(RecordingWebHandler.CreateResponse(HttpStatusCode.OK, new StringContent("{}"))))
    {
    }

    /// <summary>Completes once the production send has actually entered this handler.</summary>
    internal Task Entered => _entered.Task;

    protected override async Task<HttpResponseMessage> SendAsyncCore(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        // Signal the rendezvous, then await an explicit release. The token is deliberately NOT
        // passed to the gate: this is a cancellation-IGNORING operation that production must never
        // abandon.
        _entered.TrySetResult(true);
        // The test observes Entered and configures any late disposers before this gate is released.
        // ResponseFactory, when present, is used only after release to create the late response.
        await _release.Task.ConfigureAwait(false);
        if (ResponseFactory is null) return _release.Task.Result;
        var response = ResponseFactory(request);
        await Task.Yield(); // deterministic rendezvous before HttpClient can dispose the response
        return response;
    }

    /// <summary>Waits with a bounded budget so a broken test fails with diagnostics instead of hanging.</summary>
    internal async Task WaitForEntryAsync(string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            await _entered.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Handler was never entered: {because}");
        }
    }

    /// <summary>Releases the awaited send with a response.</summary>
    internal void CompleteWith(HttpStatusCode status, HttpContent content) =>
        _release.TrySetResult(CreateResponse(status, content));

    /// <summary>Releases the awaited send with a thrown exception (late-throw cases).</summary>
    internal void CompleteWith(Exception fault) => _release.TrySetException(fault);

    /// <summary>Releases the awaited send with a plain success body.</summary>
    internal void CompleteWithJson(string body) =>
        CompleteWith(HttpStatusCode.OK, new StringContent(body, Encoding.UTF8, "application/json"));

    /// <summary>Releases the gate; a ResponseFactory may then construct the actual late response.</summary>
    internal void ReleaseWith(HttpStatusCode status) =>
        _release.TrySetResult(CreateResponse(status, new StringContent("{}")));

    /// <summary>
    /// Deterministic cleanup on failure paths: unblocks the awaited send so no test can leave a
    /// hanging production task behind.
    /// </summary>
    internal void ReleaseWithFallback() =>
        _release.TrySetResult(CreateResponse(HttpStatusCode.OK, new StringContent("{}", Encoding.UTF8, "application/json")));
}

/// <summary>
/// A content that owns a gated stream and records its OWN disposal. On a never-read body,
/// <see cref="StreamContent"/> never materializes a read stream from its source, so content
/// disposal — not the source stream's disposal — is the observable cleanup contract for that path.
/// </summary>
internal sealed class OwningGatedContent : StreamContent
{
    private readonly TaskCompletionSource<bool> _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal OwningGatedContent(GatedWebStream stream)
        : base(stream) => Stream = stream;

    internal GatedWebStream Stream { get; }

    internal bool ContentDisposed => _disposed.Task.IsCompleted;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _disposed.TrySetResult(true);
        base.Dispose(disposing);
    }

    internal async Task WaitForDisposalAsync(string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            await _disposed.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Content was never disposed: {because}");
        }
    }
}

/// <summary>
/// A success response stream whose reads are gated and observable. Used for body-read deadline,
/// streaming-cap, and disposal tests. <see cref="ReadEntered"/> proves production reached the read.
/// </summary>
internal sealed class GatedWebStream : Stream
{
    private readonly Channel<TaskCompletionSource<int>> _releases =
        Channel.CreateUnbounded<TaskCompletionSource<int>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<int> _readCounts = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private int _readCount;
    private readonly object _gate = new();
    private readonly HashSet<TaskCompletionSource<int>> _pending = new();

    internal int ReadCount => Volatile.Read(ref _readCount);
    internal int PendingReads { get { lock (_gate) return _pending.Count; } }

    internal bool Disposed => _disposed.Task.IsCompleted;

    internal async Task WaitForDisposalAsync(string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            await _disposed.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stream was never disposed: {because}");
        }
    }

    /// <summary>Completes once production has actually entered a read on this stream.</summary>
    internal Task ReadEntered => _entered.Task;

    /// <summary>
    /// Awaits the Nth read's arrival signal. The signal is published AFTER the release TCS is
    /// enqueued, so the test cannot release a read that production has not yet armed.
    /// </summary>
    internal async Task WaitForReadCountAsync(int count, string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        var observed = 0;
        try
        {
            while (observed < count)
            {
                observed = await _readCounts.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            await _entered.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stream read count {count} was never reached (actual {ReadCount}): {because}");
        }
    }

    /// <summary>Completes the pending read with N bytes of 'x' (or an exception).</summary>
    internal void ReleaseRead(int count)
    {
        Assert.True(_releases.Reader.TryRead(out var tcs), "No pending read to release.");
        tcs.TrySetResult(count);
    }

    /// <summary>Completes the pending read with a fault.</summary>
    internal void ReleaseRead(Exception fault)
    {
        Assert.True(_releases.Reader.TryRead(out var tcs), "No pending read to fault.");
        tcs.TrySetException(fault);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(_releases.Writer.TryWrite(release), "The gated-read channel must remain open.");
        lock (_gate) _pending.Add(release);
        var count = Interlocked.Increment(ref _readCount);
        // Deliberately ignore cancellationToken: production must await, not abandon, this read.
        _entered.TrySetResult(true);
        Assert.True(_readCounts.Writer.TryWrite(count), "The read-count channel must remain open.");
        return AwaitReleaseAsync(buffer, release);
    }

    private async ValueTask<int> AwaitReleaseAsync(Memory<byte> buffer, TaskCompletionSource<int> release)
    {
        var count = await release.Task.ConfigureAwait(false);
        lock (_gate) _pending.Remove(release);
        if (count > 0)
        {
            var fill = Math.Min(count, buffer.Length);
            buffer.Span[..fill].Fill((byte)'x');
            return fill;
        }
        return 0;
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
        if (disposing) _disposed.TrySetResult(true);
        base.Dispose(disposing);
    }

    /// <summary>
    /// Deterministic cleanup on failure paths: unblocks any awaited read so no test can leave a
    /// hanging production task or a live handler/stream operation behind.
    /// </summary>
    internal void ReleaseAllPending(Exception? fault = null)
    {
        while (_releases.Reader.TryRead(out var tcs))
        {
            if (fault is null) tcs.TrySetResult(0);
            else tcs.TrySetException(fault);
        }
    }
}

/// <summary>
/// A response stream that serves a fixed byte sequence in deterministic chunks, so the byte cap is
/// enforced at an observable read boundary.
/// </summary>
internal sealed class ChunkedWebStream : Stream
{
    private readonly byte[] _bytes;
    private readonly int _chunkSize;
    private readonly List<int> _requestedLengths = new();
    private int _offset;
    private int _readCount;
    private int _totalBytesRead;
    private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ChunkedWebStream(byte[] bytes, int chunkSize)
    {
        _bytes = bytes;
        _chunkSize = chunkSize;
    }

    internal int ReadCount => Volatile.Read(ref _readCount);
    internal int TotalBytesRead => Volatile.Read(ref _totalBytesRead);
    internal IReadOnlyList<int> RequestedLengths { get { lock (_requestedLengths) return _requestedLengths.ToArray(); } }
    internal bool Disposed => _disposed.Task.IsCompleted;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readCount);
        lock (_requestedLengths) _requestedLengths.Add(buffer.Length);
        if (_offset >= _bytes.Length) return ValueTask.FromResult(0);
        var count = Math.Min(Math.Min(_chunkSize, buffer.Length), _bytes.Length - _offset);
        _bytes.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        Interlocked.Add(ref _totalBytesRead, count);
        return ValueTask.FromResult(count);
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
        if (disposing) _disposed.TrySetResult(true);
        base.Dispose(disposing);
    }
}
