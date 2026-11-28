using System.Collections.Concurrent;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Tracks test-owned handlers, streams, contents and other raw resources. Web tests use
/// <c>await using</c>, so xUnit v3 calls the sole <see cref="IAsyncDisposable"/> implementation
/// and disposal is deterministic on success and failure paths alike.
/// </summary>
internal sealed class DeterministicTestOwnedResources : IAsyncDisposable
{
    private readonly ConcurrentStack<IDisposable> _disposables = new();
    private readonly ConcurrentStack<IAsyncDisposable> _asyncDisposables = new();
    private readonly ConcurrentStack<Action> _cleanup = new();

    internal T Track<T>(T resource)
        where T : IDisposable
    {
        _disposables.Push(resource);
        return resource;
    }

    internal T TrackAsync<T>(T resource)
        where T : IAsyncDisposable
    {
        _asyncDisposables.Push(resource);
        return resource;
    }

    internal void TrackCleanup(Action cleanup) => _cleanup.Push(cleanup);

    public async ValueTask DisposeAsync()
    {
        while (_cleanup.TryPop(out var cleanup))
        {
            try { cleanup(); }
            catch { }
        }

        while (_asyncDisposables.TryPop(out var asyncDisposable))
        {
            try { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        while (_disposables.TryPop(out var disposable))
        {
            try { disposable.Dispose(); }
            catch { }
        }
    }
}
