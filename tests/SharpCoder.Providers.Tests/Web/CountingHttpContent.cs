using System.Net;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// A response content that records EVERY body-read entry point, including ReadAsStreamAsync's
/// parameterless and token overloads. Disposal is deterministic and observable through a TCS.
/// </summary>
internal sealed class CountingHttpContent : HttpContent
{
    private readonly TaskCompletionSource<bool> _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _readEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _body;
    private int _readAttempts;

    internal CountingHttpContent(string body) => _body = body;

    internal int ReadAttempts => Volatile.Read(ref _readAttempts);

    internal bool Disposed => _disposed.Task.IsCompleted;

    internal Task ReadEntered => _readEntered.Task;

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        Interlocked.Increment(ref _readAttempts);
        _readEntered.TrySetResult(true);
        return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_body)));
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readAttempts);
        _readEntered.TrySetResult(true);
        return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_body)));
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        Interlocked.Increment(ref _readAttempts);
        _readEntered.TrySetResult(true);
        return Task.CompletedTask;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = System.Text.Encoding.UTF8.GetByteCount(_body);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _disposed.TrySetResult(true);
        base.Dispose(disposing);
    }
}
