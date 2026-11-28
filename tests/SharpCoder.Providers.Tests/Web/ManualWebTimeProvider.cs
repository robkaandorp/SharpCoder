using System.Collections.Concurrent;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// A test-owned manual <see cref="TimeProvider"/> that captures EVERY timer the production code
/// registers. Deadline tests rendezvous on that exact timer (state, due time, period) and then
/// advance it explicitly — no narrow wall-clock timing margins are used.
/// </summary>
internal sealed class ManualWebTimeProvider : TimeProvider
{
    private readonly ConcurrentQueue<ManualWebTimer> _timers = new();
    private readonly List<ManualWebTimer> _all = new();
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _utcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp() => Interlocked.Read(ref _utcTicks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualWebTimer(this, callback, state, dueTime, period);
        _timers.Enqueue(timer);
        return timer;
    }

    /// <summary>
    /// Waits for the production code to register its one-shot deadline timer, then returns it.
    /// This is the rendezvous: advancing before this returns would be a vacuous test.
    /// </summary>
    internal async Task<ManualWebTimer> WaitForTimerAsync(TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (_timers.TryDequeue(out var timer)) return timer;
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Production code never registered its deadline timer.");
    }

    /// <summary>All registered timers, for exact-count assertions (e.g. infinite deadline registers none).</summary>
    internal IReadOnlyList<ManualWebTimer> RegisteredTimers
    {
        get { lock (_gate) return _all.ToArray(); }
    }

    /// <summary>Advances fake time without ever firing a timer; firing is explicit via the timer.</summary>
    internal void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
            Interlocked.Add(ref _utcTicks, by.Ticks);
        }
    }

    internal void Record(ManualWebTimer timer)
    {
        lock (_gate) _all.Add(timer);
    }
}

/// <summary>
/// A manual one-shot timer. <see cref="FireAsync"/> invokes the production callback exactly once
/// (the callback is what cancels the linked token), so no real elapsed time is involved.
/// </summary>
internal sealed class ManualWebTimer : ITimer
{
    private readonly ManualWebTimeProvider _owner;
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private int _fired;
    private int _disposed;

    internal ManualWebTimer(ManualWebTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _owner = owner;
        _callback = callback;
        _state = state;
        DueTime = dueTime;
        Period = period;
        _owner.Record(this);
    }

    /// <summary>The exact due time production requested.</summary>
    internal TimeSpan DueTime { get; private set; }

    /// <summary>The exact period production requested (must be infinite for a deadline timer).</summary>
    internal TimeSpan Period { get; private set; }

    /// <summary>The exact state object production supplied (the linked token source).</summary>
    internal object? State => _state;

    /// <summary>True once the callback has been invoked.</summary>
    internal bool Fired => Volatile.Read(ref _fired) != 0;

    /// <summary>True once disposal has been observed.</summary>
    internal bool Disposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Invokes the registered production callback on the caller's context, once. Callers must have
    /// rendezvoused on the gated handler/read operation before firing.
    /// </summary>
    internal void Fire()
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        _owner.Advance(DueTime);
        _callback(_state);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        DueTime = dueTime;
        Period = period;
        return true;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
