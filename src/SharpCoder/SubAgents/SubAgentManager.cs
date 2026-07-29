using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder.SubAgents;

/// <summary>
/// Runtime that starts, tracks and awaits sub-agents. Flat design: sub-agents cannot
/// spawn further sub-agents.
/// </summary>
public sealed class SubAgentManager : IAsyncDisposable
{
    private const int MaxCompletedHistory = 100;

    /// <summary>
    /// Immutable per-run snapshot captured at acceptance time so later mutations of the
    /// caller's <see cref="SubAgentRequest"/> or <see cref="SubAgentOptions"/> cannot affect
    /// an already-started sub-agent.
    /// </summary>
    private sealed class RunSpec
    {
        public string Task = string.Empty;
        public TimeSpan Timeout;
        public string? SystemPrompt;
        public bool EnableBash;
        public bool EnableFileOps;
        public bool EnableFileWrites;
        public bool EnableSkills;
        public string? Model;
        public IChatClient Client = null!;

        /// <summary>Client created by the manager's ClientFactory and owned by the manager, if any.</summary>
        public IChatClient? OwnedClientForDisposal;
        public int MaxSteps;
        public int MaxSummaryChars;

        // Parent-agent configuration, captured at acceptance time so later mutation of the
        // parent AgentOptions cannot affect an already-started sub-agent.
        public string WorkDirectory = string.Empty;
        public int MaxContextTokens;
        public IChatClient? CompactionClient;
        public int? CompactionMaxTokens;
        public ReasoningEffort? ReasoningEffort;
    }

    private sealed class Entry
    {
        public string Id = string.Empty;
        public string Task = string.Empty;
        public string? Model;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? CompletedAt;
        public int Status; // SubAgentStatus as int
        public int Terminal; // 0 = running, 1 = terminal
        public string? Summary;
        public string? Error;
        public long? InputTokens;
        public long? OutputTokens;

        /// <summary>Fires when the sub-agent's own timeout elapses.</summary>
        public CancellationTokenSource? TimeoutCts;

        /// <summary>Fires when <see cref="CancelAllAsync"/> or disposal cancels this sub-agent.</summary>
        public CancellationTokenSource? CancelCts;

        public Task? Runner;
        public readonly TaskCompletionSource<bool> Completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubAgentInfo Snapshot()
        {
            lock (this)
            {
                return new SubAgentInfo
                {
                    Id = Id,
                    Task = Task,
                    Model = Model,
                    StartedAt = StartedAt,
                    CompletedAt = CompletedAt,
                    Status = (SubAgentStatus)Status,
                    Summary = Summary,
                    Error = Error,
                    InputTokens = InputTokens,
                    OutputTokens = OutputTokens
                };
            }
        }
    }

    private readonly SubAgentOptions _options;
    private readonly IChatClient _defaultClient;
    private readonly AgentOptions _parentOptions;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _counter;
    private int _disposed;

    /// <summary>
    /// Test seam: whether this manager has been disposed.
    /// Tests use this to verify that CodingAgent.DisposeAsync disposed the manager.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private int _pendingStarts;

    // Capability ceiling: snapshotted from the parent options at construction time.
    private readonly bool _parentEnableBashSnapshot;
    private readonly bool _parentEnableFileOpsSnapshot;
    private readonly bool _parentEnableFileWritesSnapshot;
    private readonly bool _parentEnableSkillsSnapshot;

    /// <summary>
    /// Test seam: invoked with the freshly built <see cref="AgentOptions"/> for each sub-agent,
    /// immediately before the sub-agent's <see cref="CodingAgent"/> is constructed.
    /// </summary>
    internal Action<AgentOptions>? OnSubAgentOptionsCreated { get; set; }

    /// <summary>
    /// Test seam: invoked at the very start of a sub-agent's background task, before any
    /// per-run configuration is consumed. Used to prove the run uses acceptance-time
    /// snapshots rather than the live request/options objects.
    /// </summary>
    internal Action? OnSubAgentRunStarting { get; set; }

    /// <summary>
    /// Test seam: invoked after a concurrency slot has been acquired but before the entry is
    /// tracked and the runner scheduled. Used to exercise post-slot startup rollback.
    /// </summary>
    internal Action? OnSlotAcquiredBeforeStart { get; set; }

    /// <summary>The logger resolved by the constructor (explicit logger, else parent options logger).</summary>
    internal ILogger ResolvedLogger => _logger;

    /// <summary>Creates a new sub-agent manager.</summary>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the model catalog is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when limits or timeouts are invalid.</exception>
    public SubAgentManager(SubAgentOptions options, IChatClient defaultClient, AgentOptions parentOptions, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _defaultClient = defaultClient ?? throw new ArgumentNullException(nameof(defaultClient));
        _parentOptions = parentOptions ?? throw new ArgumentNullException(nameof(parentOptions));
        _logger = logger ?? parentOptions.Logger ?? NullLogger.Instance;

        _parentEnableBashSnapshot = parentOptions.EnableBash;
        _parentEnableFileOpsSnapshot = parentOptions.EnableFileOps;
        _parentEnableFileWritesSnapshot = parentOptions.EnableFileWrites;
        _parentEnableSkillsSnapshot = parentOptions.EnableSkills;

        if (options.MaxConcurrentSubAgents < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentSubAgents must be at least 1.");
        if (options.MaxSummaryChars < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSummaryChars must be at least 1.");
        if (options.DefaultTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultTimeout must be positive.");
        if (options.MaxTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTimeout must be positive.");
        if (options.DefaultTimeout > options.MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultTimeout cannot exceed MaxTimeout.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in options.AvailableModels)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Id))
                throw new ArgumentException("AvailableModels contains an entry with a null or blank Id.", nameof(options));
            if (!seen.Add(model.Id))
                throw new ArgumentException($"Duplicate model id '{model.Id}' in AvailableModels.", nameof(options));
        }

        _slots = new SemaphoreSlim(options.MaxConcurrentSubAgents, options.MaxConcurrentSubAgents);
    }

    /// <summary>
    /// Starts a sub-agent. Blocks until a concurrency slot is available.
    /// Validation failures return a standalone failed <see cref="SubAgentInfo"/> with an empty Id.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has been disposed.</exception>
    public async Task<SubAgentInfo> StartAsync(SubAgentRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SubAgentManager));

        EvictHistory();

        if (string.IsNullOrWhiteSpace(request.Task))
            return ValidationFailure("Task is required.");

        // Capture the task text immediately so later mutation cannot affect the run.
        var taskText = request.Task;

        IChatClient client;
        IChatClient? ownedClient = null;
        string? modelId = null;
        var requestedModel = request.Model;
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            client = _options.DefaultClient ?? _defaultClient;
        }
        else
        {
            var model = _options.AvailableModels.FirstOrDefault(
                m => string.Equals(m.Id, requestedModel, StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                var valid = string.Join(", ", _options.AvailableModels.Select(m => m.Id));
                return ValidationFailure($"Unknown model '{requestedModel}'. Valid models: {valid}");
            }
            if (_options.ClientFactory is null)
                return ValidationFailure($"ClientFactory is required to resolve model '{model.Id}'");
            modelId = model.Id;
            // A throwing factory is a pre-slot failure: nothing was created, nothing to clean up.
            client = _options.ClientFactory(model.Id);
            ownedClient = client;
        }

        try
        {
            var timeout = _options.DefaultTimeout;
            var requestedTimeout = request.Timeout;
            if (requestedTimeout.HasValue)
            {
                if (requestedTimeout.Value <= TimeSpan.Zero)
                    return ValidationFailure("Timeout must be positive.");
                timeout = requestedTimeout.Value > _options.MaxTimeout ? _options.MaxTimeout : requestedTimeout.Value;
            }

            // Fully immutable snapshot of everything the run needs.
            var spec = new RunSpec
            {
                Task = taskText,
                Timeout = timeout,
                SystemPrompt = request.SystemPrompt,
                EnableBash = (request.EnableBash ?? _options.DefaultEnableBash) && _parentEnableBashSnapshot,
                EnableFileOps = (request.EnableFileOps ?? _options.DefaultEnableFileOps) && _parentEnableFileOpsSnapshot,
                EnableFileWrites = (request.EnableFileWrites ?? _options.DefaultEnableFileWrites) && _parentEnableFileWritesSnapshot,
                EnableSkills = (request.EnableSkills ?? _options.DefaultEnableSkills) && _parentEnableSkillsSnapshot,
                Model = modelId,
                Client = client,
                OwnedClientForDisposal = ownedClient,
                MaxSteps = _options.MaxSteps,
                MaxSummaryChars = _options.MaxSummaryChars,
                WorkDirectory = _parentOptions.WorkDirectory,
                MaxContextTokens = _parentOptions.MaxContextTokens,
                CompactionClient = _parentOptions.CompactionClient,
                CompactionMaxTokens = _parentOptions.CompactionMaxTokens,
                ReasoningEffort = _parentOptions.ReasoningEffort
            };

            Interlocked.Increment(ref _pendingStarts);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(SubAgentManager));

                await _slots.WaitAsync(ct).ConfigureAwait(false);

                if (Volatile.Read(ref _disposed) != 0)
                {
                    _slots.Release();
                    throw new ObjectDisposedException(nameof(SubAgentManager));
                }

                // Post-slot startup transaction: on any failure release the slot, dispose any
                // created CTSs and leave no orphaned entry behind.
                CancellationTokenSource? timeoutCts = null;
                CancellationTokenSource? cancelCts = null;
                Entry? entry = null;
                try
                {
                    timeoutCts = new CancellationTokenSource();
                    cancelCts = new CancellationTokenSource();

                    OnSlotAcquiredBeforeStart?.Invoke();

                    var id = "sub-" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    entry = new Entry
                    {
                        Id = id,
                        Task = SubAgentInfo.Truncate(spec.Task, 200),
                        Model = modelId,
                        StartedAt = DateTimeOffset.UtcNow,
                        Status = (int)SubAgentStatus.Running
                    };
                    entry.TimeoutCts = timeoutCts;
                    entry.CancelCts = cancelCts;
                    _entries[id] = entry;

                    // Capture the Running snapshot BEFORE the background task can transition it.
                    var initial = entry.Snapshot();

                    var capturedEntry = entry;
                    entry.Runner = Task.Run(() => RunAsync(capturedEntry, spec));

                    // Ownership handed to the runner.
                    timeoutCts = null;
                    cancelCts = null;
                    ownedClient = null;
                    return initial;
                }
                catch
                {
                    try { timeoutCts?.Dispose(); } catch (ObjectDisposedException) { }
                    try { cancelCts?.Dispose(); } catch (ObjectDisposedException) { }
                    if (entry != null)
                        _entries.TryRemove(entry.Id, out _);
                    try { _slots.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
                    throw;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pendingStarts);
            }
        }
        finally
        {
            if (ownedClient != null)
                await DisposeOwnedClientAsync(ownedClient).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes a manager-owned client, never throwing and never blocking synchronously.</summary>
    private async ValueTask DisposeOwnedClientAsync(IChatClient? client)
    {
        if (client is null) return;
        try
        {
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose sub-agent client");
        }
    }

    private static SubAgentInfo ValidationFailure(string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubAgentInfo
        {
            Id = string.Empty,
            Task = string.Empty,
            Status = SubAgentStatus.Failed,
            Error = message,
            Summary = null,
            StartedAt = now,
            CompletedAt = now
        };
    }

    private async Task RunAsync(Entry entry, RunSpec spec)
    {
        var timeoutCts = entry.TimeoutCts!;
        var cancelCts = entry.CancelCts!;
        CancellationTokenSource? linked = null;
        var status = SubAgentStatus.Failed;
        string? summary = null;
        string? error = null;
        UsageDetails? usage = null;
        try
        {
            // Test seam: lets a test suspend the runner before it reads anything.
            OnSubAgentRunStarting?.Invoke();

            // The caller's token is intentionally NOT linked: once started, the sub-agent's
            // lifetime is governed solely by its own timeout and CancelAllAsync.
            linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancelCts.Token);
            timeoutCts.CancelAfter(spec.Timeout);

            var subOptions = new AgentOptions
            {
                WorkDirectory = spec.WorkDirectory,
                MaxSteps = spec.MaxSteps,
                EnableBash = spec.EnableBash,
                EnableFileOps = spec.EnableFileOps,
                EnableFileWrites = spec.EnableFileWrites,
                EnableSkills = spec.EnableSkills,
                CustomTools = new List<AITool>(),
                AutoLoadWorkspaceInstructions = false,
                MaxContextTokens = spec.MaxContextTokens,
                CompactionClient = spec.CompactionClient,
                CompactionMaxTokens = spec.CompactionMaxTokens,
                ReasoningEffort = spec.ReasoningEffort,
                Logger = _logger
            };
            if (spec.SystemPrompt != null)
                subOptions.SystemPrompt = spec.SystemPrompt;

            OnSubAgentOptionsCreated?.Invoke(subOptions);

            var agent = new CodingAgent(spec.Client, subOptions);
            var result = await agent.ExecuteAsync(spec.Task, linked.Token).ConfigureAwait(false);

            if (!string.Equals(result.Status, "Success", StringComparison.Ordinal))
            {
                status = SubAgentStatus.Failed;
                summary = null;
                error = result.Message;
                usage = result.Usage;
            }
            else
            {
                status = SubAgentStatus.Completed;
                summary = SubAgentInfo.Truncate(result.Message ?? string.Empty, spec.MaxSummaryChars);
                error = null;
                usage = result.Usage;
            }
        }
        catch (OperationCanceledException)
        {
            // Determine which source caused the cancellation. Timeout wins when both fired
            // only if the timeout source is the one that is cancelled; otherwise it is a
            // manager-initiated cancellation.
            if (timeoutCts.IsCancellationRequested)
            {
                status = SubAgentStatus.TimedOut;
                error = "Sub-agent timed out.";
            }
            else
            {
                status = SubAgentStatus.Cancelled;
                error = "Sub-agent was cancelled.";
            }
            summary = null;
            usage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sub-agent {Id} failed", entry.Id);
            status = SubAgentStatus.Failed;
            summary = null;
            error = ex.Message;
            usage = null;
        }
        finally
        {
            // Dispose the owned client BEFORE signalling completion so awaiters can rely on
            // "await returned ⇒ owned client already disposed".
            await DisposeOwnedClientAsync(spec.OwnedClientForDisposal).ConfigureAwait(false);
            Complete(entry, status, summary, error, usage);

            try { linked?.Dispose(); } catch (ObjectDisposedException) { }
            try { timeoutCts.Dispose(); } catch (ObjectDisposedException) { }
            try { cancelCts.Dispose(); } catch (ObjectDisposedException) { }
            try { _slots.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Records the terminal outcome of a sub-agent. The first terminal transition wins and is
    /// never overwritten. Terminal fields and history eviction are applied BEFORE waiters are
    /// signalled so an <see cref="AwaitAsync"/> continuation always observes a consistent state.
    /// </summary>
    private void Complete(Entry entry, SubAgentStatus status, string? summary, string? error, UsageDetails? usage)
    {
        lock (entry)
        {
            if (entry.Terminal != 0)
                return;

            // 1. Terminal fields first.
            entry.Summary = summary;
            entry.Error = error;
            entry.InputTokens = usage?.InputTokenCount;
            entry.OutputTokens = usage?.OutputTokenCount;
            entry.CompletedAt = DateTimeOffset.UtcNow;
            entry.Status = (int)status;

            // 2. Terminal flag (guarded transition).
            Interlocked.Exchange(ref entry.Terminal, 1);
        }

        // 3. Enforce the history cap before waking waiters.
        EvictHistory();

        // 4. Signal AwaitAsync waiters.
        entry.Completion.TrySetResult(true);
    }

    private void EvictHistory()
    {
        var completed = _entries.Values
            .Where(e => Volatile.Read(ref e.Terminal) == 1)
            .OrderBy(e => e.CompletedAt ?? DateTimeOffset.MaxValue)
            .ToList();

        var excess = completed.Count - MaxCompletedHistory;
        for (var i = 0; i < excess; i++)
        {
            _entries.TryRemove(completed[i].Id, out _);
        }
    }

    /// <summary>Returns a snapshot of one or all tracked sub-agents. Unknown IDs return an empty list.</summary>
    public IReadOnlyList<SubAgentInfo> GetStatus(string? id = null)
    {
        if (id is null)
        {
            return _entries.Values
                .OrderBy(e => e.StartedAt)
                .Select(e => e.Snapshot())
                .ToList();
        }

        return _entries.TryGetValue(id, out var entry)
            ? new List<SubAgentInfo> { entry.Snapshot() }
            : new List<SubAgentInfo>();
    }

    /// <summary>
    /// Awaits the specified sub-agents (or all currently tracked ones) and returns their final snapshots.
    /// Never throws for failed or timed-out sub-agents.
    /// </summary>
    public async Task<IReadOnlyList<SubAgentInfo>> AwaitAsync(IEnumerable<string>? ids = null, CancellationToken ct = default)
    {
        List<Entry> targets;
        if (ids is null)
        {
            targets = _entries.Values.OrderBy(e => e.StartedAt).ToList();
        }
        else
        {
            targets = new List<Entry>();
            foreach (var id in ids)
            {
                if (id != null && _entries.TryGetValue(id, out var e))
                    targets.Add(e);
            }
        }

        foreach (var entry in targets)
        {
            await WaitForAsync(entry, ct).ConfigureAwait(false);
        }

        return targets.Select(e => e.Snapshot()).ToList();
    }

    private static async Task WaitForAsync(Entry entry, CancellationToken ct)
    {
        if (entry.Completion.Task.IsCompleted) return;
        if (!ct.CanBeCanceled)
        {
            await entry.Completion.Task.ConfigureAwait(false);
            return;
        }

        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelTcs.TrySetCanceled(ct)))
        {
            var finished = await Task.WhenAny(entry.Completion.Task, cancelTcs.Task).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
    }

    /// <summary>Cancels all running sub-agents and waits until each has reached a terminal status.</summary>
    public Task CancelAllAsync() => CancelAllCoreAsync();

    private async Task CancelAllCoreAsync()
    {
        var snapshot = _entries.Values.ToList();

        foreach (var entry in snapshot)
        {
            if (Volatile.Read(ref entry.Terminal) == 1) continue;
            try { entry.CancelCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to cancel sub-agent {Id}", entry.Id); }
        }

        foreach (var entry in snapshot)
        {
            var runner = entry.Runner;
            if (runner != null)
            {
                try { await runner.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Sub-agent {Id} ended with an exception", entry.Id);

                    // The runner Task itself faulted, so its own completion handler never
                    // ran and the entry would otherwise never reach a terminal status —
                    // making the await below hang forever. Record the fault here.
                    Complete(entry, SubAgentStatus.Failed, null, ex.Message, null);
                }
            }

            try { await entry.Completion.Task.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Sub-agent {Id} completion faulted", entry.Id); }
        }
    }

    /// <summary>Cancels running sub-agents and releases resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            // Cancel everything currently tracked; this frees concurrency slots so any
            // StartAsync still waiting for a slot can wake up and bail out.
            await CancelAllCoreAsync().ConfigureAwait(false);

            // Wait for in-flight StartAsync calls to observe the disposed flag and unwind.
            while (Volatile.Read(ref _pendingStarts) > 0)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            // Anything that slipped in before the flag was observed is cancelled here.
            await CancelAllCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while cancelling sub-agents during dispose");
        }

        _slots.Dispose();
    }
}
