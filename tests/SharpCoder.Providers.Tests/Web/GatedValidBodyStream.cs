using System.Threading.Channels;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Serves one complete JSON payload, then gates the terminal EOF read so the test can prove the
/// full body was read BEFORE the deadline fires. Every rendezvous is a TCS/channel signal.
/// </summary>
internal sealed class GatedValidBodyStream : Stream
{
    private readonly byte[] _bytes;
    private readonly TaskCompletionSource<bool> _completedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<int> _readArrivals = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private bool _served;
    private int _readCount;

    internal GatedValidBodyStream(byte[] bytes) => _bytes = bytes;

    internal bool Completed => _completedRead.Task.IsCompleted;
    internal bool Disposed => _disposed.Task.IsCompleted;
    internal int ReadCount => Volatile.Read(ref _readCount);

    internal async Task WaitForCompletedReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            // First arrival = complete payload; second arrival = gated terminal EOF read.
            _ = await _readArrivals.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            _ = await _readArrivals.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            await _completedRead.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Full body read was never reached.");
        }
    }

    internal void ReleaseEof() => _eof.TrySetResult(0);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _readCount);
        Assert.True(_readArrivals.Writer.TryWrite(count), "The read-arrival channel must remain open.");
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
        if (disposing)
        {
            _disposed.TrySetResult(true);
            _eof.TrySetResult(0);
        }
        base.Dispose(disposing);
    }
}
