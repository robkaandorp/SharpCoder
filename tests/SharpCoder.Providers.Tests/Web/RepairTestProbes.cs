using System.Net;
using System.Text;
using System.Threading.Channels;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>Serializes public properties only, approximating serializers that ignore STJ attributes.</summary>
internal static class PublicPropertyTraversal
{
    internal static string SerializePublicProperties(object? value)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var builder = new StringBuilder();
        Visit(value, seen, builder);
        return builder.ToString();
    }

    private static void Visit(object? value, HashSet<object> seen, StringBuilder builder)
    {
        if (value is null) return;
        if (value is string text)
        {
            builder.Append(text);
            return;
        }
        if (!value.GetType().IsValueType && !seen.Add(value)) return;
        if (value is System.Collections.IEnumerable sequence and not string)
        {
            foreach (var item in sequence) Visit(item, seen, builder);
            return;
        }
        foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            var propertyValue = property.GetValue(value);
            builder.Append(property.Name).Append('=').Append(propertyValue?.GetType().FullName).Append(':');
            Visit(propertyValue, seen, builder);
        }
    }
}

/// <summary>A gated ReadAsStreamAsync operation that deliberately ignores cancellation.</summary>
internal sealed class GatedOpenContent : HttpContent
{
    private readonly TaskCompletionSource<Stream> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal Task Entered => _entered.Task;
    internal bool Disposed => Volatile.Read(ref _disposed) != 0;

    internal async Task WaitForOpenAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try { await _entered.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("ReadAsStreamAsync was never entered.");
        }
    }

    internal void Release(Stream stream) => _release.TrySetResult(stream);
    internal void Release(Exception fault) => _release.TrySetException(fault);
    internal void ReleaseFallback() => _release.TrySetResult(new MemoryStream(Encoding.UTF8.GetBytes("{}")));

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        _entered.TrySetResult(true);
        return _release.Task;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.CompletedTask;

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            ReleaseFallback();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Records physical release order and can throw at one selected cleanup point.</summary>
internal sealed class CleanupProbeResources
{
    internal List<string> Releases { get; } = new();
    internal HttpRequestMessage? Request { get; private set; }
    internal ThrowingResponse? Response { get; private set; }
    internal ThrowingContent? Content { get; private set; }
    internal ThrowingStream? Stream { get; private set; }

    internal GatedWebHandler CreateGatedHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new GatedWebHandler();
        // Install the release action before returning; completion creates and releases exactly once.
        handler.ResponseFactory = request =>
        {
            Request = request;
            Stream = new ThrowingStream(this, Encoding.UTF8.GetBytes(body));
            Content = new ThrowingContent(this, Stream);
            Response = new ThrowingResponse(this, status).SetContent(Content);
            return Response;
        };
        handler.ReleaseWith(status); // response is created after the test configures disposers
        return handler;
    }

    internal void Record(string name) { lock (Releases) Releases.Add(name); }
}

internal sealed class ThrowingResponse(CleanupProbeResources owner, HttpStatusCode status)
    : HttpResponseMessage(status)
{
    private int _disposeCalls;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public bool ThrowOnDispose { get; set; }
    public ThrowingResponse SetContent(ThrowingContent value)
    {
        Content = value;
        return this;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner.Record("response-start");
            Interlocked.Increment(ref _disposeCalls);
        }
        base.Dispose(disposing);
        if (ThrowOnDispose) throw new IOException("RESPONSE_CLEANUP_SENTINEL");
    }
}

internal sealed class ThrowingContent(CleanupProbeResources owner, Stream stream) : StreamContent(stream)
{
    private int _disposeCalls;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public bool ThrowOnDispose { get; set; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner.Record("content");
            Interlocked.Increment(ref _disposeCalls);
        }
        base.Dispose(disposing);
        if (ThrowOnDispose) throw new IOException("CONTENT_CLEANUP_SENTINEL");
    }
}

internal sealed class ThrowingStream(CleanupProbeResources owner, byte[] bytes) : MemoryStream(bytes)
{
    private int _disposeCalls;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public bool ThrowOnDispose { get; set; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner.Record("stream");
            Interlocked.Increment(ref _disposeCalls);
        }
        base.Dispose(disposing);
        if (ThrowOnDispose) throw new IOException("STREAM_CLEANUP_SENTINEL");
    }
}

/// <summary>
/// Returns the SAME stream on every open and owns it on content disposal. This makes the response's
/// disposal chain deterministic without relying on StreamContent's internal stream-cache behavior.
/// </summary>
internal sealed class PreopenedStreamContent : HttpContent
{
    private readonly Stream _stream;
    internal PreopenedStreamContent(Stream stream) => _stream = stream;

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream.Dispose();
        base.Dispose(disposing);
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}

/// <summary>Body stream that gates disposal after EOF has been read.</summary>
internal sealed class DisposalGatedBodyStream : Stream
{
    private readonly byte[] _bytes;
    private readonly TaskCompletionSource<bool> _disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(int Count, bool Returned)> _reads = Channel.CreateUnbounded<(int Count, bool Returned)>();
    private int _offset;

    internal DisposalGatedBodyStream(string body) => _bytes = Encoding.UTF8.GetBytes(body);
    internal Task DisposeEntered => _disposeEntered.Task;

    internal async Task WaitForReadReturnsAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            var returned = 0;
            while (returned < count)
            {
                var observation = await _reads.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (!observation.Returned) continue;
                returned++;
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Expected {count} completed reads.");
        }
    }

    internal void ReleaseDispose() => _disposeRelease.TrySetResult(true);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _bytes.Length - _offset;
        var count = Math.Min(buffer.Length, remaining);
        if (count > 0) _bytes.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        Assert.True(_reads.Writer.TryWrite((count, false)), "Read observation channel must stay open.");
        await Task.Yield();
        Assert.True(_reads.Writer.TryWrite((count, true)), "Read observation channel must stay open.");
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposeEntered.TrySetResult(true);
            // Synchronous gate: production must not reach parsing until the test releases disposal.
            _disposeRelease.Task.GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _bytes.Length;
    public override long Position { get => _offset; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
