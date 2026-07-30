using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

public class SubAgentManagerTests
{
    // ========================================================================
    // Fake IChatClient implementations
    // ========================================================================

    /// <summary>
    /// Fake chat client that returns a fixed text response, captures received
    /// messages and ChatOptions.Tools, and optionally blocks on a
    /// TaskCompletionSource so the sub-agent never completes until signaled.
    /// </summary>
    private sealed class CapturingClient : IChatClient
    {
        private readonly string _response;
        private readonly TaskCompletionSource<bool>? _gate;
        private readonly int? _inputTokens;
        private readonly int? _outputTokens;
        private readonly bool _throw;

        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
        public List<ChatOptions?> ReceivedOptions { get; } = [];

        public CapturingClient(
            string response = "Done.",
            TaskCompletionSource<bool>? gate = null,
            int? inputTokens = null,
            int? outputTokens = null,
            bool throwOnCall = false)
        {
            _response = response;
            _gate = gate;
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _throw = throwOnCall;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            ReceivedOptions.Add(options);

            if (_throw)
                throw new InvalidOperationException("Simulated client failure.");

            if (_gate is not null)
            {
                // Await either the gate or cancellation so the sub-agent
                // timeout (which cancels the token) can interrupt us.
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelTcs.TrySetResult(true)))
                {
                    var finished = await Task.WhenAny(_gate.Task, cancelTcs.Task).ConfigureAwait(false);
                    if (ReferenceEquals(finished, cancelTcs.Task))
                        throw new OperationCanceledException(cancellationToken);
                }
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _response));
            if (_inputTokens.HasValue)
            {
                response.Usage = new UsageDetails
                {
                    InputTokenCount = _inputTokens,
                    OutputTokenCount = _outputTokens ?? 0,
                    TotalTokenCount = (_inputTokens ?? 0) + (_outputTokens ?? 0)
                };
            }
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public IEnumerable<string> CapturedToolNames =>
            ReceivedOptions
                .Where(o => o?.Tools is not null)
                .SelectMany(o => o!.Tools!.OfType<AIFunction>().Select(f => f.Name))
                .ToList();

        public IReadOnlyList<string>? LastCapturedToolNames =>
            ReceivedOptions.Count == 0
                ? null
                : ReceivedOptions[ReceivedOptions.Count - 1]?.Tools?
                    .OfType<AIFunction>()
                    .Select(f => f.Name)
                    .ToList();
    }

    /// <summary>Client that always throws in GetResponseAsync (non-HttpRequestException).</summary>
    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Client whose GetResponseAsync throws <see cref="HttpRequestException"/>, which
    /// <c>CodingAgent.ExecuteAsync</c> deliberately re-throws. This makes the sub-agent's
    /// background task genuinely fault unless the runner's catch-all handles it.
    /// </summary>
    private sealed class HttpFaultingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("network exploded");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        // Disposal also throws, so any disposal path must be guarded too.
        public void Dispose() => throw new InvalidOperationException("dispose exploded");
    }

    /// <summary>
    /// Client that waits on a gate BEFORE recording anything, so a test can mutate the
    /// caller's request/options while the runner is suspended and still prove the runner
    /// used the values snapshotted at acceptance time.
    /// </summary>
    private sealed class GateFirstCapturingClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> _gate;
        private readonly string _response;

        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
        public List<ChatOptions?> ReceivedOptions { get; } = [];

        /// <summary>Signalled once GetResponseAsync has been entered (before the gate wait).</summary>
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GateFirstCapturingClient(TaskCompletionSource<bool> gate, string response = "done")
        {
            _gate = gate;
            _response = response;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await _gate.Task.ConfigureAwait(false);

            // Recorded only AFTER the test has mutated the request object.
            ReceivedMessages.Add(messages.ToList());
            ReceivedOptions.Add(options);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _response));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public IReadOnlyList<string>? LastCapturedToolNames =>
            ReceivedOptions.Count == 0
                ? null
                : ReceivedOptions[ReceivedOptions.Count - 1]?.Tools?
                    .OfType<AIFunction>()
                    .Select(f => f.Name)
                    .ToList();
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static AgentOptions ParentOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        // The parent capability ceiling clamps sub-agent capabilities, so the
        // default test parent is fully capable unless a test says otherwise.
        EnableBash = true,
    };

    private static SubAgentOptions DefaultOptions() => new();

    private static SubAgentManager CreateManager(
        SubAgentOptions? options = null,
        IChatClient? parentClient = null)
    {
        return new SubAgentManager(
            options ?? DefaultOptions(),
            parentClient ?? new CapturingClient(),
            ParentOptions(),
            logger: null);
    }

    // ========================================================================
    // 1. Lifecycle tests
    // ========================================================================

    [Fact]
    public async Task StartAsync_Returns_Immediately_With_Running_Status()
    {
        await using var manager = CreateManager();
        var info = await manager.StartAsync(new SubAgentRequest { Task = "do work" },
            TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Running, info.Status);
        Assert.False(string.IsNullOrEmpty(info.Id));
        Assert.NotEqual(default, info.StartedAt);
        Assert.Null(info.CompletedAt);
        Assert.Null(info.Summary);
    }

    [Fact]
    public async Task SubAgent_Completes_Populates_Summary_And_Tokens()
    {
        var client = new CapturingClient("The summary text.", inputTokens: 42, outputTokens: 10);
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "do work" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Single(results);
        var final = results[0];
        Assert.Equal(SubAgentStatus.Completed, final.Status);
        Assert.Equal("The summary text.", final.Summary);
        Assert.Equal(42, final.InputTokens);
        Assert.Equal(10, final.OutputTokens);
        Assert.NotNull(final.CompletedAt);
    }

    // ========================================================================
    // 2. Validation failure tests
    // ========================================================================

    [Fact]
    public async Task StartAsync_Blank_Task_Returns_Failed_NotTracked()
    {
        await using var manager = CreateManager();

        // null Task
        var infoNull = await manager.StartAsync(
            new SubAgentRequest { Task = null! },
            TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, infoNull.Id);
        Assert.Equal(SubAgentStatus.Failed, infoNull.Status);
        Assert.NotNull(infoNull.Error);

        // whitespace Task
        var infoWs = await manager.StartAsync(
            new SubAgentRequest { Task = "   " },
            TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, infoWs.Id);
        Assert.Equal(SubAgentStatus.Failed, infoWs.Status);

        // not tracked
        Assert.Empty(manager.GetStatus(string.Empty));
        Assert.Empty(manager.GetStatus());
    }

    [Fact]
    public async Task StartAsync_Unknown_Model_Returns_Failed_With_Valid_Ids()
    {
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        options.AvailableModels.Add(new SubAgentModelInfo("o1-mini"));
        options.ClientFactory = _ => new CapturingClient();

        await using var manager = CreateManager(options);
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "nonexistent" },
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(SubAgentStatus.Failed, info.Status);
        Assert.NotNull(info.Error);
        Assert.Contains("gpt-4", info.Error);
        Assert.Contains("o1-mini", info.Error);
        Assert.Empty(manager.GetStatus());
    }

    [Fact]
    public async Task StartAsync_Known_Model_Null_ClientFactory_Returns_Failed()
    {
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        // ClientFactory left null

        await using var manager = CreateManager(options);
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "gpt-4" },
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(SubAgentStatus.Failed, info.Status);
        Assert.NotNull(info.Error);
        Assert.Contains("ClientFactory is required", info.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.GetStatus());
    }

    [Fact]
    public async Task StartAsync_NonPositive_Request_Timeout_Returns_Failed()
    {
        await using var manager = CreateManager();

        // Zero
        var infoZero = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Timeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, infoZero.Id);
        Assert.Equal(SubAgentStatus.Failed, infoZero.Status);

        // Negative
        var infoNeg = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Timeout = TimeSpan.FromSeconds(-1) },
            TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, infoNeg.Id);
        Assert.Equal(SubAgentStatus.Failed, infoNeg.Status);

        Assert.Empty(manager.GetStatus());
    }

    // ========================================================================
    // 3. Catalog validation tests (constructor throws)
    // ========================================================================

    [Fact]
    public void Constructor_Duplicate_Model_Ids_Throws_ArgumentException()
    {
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        options.AvailableModels.Add(new SubAgentModelInfo("GPT-4")); // case-insensitive duplicate

        Assert.Throws<ArgumentException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    [Fact]
    public void Constructor_Blank_Model_Id_Throws_ArgumentException()
    {
        // The record constructor guards blank ids...
        Assert.Throws<ArgumentException>(() => new SubAgentModelInfo("  "));

        // ...and the manager independently validates the catalog. Build a bad entry
        // without running the record constructor guard.
        var bad = (SubAgentModelInfo)RuntimeHelpers
            .GetUninitializedObject(typeof(SubAgentModelInfo));
        var options = DefaultOptions();
        options.AvailableModels.Add(bad);

        Assert.Throws<ArgumentException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    // ========================================================================
    // 4. Constructor option validation tests
    // ========================================================================

    [Fact]
    public void Constructor_NonPositive_MaxConcurrent_Throws()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    [Fact]
    public void Constructor_NonPositive_MaxSummaryChars_Throws()
    {
        var options = DefaultOptions();
        options.MaxSummaryChars = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    [Fact]
    public void Constructor_NonPositive_DefaultTimeout_Throws()
    {
        var options = DefaultOptions();
        options.DefaultTimeout = TimeSpan.Zero;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    [Fact]
    public void Constructor_NonPositive_MaxTimeout_Throws()
    {
        var options = DefaultOptions();
        options.MaxTimeout = TimeSpan.Zero;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    [Fact]
    public void Constructor_DefaultTimeout_GreaterThan_MaxTimeout_Throws()
    {
        var options = DefaultOptions();
        options.DefaultTimeout = TimeSpan.FromMinutes(30);
        options.MaxTimeout = TimeSpan.FromMinutes(10);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubAgentManager(options, new CapturingClient(), ParentOptions()));
    }

    // ========================================================================
    // 5. Concurrency cap tests
    // ========================================================================

    [Fact]
    public async Task ConcurrencyCap_Second_Start_Blocks_Until_First_Finishes()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 1;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("done", gate: gate);
        await using var manager = CreateManager(options, client);

        var first = await manager.StartAsync(
            new SubAgentRequest { Task = "first" },
            TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentStatus.Running, manager.GetStatus(first.Id)[0].Status);

        // Start a second — it should block waiting for a slot.
        var secondStart = manager.StartAsync(
            new SubAgentRequest { Task = "second" },
            TestContext.Current.CancellationToken);

        // Give it time to potentially return (it should NOT).
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(secondStart.IsCompleted);

        // Release the first sub-agent.
        gate.SetResult(true);
        await manager.AwaitAsync(new[] { first.Id }, TestContext.Current.CancellationToken);

        // Now the second start should complete.
        var second = await secondStart;
        Assert.False(string.IsNullOrEmpty(second.Id));
        Assert.NotEqual(first.Id, second.Id);

        await manager.AwaitAsync(new[] { second.Id }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrencyCap_Cancelled_StartAsync_Aborts_Slot_Wait()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 1;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("done", gate: gate);
        await using var manager = CreateManager(options, client);

        // Occupy the only slot.
        var first = await manager.StartAsync(
            new SubAgentRequest { Task = "first" },
            TestContext.Current.CancellationToken);

        // Try to start a second with a token we will cancel.
        using var cts = new CancellationTokenSource();
        var secondStart = manager.StartAsync(
            new SubAgentRequest { Task = "second" }, cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(secondStart.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await secondStart);

        // The second sub-agent was never started — not tracked.
        Assert.Single(manager.GetStatus());
        Assert.Equal(first.Id, manager.GetStatus()[0].Id);

        // Clean up.
        gate.SetResult(true);
        await manager.AwaitAsync(new[] { first.Id }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // 6. Timeout test
    // ========================================================================

    [Fact]
    public async Task SubAgent_TimesOut_When_Not_Completing()
    {
        var options = DefaultOptions();
        options.DefaultTimeout = TimeSpan.FromMilliseconds(100);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("never", gate: gate);
        await using var manager = CreateManager(options, client);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "slow" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Single(results);
        var final = results[0];
        Assert.Equal(SubAgentStatus.TimedOut, final.Status);
        Assert.NotNull(final.CompletedAt);
        Assert.Null(final.Summary);

        // Release the gate so the background task can finish cleanly.
        gate.SetResult(true);
    }

    // ========================================================================
    // 7. AwaitAsync tests
    // ========================================================================

    [Fact]
    public async Task AwaitAsync_All_Returns_Results_Including_Failed_And_TimedOut()
    {
        // --- Timeout sub-agent ---
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toClient = new CapturingClient("never", gate: gate);
        var toOptions = DefaultOptions();
        toOptions.DefaultTimeout = TimeSpan.FromMilliseconds(100);
        await using var toManager = CreateManager(toOptions, toClient);

        var t = await toManager.StartAsync(new SubAgentRequest { Task = "slow" },
            TestContext.Current.CancellationToken);
        var toResults = await toManager.AwaitAsync(null, TestContext.Current.CancellationToken);
        Assert.Single(toResults);
        Assert.Equal(SubAgentStatus.TimedOut, toResults[0].Status);
        gate.SetResult(true);

        // --- Failed sub-agent (throwing client) ---
        var failClient = new ThrowingClient();
        await using var failManager = CreateManager(DefaultOptions(), failClient);
        var f = await failManager.StartAsync(new SubAgentRequest { Task = "boom" },
            TestContext.Current.CancellationToken);
        var failResults = await failManager.AwaitAsync(null, TestContext.Current.CancellationToken);
        Assert.Single(failResults);
        Assert.Equal(SubAgentStatus.Failed, failResults[0].Status);

        // --- Completed sub-agent ---
        await using var okManager = CreateManager(DefaultOptions(), new CapturingClient("ok"));
        var s = await okManager.StartAsync(new SubAgentRequest { Task = "ok" },
            TestContext.Current.CancellationToken);
        var okResults = await okManager.AwaitAsync(null, TestContext.Current.CancellationToken);
        Assert.Single(okResults);
        Assert.Equal(SubAgentStatus.Completed, okResults[0].Status);
    }

    [Fact]
    public async Task AwaitAsync_Explicit_Ids_Only_Awaits_Those()
    {
        await using var manager = CreateManager();

        var a = await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);
        var b = await manager.StartAsync(new SubAgentRequest { Task = "b" },
            TestContext.Current.CancellationToken);
        var c = await manager.StartAsync(new SubAgentRequest { Task = "c" },
            TestContext.Current.CancellationToken);

        var results = await manager.AwaitAsync(new[] { a.Id, b.Id },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var ids = results.Select(r => r.Id).ToHashSet();
        Assert.Contains(a.Id, ids);
        Assert.Contains(b.Id, ids);
        Assert.DoesNotContain(c.Id, ids);
    }

    [Fact]
    public async Task AwaitAsync_Unknown_Ids_Silently_Ignored()
    {
        await using var manager = CreateManager();
        var a = await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);

        var results = await manager.AwaitAsync(
            new[] { a.Id, "unknown-1", "unknown-2" },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(a.Id, results[0].Id);
    }

    [Fact]
    public async Task AwaitAsync_Caller_Cancellation_Throws_But_SubAgents_Keep_Running()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("blocked", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "blocked" },
            TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.AwaitAsync(new[] { info.Id }, cts.Token));

        // Sub-agent should still be running.
        var status = manager.GetStatus(info.Id);
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Running, status[0].Status);

        // Clean up.
        gate.SetResult(true);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AwaitAsync_Null_Ids_Awaits_Invocation_Time_Snapshot()
    {
        await using var manager = CreateManager();

        var a = await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);
        var b = await manager.StartAsync(new SubAgentRequest { Task = "b" },
            TestContext.Current.CancellationToken);

        var awaitTask = manager.AwaitAsync(null, TestContext.Current.CancellationToken);

        // Start a third sub-agent after the snapshot.
        var c = await manager.StartAsync(new SubAgentRequest { Task = "c" },
            TestContext.Current.CancellationToken);

        var results = await awaitTask;
        Assert.Equal(2, results.Count);
        var ids = results.Select(r => r.Id).ToHashSet();
        Assert.Contains(a.Id, ids);
        Assert.Contains(b.Id, ids);
        Assert.DoesNotContain(c.Id, ids);

        // Make sure c completes so disposal doesn't hang.
        await manager.AwaitAsync(new[] { c.Id }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // 8. GetStatus tests
    // ========================================================================

    [Fact]
    public async Task GetStatus_All_Returns_All_Tracked()
    {
        await using var manager = CreateManager();
        await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);
        await manager.StartAsync(new SubAgentRequest { Task = "b" },
            TestContext.Current.CancellationToken);

        var all = manager.GetStatus();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetStatus_Filtered_By_Id()
    {
        await using var manager = CreateManager();
        var a = await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);
        await manager.StartAsync(new SubAgentRequest { Task = "b" },
            TestContext.Current.CancellationToken);

        var filtered = manager.GetStatus(a.Id);
        Assert.Single(filtered);
        Assert.Equal(a.Id, filtered[0].Id);
    }

    [Fact]
    public void GetStatus_Unknown_Id_Returns_Empty()
    {
        var manager = new SubAgentManager(DefaultOptions(), new CapturingClient(), ParentOptions());
        Assert.Empty(manager.GetStatus("nonexistent"));
    }

    // ========================================================================
    // 9. CancelAllAsync tests
    // ========================================================================

    [Fact]
    public async Task CancelAllAsync_Waits_Until_All_Terminal()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("blocked", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);

        var a = await manager.StartAsync(new SubAgentRequest { Task = "a" },
            TestContext.Current.CancellationToken);
        var b = await manager.StartAsync(new SubAgentRequest { Task = "b" },
            TestContext.Current.CancellationToken);

        await manager.CancelAllAsync();

        var statuses = manager.GetStatus();
        Assert.Equal(2, statuses.Count);
        foreach (var s in statuses)
        {
            Assert.True(
                s.Status == SubAgentStatus.Cancelled ||
                s.Status == SubAgentStatus.Completed ||
                s.Status == SubAgentStatus.Failed ||
                s.Status == SubAgentStatus.TimedOut,
                $"Expected terminal status, got {s.Status}");
        }
    }

    [Fact]
    public async Task CancelAllAsync_Precedence_Completed_Lands_First()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(DefaultOptions(), client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);

        // Let it complete, then cancel.
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await manager.CancelAllAsync();

        var status = manager.GetStatus(info.Id);
        Assert.Single(status);
        // Completion landed first, so it must not be overwritten by cancellation.
        Assert.Equal(SubAgentStatus.Completed, status[0].Status);
        Assert.Equal("done", status[0].Summary);
    }

    // ========================================================================
    // 10. Truncation tests
    // ========================================================================

    [Fact]
    public async Task Summary_Truncation_At_MaxSummaryChars()
    {
        var options = DefaultOptions();
        options.MaxSummaryChars = 10;
        var longResponse = new string('x', 100);
        var client = new CapturingClient(longResponse);
        await using var manager = CreateManager(options, client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        var summary = results[0].Summary!;
        Assert.Equal(10, summary.Length);
        Assert.EndsWith("\u2026", summary);
        Assert.Equal(longResponse.Substring(0, 9), summary.Substring(0, 9));
    }

    [Fact]
    public async Task Task_Truncation_At_200_Chars()
    {
        await using var manager = CreateManager();
        var longTask = new string('y', 150) + new string('z', 150);
        var info = await manager.StartAsync(new SubAgentRequest { Task = longTask },
            TestContext.Current.CancellationToken);

        Assert.Equal(200, info.Task.Length);
        Assert.EndsWith("\u2026", info.Task);
        Assert.Equal(longTask.Substring(0, 199), info.Task.Substring(0, 199));

        // Shorter tasks are stored verbatim.
        var shortInfo = await manager.StartAsync(new SubAgentRequest { Task = "short task" },
            TestContext.Current.CancellationToken);
        Assert.Equal("short task", shortInfo.Task);
    }

    // ========================================================================
    // 11. Non-success result test
    // ========================================================================

    [Fact]
    public async Task NonSuccess_AgentResult_Maps_To_Failed()
    {
        var client = new ThrowingClient();
        await using var manager = CreateManager(DefaultOptions(), client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(SubAgentStatus.Failed, results[0].Status);
        Assert.NotNull(results[0].Error);
        Assert.Null(results[0].Summary);
    }

    // ========================================================================
    // 12. Case-insensitive model matching test
    // ========================================================================

    [Fact]
    public async Task Model_Matching_Is_Case_Insensitive()
    {
        var factoryClient = new CapturingClient("via-factory");
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        options.ClientFactory = _ => factoryClient;
        await using var manager = CreateManager(options, new CapturingClient("parent"));

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "GPT-4" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal("via-factory", results[0].Summary);
        Assert.Equal("gpt-4", results[0].Model);
    }

    // ========================================================================
    // 13. Tool allow-list verification tests
    // ========================================================================

    [Fact]
    public async Task SubAgent_Receives_Default_Tools_Bash_Absent()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        var tools = client.LastCapturedToolNames;
        Assert.NotNull(tools);
        Assert.DoesNotContain("execute_bash_command", tools);
        Assert.Contains("read_file", tools);
        Assert.Contains("glob", tools);
        Assert.Contains("grep", tools);
        Assert.DoesNotContain("write_file", tools);
        Assert.DoesNotContain("edit_file", tools);
        Assert.Contains("load_skill", tools);
        Assert.Contains("list_skills", tools);
    }

    [Fact]
    public async Task SubAgent_Receives_EnableBash_True()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", EnableBash = true },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        var tools = client.LastCapturedToolNames;
        Assert.NotNull(tools);
        Assert.Contains("execute_bash_command", tools);
    }

    [Fact]
    public async Task SubAgent_Receives_EnableFileWrites_True()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", EnableFileWrites = true },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        var tools = client.LastCapturedToolNames;
        Assert.NotNull(tools);
        Assert.Contains("write_file", tools);
        Assert.Contains("edit_file", tools);
    }

    [Fact]
    public async Task SubAgent_Receives_EnableFileOps_False()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", EnableFileOps = false },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        var tools = client.LastCapturedToolNames;
        Assert.NotNull(tools);
        Assert.DoesNotContain("read_file", tools);
        Assert.DoesNotContain("glob", tools);
        Assert.DoesNotContain("grep", tools);
        Assert.DoesNotContain("write_file", tools);
        Assert.DoesNotContain("edit_file", tools);
    }

    [Fact]
    public async Task SubAgent_Receives_Custom_SystemPrompt()
    {
        var client = new CapturingClient("done");
        await using var manager = CreateManager(parentClient: client);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", SystemPrompt = "You are a test sub-agent." },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        // The system prompt should appear in the first message of the first call.
        Assert.NotEmpty(client.ReceivedMessages);
        var firstCall = client.ReceivedMessages[0];
        var systemText = firstCall
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text)
            .FirstOrDefault();
        Assert.NotNull(systemText);
        Assert.Contains("You are a test sub-agent.", systemText);
    }

    // ========================================================================
    // 14. Eviction test
    // ========================================================================

    [Fact]
    public async Task Eviction_After_100_Completions_Oldest_Evicted()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 200;
        await using var manager = CreateManager(options, new CapturingClient("done"));

        var ids = new List<string>();
        for (var i = 0; i < 105; i++)
        {
            var info = await manager.StartAsync(new SubAgentRequest { Task = $"task-{i}" },
                TestContext.Current.CancellationToken);
            await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
            ids.Add(info.Id);
        }

        // No extra StartAsync: eviction must already have run on completion.
        var all = manager.GetStatus();
        Assert.True(all.Count <= 100, $"Expected at most 100 entries, got {all.Count}");

        // The oldest completed entries (first ones) should have been evicted.
        var remaining = all.Select(e => e.Id).ToHashSet();
        // The first 5 should be evicted (105 started, at most 100 remain).
        for (var i = 0; i < 5; i++)
        {
            Assert.DoesNotContain(ids[i], remaining);
        }
        // The last 100 should still be present.
        for (var i = 5; i < 105; i++)
        {
            Assert.Contains(ids[i], remaining);
        }
    }

    // ========================================================================
    // 15. Model/client selection tests
    // ========================================================================

    [Fact]
    public async Task No_Model_Uses_DefaultClient()
    {
        var defaultClient = new CapturingClient("from-default");
        var parentClient = new CapturingClient("from-parent");
        var options = DefaultOptions();
        options.DefaultClient = defaultClient;
        await using var manager = CreateManager(options, parentClient);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("from-default", results[0].Summary);
        // The default client should have received messages, not the parent.
        Assert.NotEmpty(defaultClient.ReceivedMessages);
        Assert.Empty(parentClient.ReceivedMessages);
    }

    [Fact]
    public async Task No_Model_DefaultClient_Null_Uses_Parent_Client()
    {
        var parentClient = new CapturingClient("from-parent");
        var options = DefaultOptions();
        // DefaultClient left null
        await using var manager = CreateManager(options, parentClient);

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("from-parent", results[0].Summary);
        Assert.NotEmpty(parentClient.ReceivedMessages);
    }

    [Fact]
    public async Task Model_Specified_Uses_ClientFactory()
    {
        var factoryClient = new CapturingClient("from-factory");
        var parentClient = new CapturingClient("from-parent");
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        string? factoryArg = null;
        options.ClientFactory = id =>
        {
            factoryArg = id;
            return factoryClient;
        };
        await using var manager = CreateManager(options, parentClient);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "gpt-4" },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("from-factory", results[0].Summary);
        Assert.Equal("gpt-4", factoryArg);
        Assert.NotEmpty(factoryClient.ReceivedMessages);
        Assert.Empty(parentClient.ReceivedMessages);
    }
    // ========================================================================
    // 16. Logger precedence
    // ========================================================================

    private sealed class StubLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        { }
    }

    [Fact]
    public async Task Logger_Explicit_Wins_And_Propagates_To_SubAgent()
    {
        var explicitLogger = new StubLogger();
        var parentLogger = new StubLogger();
        var parent = ParentOptions();
        parent.Logger = parentLogger;

        var manager = new SubAgentManager(DefaultOptions(), new CapturingClient(), parent, explicitLogger);
        await using var _ = manager;

        Assert.Same(explicitLogger, manager.ResolvedLogger);

        AgentOptions? captured = null;
        manager.OnSubAgentOptionsCreated = o => captured = o;
        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Same(explicitLogger, captured!.Logger);
    }

    [Fact]
    public async Task Logger_Falls_Back_To_ParentOptions_Then_NullLogger()
    {
        var parentLogger = new StubLogger();
        var parent = ParentOptions();
        parent.Logger = parentLogger;

        await using var manager1 = new SubAgentManager(
            DefaultOptions(), new CapturingClient(), parent, logger: null);
        Assert.Same(parentLogger, manager1.ResolvedLogger);

        AgentOptions? captured = null;
        manager1.OnSubAgentOptionsCreated = o => captured = o;
        var info = await manager1.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager1.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        Assert.Same(parentLogger, captured!.Logger);

        // Parent logger defaults to NullLogger.Instance when never set.
        await using var manager2 = new SubAgentManager(
            DefaultOptions(), new CapturingClient(), ParentOptions(), logger: null);
        Assert.Same(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            manager2.ResolvedLogger);
    }

    // ========================================================================
    // 17. Caller token after start
    // ========================================================================

    [Fact]
    public async Task Caller_Token_Cancelled_After_Start_Leaves_SubAgent_Running()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("blocked", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);

        using var cts = new CancellationTokenSource();
        var info = await manager.StartAsync(new SubAgentRequest { Task = "blocked" }, cts.Token);
        Assert.Equal(SubAgentStatus.Running, info.Status);

        cts.Cancel();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var status = manager.GetStatus(info.Id);
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Running, status[0].Status);

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // 18. Timeout clamping
    // ========================================================================

    [Fact]
    public async Task Request_Timeout_Above_MaxTimeout_Is_Clamped_Not_Rejected()
    {
        var options = DefaultOptions();
        // DefaultTimeout is far longer than MaxTimeout would allow for the request:
        // if the code ignored the clamped request timeout and used DefaultTimeout,
        // the sub-agent would not time out for 10 seconds.
        options.MaxTimeout = TimeSpan.FromSeconds(10);
        options.DefaultTimeout = TimeSpan.FromSeconds(10);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("never", gate: gate);
        await using var manager = CreateManager(options, client);

        // Lower MaxTimeout after construction so the clamp target is 200ms while
        // DefaultTimeout stays at 10s.
        options.MaxTimeout = TimeSpan.FromMilliseconds(200);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "slow", Timeout = TimeSpan.FromMinutes(5) },
            TestContext.Current.CancellationToken);

        // Not a validation failure: it was accepted and tracked.
        Assert.False(string.IsNullOrEmpty(info.Id));
        Assert.Equal(SubAgentStatus.Running, info.Status);

        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.TimedOut, results[0].Status);
        var elapsed = results[0].CompletedAt!.Value - results[0].StartedAt;
        Assert.True(elapsed < TimeSpan.FromSeconds(2),
            $"Expected the clamped 200ms timeout, but the sub-agent ran for {elapsed}");

        gate.SetResult(true);
    }

    // ========================================================================
    // 19. Disposal
    // ========================================================================

    /// <summary>
    /// Logger whose LogWarning throws. SubAgentManager's runner calls
    /// <c>_logger.LogWarning(...)</c> inside its <c>catch (Exception)</c> handler BEFORE it
    /// records the terminal status, so a throwing logger makes the runner <see cref="Task"/>
    /// itself fault — which is exactly the condition the unobserved-exception guards in
    /// CancelAllAsync/DisposeAsync exist to swallow.
    /// </summary>
    private sealed class FaultingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                throw new InvalidOperationException("logger exploded");
        }
    }

    /// <summary>
    /// Creates a manager whose sub-agent runner tasks genuinely fault, starts two of them,
    /// and disposes it. Kept in its own non-inlined method so the runner Tasks become
    /// unreachable and finalizable once it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SubAgentManager> StartFaultingSubAgentsAndDisposeAsync()
    {
        var manager = new SubAgentManager(
            DefaultOptions(),
            new HttpFaultingClient(),
            ParentOptions(),
            new FaultingLogger());

        // Two faulting runners: the try/catch around `await runner` in CancelAllCoreAsync
        // must observe BOTH faults and record each entry as terminal.
        await manager.StartAsync(new SubAgentRequest { Task = "boom-1" });
        await manager.StartAsync(new SubAgentRequest { Task = "boom-2" });

        // Give both runners time to reach their faulting catch handler.
        await Task.Delay(200);

        await manager.DisposeAsync();
        return manager;
    }

    [Fact]
    public async Task DisposeAsync_With_Throwing_SubAgent_Completes_Without_Unobserved_Exception()
    {
        var unobserved = 0;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // DisposeAsync must complete (not hang, not throw) even though every sub-agent
            // runner Task faults outright.
            var work = StartFaultingSubAgentsAndDisposeAsync();
            var finished = await Task.WhenAny(
                work,
                Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Same(work, finished);
            var manager = await work;

            // Every runner fault must have been observed and recorded as terminal.
            // If the try/catch around `await runner` is removed, the first fault escapes
            // CancelAllCoreAsync and the remaining entries stay Running.
            var all = manager.GetStatus();
            Assert.Equal(2, all.Count);
            Assert.All(all, e =>
            {
                Assert.Equal(SubAgentStatus.Failed, e.Status);
                Assert.NotNull(e.CompletedAt);
            });

            // Force finalization so any unobserved faulted Task raises the event.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task StartAsync_After_Dispose_Throws_ObjectDisposedException()
    {
        var manager = CreateManager();
        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await manager.StartAsync(new SubAgentRequest { Task = "work" },
                TestContext.Current.CancellationToken));
    }

    // ========================================================================
    // 20. Parent option propagation / sub-agent AgentOptions
    // ========================================================================

    [Fact]
    public async Task SubAgent_AgentOptions_Inherit_Parent_Settings()
    {
        var compactionClient = new CapturingClient("compact");
        var parent = ParentOptions();
        parent.MaxContextTokens = 12_345;
        parent.CompactionClient = compactionClient;
        parent.CompactionMaxTokens = 4_242;
        parent.ReasoningEffort = ReasoningEffort.High;
        parent.CustomTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "x", "parent_only_tool")
        };

        var options = DefaultOptions();
        options.MaxSteps = 7;
        var client = new CapturingClient("done");
        var manager = new SubAgentManager(options, client, parent);
        await using var _ = manager;

        AgentOptions? captured = null;
        manager.OnSubAgentOptionsCreated = o => captured = o;

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(12_345, captured!.MaxContextTokens);
        Assert.Same(compactionClient, captured.CompactionClient);
        Assert.Equal(4_242, captured.CompactionMaxTokens);
        Assert.Equal(ReasoningEffort.High, captured.ReasoningEffort);
        Assert.Equal(7, captured.MaxSteps);
        Assert.Equal(parent.WorkDirectory, captured.WorkDirectory);
        Assert.False(captured.AutoLoadWorkspaceInstructions);

        // CustomTools is always empty — no nested sub-agents.
        Assert.Empty(captured.CustomTools);
        var tools = client.LastCapturedToolNames;
        Assert.NotNull(tools);
        Assert.DoesNotContain("parent_only_tool", tools);
    }

    [Fact]
    public async Task SubAgent_Does_Not_AutoLoad_Workspace_Instructions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "subagent-autoload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "AGENTS.md"),
                "MAGIC_WORKSPACE_MARKER_12345",
                TestContext.Current.CancellationToken);

            var parent = new AgentOptions { WorkDirectory = dir };
            var client = new CapturingClient("done");
            await using var manager = new SubAgentManager(DefaultOptions(), client, parent);

            var info = await manager.StartAsync(new SubAgentRequest { Task = "work" },
                TestContext.Current.CancellationToken);
            await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

            Assert.NotEmpty(client.ReceivedMessages);
            foreach (var call in client.ReceivedMessages)
            {
                foreach (var msg in call)
                {
                    Assert.DoesNotContain("MAGIC_WORKSPACE_MARKER_12345", msg.Text ?? string.Empty);
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ========================================================================
    // 21. Running entries are never evicted
    // ========================================================================

    [Fact]
    public async Task Running_Entries_Are_Never_Evicted()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 200;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingClient = new CapturingClient("blocked", gate: gate);

        options.AvailableModels.Add(new SubAgentModelInfo("fast"));
        options.ClientFactory = _ => new CapturingClient("done");

        await using var manager = new SubAgentManager(options, blockingClient, ParentOptions());

        // Blocked sub-agent uses the (blocking) default client.
        var blocked = await manager.StartAsync(new SubAgentRequest { Task = "blocked" },
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 105; i++)
        {
            var info = await manager.StartAsync(
                new SubAgentRequest { Task = $"t-{i}", Model = "fast" },
                TestContext.Current.CancellationToken);
            await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        }

        var all = manager.GetStatus();
        Assert.Contains(all, e => e.Id == blocked.Id && e.Status == SubAgentStatus.Running);
        Assert.True(all.Count(e => e.Status != SubAgentStatus.Running) <= 100);

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { blocked.Id }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // 22. Mutating the request after start does not affect the run
    // ========================================================================

    [Fact]
    public async Task Mutating_Request_After_Start_Does_Not_Affect_Run()
    {
        // The runner is suspended at the very first instruction of its background task,
        // so the mutation below is guaranteed to happen BEFORE any per-run value is read.
        var runnerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new GateFirstCapturingClient(clientGate, "done");

        var parent = ParentOptions();
        parent.MaxContextTokens = 11_111;
        parent.CompactionMaxTokens = 2_222;
        parent.ReasoningEffort = ReasoningEffort.Low;
        var originalWorkDir = parent.WorkDirectory;

        var options = DefaultOptions();
        options.MaxSteps = 3;
        var originalMaxSteps = options.MaxSteps;
        await using var manager = new SubAgentManager(options, client, parent);

        AgentOptions? captured = null;
        manager.OnSubAgentOptionsCreated = o => captured = o;
        manager.OnSubAgentRunStarting = () =>
        {
            runnerEntered.TrySetResult(true);
            runnerGate.Task.GetAwaiter().GetResult();
        };

        var request = new SubAgentRequest
        {
            Task = "original task",
            EnableBash = false,
            EnableFileWrites = false,
            SystemPrompt = "ORIGINAL_PROMPT_MARKER"
        };
        var info = await manager.StartAsync(request, TestContext.Current.CancellationToken);

        // Wait until the runner is definitely suspended before mutating anything.
        await runnerEntered.Task;

        // Mutate the request AND the parent options — neither may affect the started run.
        request.Task = "mutated task";
        request.Timeout = TimeSpan.FromTicks(-1);
        request.EnableBash = true;
        request.EnableFileWrites = true;
        request.SystemPrompt = "MUTATED_PROMPT_MARKER";

        parent.MaxContextTokens = 99_999;
        parent.CompactionMaxTokens = 9_999;
        parent.ReasoningEffort = ReasoningEffort.High;
        options.MaxSteps = 999;

        // Release the runner, then the client.
        runnerGate.SetResult(true);
        clientGate.SetResult(true);

        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal("original task", results[0].Task);

        // Request values: originals only.
        Assert.NotNull(captured);
        Assert.False(captured!.EnableBash);
        Assert.False(captured.EnableFileWrites);
        Assert.Equal("ORIGINAL_PROMPT_MARKER", captured.SystemPrompt);

        // Parent values: captured at acceptance time.
        Assert.Equal(11_111, captured.MaxContextTokens);
        Assert.Equal(2_222, captured.CompactionMaxTokens);
        Assert.Equal(ReasoningEffort.Low, captured.ReasoningEffort);
        Assert.Equal(originalMaxSteps, captured.MaxSteps);
        Assert.Equal(originalWorkDir, captured.WorkDirectory);

        // And the wire-level view agrees.
        var userText = client.ReceivedMessages[0]
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text)
            .FirstOrDefault();
        Assert.Equal("original task", userText);
        var systemText = client.ReceivedMessages[0]
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text)
            .FirstOrDefault();
        Assert.NotNull(systemText);
        Assert.Contains("ORIGINAL_PROMPT_MARKER", systemText);
        Assert.DoesNotContain("MUTATED_PROMPT_MARKER", systemText);
        Assert.DoesNotContain("execute_bash_command", client.LastCapturedToolNames!);
        Assert.DoesNotContain("write_file", client.LastCapturedToolNames!);
    }

    // ========================================================================
    // 23. Dispose vs pending start race
    // ========================================================================

    [Fact]
    public async Task DisposeAsync_With_Blocked_Start_Does_Not_Start_New_SubAgent()
    {
        var options = DefaultOptions();
        options.MaxConcurrentSubAgents = 1;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("blocked", gate: gate);
        var manager = CreateManager(options, client);

        var first = await manager.StartAsync(new SubAgentRequest { Task = "first" },
            TestContext.Current.CancellationToken);

        // Second start blocks waiting for the only slot.
        var secondStart = manager.StartAsync(new SubAgentRequest { Task = "second" },
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(secondStart.IsCompleted);

        // Dispose concurrently while the second start is still blocked.
        var disposeTask = manager.DisposeAsync().AsTask();

        // Let the first sub-agent finish so the slot frees up.
        gate.SetResult(true);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await secondStart);

        // DisposeAsync must complete (not hang).
        var completed = await Task.WhenAny(disposeTask,
            Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(disposeTask, completed);
        await disposeTask;

        // The second sub-agent was never tracked or executed.
        var all = manager.GetStatus();
        Assert.DoesNotContain(all, e => e.Task == "second");
        Assert.Single(all);
        Assert.Equal(first.Id, all[0].Id);
        Assert.All(all, e => Assert.NotEqual(SubAgentStatus.Running, e.Status));
    }

    // ========================================================================
    // 21. Parent capability ceiling (clamp)
    // ========================================================================

    private static async Task<AgentOptions> CaptureSubAgentOptionsAsync(
        AgentOptions parent, SubAgentOptions options, SubAgentRequest request)
    {
        var manager = new SubAgentManager(options, new CapturingClient(), parent, logger: null);
        await using var _ = manager;

        AgentOptions? captured = null;
        manager.OnSubAgentOptionsCreated = o => captured = o;
        var info = await manager.StartAsync(request, TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task Ceiling_Clamps_Bash_When_Parent_Disables_It()
    {
        var parent = ParentOptions();
        parent.EnableBash = false;
        var options = DefaultOptions();
        options.DefaultEnableBash = true;

        var captured = await CaptureSubAgentOptionsAsync(
            parent, options, new SubAgentRequest { Task = "work", EnableBash = true });

        Assert.False(captured.EnableBash);
    }

    [Fact]
    public async Task Ceiling_Clamps_FileWrites_When_Parent_Disables_It()
    {
        var parent = ParentOptions();
        parent.EnableFileWrites = false;
        var options = DefaultOptions();
        options.DefaultEnableFileWrites = true;

        var captured = await CaptureSubAgentOptionsAsync(
            parent, options, new SubAgentRequest { Task = "work", EnableFileWrites = true });

        Assert.False(captured.EnableFileWrites);
    }

    [Fact]
    public async Task Ceiling_Clamps_FileOps_When_Parent_Disables_It()
    {
        var parent = ParentOptions();
        parent.EnableFileOps = false;
        var options = DefaultOptions();
        options.DefaultEnableFileOps = true;

        var captured = await CaptureSubAgentOptionsAsync(
            parent, options, new SubAgentRequest { Task = "work", EnableFileOps = true });

        Assert.False(captured.EnableFileOps);
    }

    [Fact]
    public async Task Ceiling_Clamps_Skills_When_Parent_Disables_It()
    {
        var parent = ParentOptions();
        parent.EnableSkills = false;
        var options = DefaultOptions();
        options.DefaultEnableSkills = true;

        var captured = await CaptureSubAgentOptionsAsync(
            parent, options, new SubAgentRequest { Task = "work", EnableSkills = true });

        Assert.False(captured.EnableSkills);
    }

    [Fact]
    public async Task Ceiling_Uses_Creation_Time_Snapshot_Not_Later_Mutation()
    {
        var parent = ParentOptions();
        parent.EnableBash = true;

        var manager = new SubAgentManager(DefaultOptions(), new CapturingClient(), parent, logger: null);
        await using var _ = manager;

        // Mutating the parent after construction must not affect the ceiling.
        parent.EnableBash = false;

        AgentOptions? captured = null;
        manager.OnSubAgentOptionsCreated = o => captured = o;
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", EnableBash = true },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.True(captured!.EnableBash);
    }

    [Fact]
    public async Task Ceiling_Allows_Capability_When_Parent_Enabled_And_Request_Overrides()
    {
        var parent = ParentOptions();
        parent.EnableBash = true;
        var options = DefaultOptions();
        options.DefaultEnableBash = false;

        var captured = await CaptureSubAgentOptionsAsync(
            parent, options, new SubAgentRequest { Task = "work", EnableBash = true });

        Assert.True(captured.EnableBash);
    }

    // ========================================================================
    // Owned client disposal (ClientFactory ownership)
    // ========================================================================

    /// <summary>Client that records how many times it has been disposed.</summary>
    private sealed class DisposalTrackingClient : IChatClient
    {
        private readonly string _response;
        private readonly bool _throwOnCall;
        private readonly bool _throwOnDispose;
        private readonly TaskCompletionSource<bool>? _gate;
        private int _disposeCount;

        public DisposalTrackingClient(
            string response = "done",
            bool throwOnCall = false,
            bool throwOnDispose = false,
            TaskCompletionSource<bool>? gate = null)
        {
            _response = response;
            _throwOnCall = throwOnCall;
            _throwOnDispose = throwOnDispose;
            _gate = gate;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool IsDisposed => DisposeCount > 0;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_throwOnCall)
                throw new InvalidOperationException("Simulated client failure.");

            if (_gate is not null)
            {
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelTcs.TrySetResult(true)))
                {
                    var finished = await Task.WhenAny(_gate.Task, cancelTcs.Task).ConfigureAwait(false);
                    if (ReferenceEquals(finished, cancelTcs.Task))
                        throw new OperationCanceledException(cancellationToken);
                }
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _response));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (_throwOnDispose)
                throw new InvalidOperationException("dispose exploded");
        }
    }

    private static SubAgentOptions ModelOptions(Func<string, IChatClient> factory)
    {
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("m1"));
        options.ClientFactory = factory;
        return options;
    }

    [Fact]
    public async Task Owned_Client_Disposed_Once_And_Before_Await_Returns()
    {
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.True(owned.IsDisposed);
        Assert.Equal(1, owned.DisposeCount);
    }

    [Fact]
    public async Task Owned_Client_Disposed_Once_On_Failure()
    {
        var owned = new DisposalTrackingClient(throwOnCall: true);
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Failed, results[0].Status);
        Assert.Equal(1, owned.DisposeCount);
    }

    [Fact]
    public async Task Owned_Client_Disposed_Once_On_Timeout()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new DisposalTrackingClient(gate: gate);
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1", Timeout = TimeSpan.FromMilliseconds(50) },
            TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.TimedOut, results[0].Status);
        Assert.Equal(1, owned.DisposeCount);
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task Owned_Client_Disposed_Once_On_CancelAll()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new DisposalTrackingClient(gate: gate);
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);
        await manager.CancelAllAsync();

        var results = manager.GetStatus(info.Id);
        Assert.Equal(SubAgentStatus.Cancelled, results[0].Status);
        Assert.Equal(1, owned.DisposeCount);
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task Owned_Client_Disposed_When_Timeout_Validation_Fails()
    {
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1", Timeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Failed, info.Status);
        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(1, owned.DisposeCount);
    }

    [Fact]
    public async Task Owned_Client_Disposed_When_Slot_Wait_Cancelled()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        options.MaxConcurrentSubAgents = 1;
        var blocking = new CapturingClient("blocked", gate: gate);
        await using var manager = new SubAgentManager(options, blocking, ParentOptions());

        await manager.StartAsync(new SubAgentRequest { Task = "blocker" }, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var pending = manager.StartAsync(new SubAgentRequest { Task = "work", Model = "m1" }, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, owned.DisposeCount);

        gate.SetResult(true);
    }

    [Fact]
    public async Task Owned_Client_Disposed_When_Manager_Disposed_During_Slot_Wait()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        options.MaxConcurrentSubAgents = 1;
        var blocking = new CapturingClient("blocked", gate: gate);
        var manager = new SubAgentManager(options, blocking, ParentOptions());

        await manager.StartAsync(new SubAgentRequest { Task = "blocker" }, TestContext.Current.CancellationToken);

        var pending = manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);

        await manager.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal(1, owned.DisposeCount);
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task Throwing_ClientFactory_Fails_Cleanly_With_No_Entry()
    {
        var options = ModelOptions(_ => throw new InvalidOperationException("factory boom"));
        await using var manager = CreateManager(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken));

        Assert.Empty(manager.GetStatus());

        // The slot was never taken: a subsequent start still succeeds.
        options.ClientFactory = _ => new DisposalTrackingClient();
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "ok", Model = "m1" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(string.Empty, info.Id);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Post_Slot_Startup_Failure_Rolls_Back_Slot_Entry_And_Client()
    {
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        options.MaxConcurrentSubAgents = 1;
        await using var manager = CreateManager(options);

        manager.OnSlotAcquiredBeforeStart = () => throw new InvalidOperationException("startup boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken));

        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(manager.GetStatus());

        // Slot was released: another start succeeds.
        manager.OnSlotAcquiredBeforeStart = null;
        var second = new DisposalTrackingClient();
        options.ClientFactory = _ => second;
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "again", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Caller_Owned_Clients_Are_Never_Disposed()
    {
        var defaultClient = new DisposalTrackingClient();
        var parentClient = new DisposalTrackingClient();
        var options = DefaultOptions();
        options.DefaultClient = defaultClient;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        Assert.Equal(0, defaultClient.DisposeCount);
        Assert.Equal(0, parentClient.DisposeCount);
    }

    [Fact]
    public async Task Throwing_Dispose_On_Owned_Client_Does_Not_Change_Outcome()
    {
        var owned = new DisposalTrackingClient(throwOnDispose: true);
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal(1, owned.DisposeCount);
    }

    [Fact]
    public async Task Each_Model_Selected_Run_Disposes_Its_Own_Client_Once()
    {
        var created = new List<DisposalTrackingClient>();
        var options = ModelOptions(_ =>
        {
            var c = new DisposalTrackingClient();
            lock (created) { created.Add(c); }
            return c;
        });
        await using var manager = CreateManager(options);

        for (var i = 0; i < 3; i++)
        {
            var info = await manager.StartAsync(
                new SubAgentRequest { Task = $"work-{i}", Model = "m1" }, TestContext.Current.CancellationToken);
            await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, created.Count);
        Assert.All(created, c => Assert.Equal(1, c.DisposeCount));
    }

    // ========================================================================
    // Additional integration tests for disposal contracts
    // (strengthened deterministic coverage)
    // ========================================================================

    [Fact]
    public async Task Owned_Client_Already_Disposed_Before_AwaitAsync_Returns()
    {
        // Deterministic ordering guarantee: the factory-created client is disposed
        // BEFORE AwaitAsync returns for the caller. We verify by having the fake set
        // a flag in Dispose() and asserting it is true once AwaitAsync completes.
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);

        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        // AwaitAsync must NOT return until disposal has happened.
        Assert.True(owned.IsDisposed, "Owned client should be disposed before AwaitAsync returns.");
        Assert.Equal(1, owned.DisposeCount);
        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
    }

    [Fact]
    public async Task Concurrent_Model_Selected_Runs_Dispose_Each_Client_Once()
    {
        // Start multiple model-selected sub-agents concurrently, await all, and verify
        // each factory-created client was disposed exactly once (3 instances, 3 disposals).
        var created = new List<DisposalTrackingClient>();
        var options = ModelOptions(_ =>
        {
            var c = new DisposalTrackingClient();
            lock (created) { created.Add(c); }
            return c;
        });
        await using var manager = CreateManager(options);

        var infos = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var info = await manager.StartAsync(
                new SubAgentRequest { Task = $"work-{i}", Model = "m1" }, TestContext.Current.CancellationToken);
            infos.Add(info.Id);
        }

        await manager.AwaitAsync(infos, TestContext.Current.CancellationToken);

        Assert.Equal(3, created.Count);
        // Three separate instances, each disposed exactly once.
        for (var i = 0; i < created.Count; i++)
            Assert.Equal(1, created[i].DisposeCount);
    }

    [Fact]
    public async Task NoModel_Path_Never_Disposes_Caller_Owned_Clients()
    {
        // Start a sub-agent WITHOUT a model (uses DefaultClient), complete it, dispose
        // the manager. Neither the default client nor the parent client is disposed.
        var defaultClient = new DisposalTrackingClient();
        var parentClient = new DisposalTrackingClient();
        var options = DefaultOptions();
        options.DefaultClient = defaultClient;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        Assert.Equal(0, defaultClient.DisposeCount);
        Assert.Equal(0, parentClient.DisposeCount);
    }

    [Fact]
    public async Task Manager_Disposal_Does_Not_Dispose_Caller_Owned_Clients()
    {
        // Start a no-model sub-agent, dispose the manager WITHOUT awaiting the sub-agent.
        // The default/parent clients must NOT be disposed.
        var parentClient = new DisposalTrackingClient();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = DefaultOptions();
        // DefaultClient that blocks so the sub-agent does not complete before dispose.
        var defaultClient = new DisposalTrackingClient(gate: gate);
        options.DefaultClient = defaultClient;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        var info = await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);

        // Dispose without awaiting the sub-agent.
        await manager.DisposeAsync();
        gate.TrySetResult(true);

        // The actual configured DefaultClient (and the parent client) are caller-owned
        // and must never be disposed by the manager.
        Assert.Equal(0, defaultClient.DisposeCount);
        Assert.Equal(0, parentClient.DisposeCount);

        // Verify the manager is disposed via StartAsync (throws ObjectDisposedException).
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "after" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Manager_Disposal_Does_Not_Dispose_Selected_Parent_Client()
    {
        // DefaultClient is null, so the no-model path selects the parent client passed to
        // the constructor. It must not be disposed by the manager.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentClient = new DisposalTrackingClient(gate: gate);
        var options = DefaultOptions();
        options.DefaultClient = null;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);

        await manager.DisposeAsync();
        gate.TrySetResult(true);

        Assert.Equal(0, parentClient.DisposeCount);
    }

    [Fact]
    public async Task PostSlot_Startup_Rollback_Via_OnSlotAcquiredBeforeStart()
    {
        // Set the hook to throw, attempt a start, catch the exception, then verify:
        // (a) a subsequent start succeeds (slot released),
        // (b) the factory-created client was disposed,
        // (c) GetStatus() returns empty (no orphaned entry).
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        options.MaxConcurrentSubAgents = 1;
        await using var manager = CreateManager(options);

        manager.OnSlotAcquiredBeforeStart = () => throw new InvalidOperationException("startup boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken));

        // (b) client disposed, (c) no orphaned entry
        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(manager.GetStatus());

        // (a) slot released → subsequent start succeeds
        manager.OnSlotAcquiredBeforeStart = null;
        var second = new DisposalTrackingClient();
        options.ClientFactory = _ => second;
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "again", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Factory_Throws_PreSlot_No_Client_No_Slot_Leak()
    {
        // Use a ClientFactory that throws. Verify the start fails with the factory's
        // exception, GetStatus() is empty, and no slot is leaked (a subsequent start
        // succeeds).
        var options = ModelOptions(_ => throw new InvalidOperationException("factory boom"));
        await using var manager = CreateManager(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken));

        Assert.Empty(manager.GetStatus());

        // Slot never acquired → subsequent start succeeds.
        options.ClientFactory = _ => new DisposalTrackingClient();
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "ok", Model = "m1" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(string.Empty, info.Id);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // Iteration-2 review fixes: async disposal path, no deadlock,
    // CTS rollback, strengthened caller-owned assertions.
    // ========================================================================

    /// <summary>
    /// Client that implements ONLY IAsyncDisposable (not IDisposable) so the async
    /// disposal path is provably exercised. Records its DisposeAsync invocation.
    /// </summary>
    private sealed class AsyncOnlyDisposalClient : IChatClient, IAsyncDisposable
    {
        private readonly string _response;
        private readonly TaskCompletionSource<bool>? _gate;
        private int _asyncDisposeCount;
        private int _syncDisposeCount;
        private readonly TaskCompletionSource<bool> _disposedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncOnlyDisposalClient(string response = "done", TaskCompletionSource<bool>? gate = null)
        {
            _response = response;
            _gate = gate;
        }

        // Tracks IAsyncDisposable.DisposeAsync invocations.
        public int AsyncDisposeCount => Volatile.Read(ref _asyncDisposeCount);
        // Tracks IDisposable.Dispose invocations — must stay 0 to prove the async path won.
        public int SyncDisposeCount => Volatile.Read(ref _syncDisposeCount);
        public Task Disposed => _disposedSignal.Task;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_gate is not null)
            {
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelTcs.TrySetResult(true)))
                {
                    var finished = await Task.WhenAny(_gate.Task, cancelTcs.Task).ConfigureAwait(false);
                    if (ReferenceEquals(finished, cancelTcs.Task))
                        throw new OperationCanceledException(cancellationToken);
                }
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _response));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        // IAsyncDisposable — the path DisposeOwnedClientAsync should use.
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncDisposeCount);
            _disposedSignal.TrySetResult(true);
            return default;
        }

        // IDisposable (required by IChatClient) — must NOT be called by the manager.
        public void Dispose() => Interlocked.Increment(ref _syncDisposeCount);
    }

    [Fact]
    public async Task AsyncDisposable_Path_Used_When_Client_Implements_Only_IAsyncDisposable()
    {
        // The client implements IAsyncDisposable but NOT IDisposable. The helper must
        // exercise the IAsyncDisposable.DisposeAsync path (not fall back to IDisposable).
        var owned = new AsyncOnlyDisposalClient();
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        // The Disposed task is completed only if DisposeAsync was actually called.
        Assert.True(owned.Disposed.IsCompleted);
        Assert.Equal(1, owned.AsyncDisposeCount);
        // The sync IDisposable.Dispose path must NOT have been used.
        Assert.Equal(0, owned.SyncDisposeCount);
    }

    [Fact]
    public async Task PreRun_Exit_Awaits_Async_Disposal_No_Deadlock()
    {
        // A pre-run exit path (non-positive timeout) must await the async disposal rather
        // than blocking synchronously. The client's DisposeAsync yields once before
        // recording, so if the helper blocked via GetAwaiter().GetResult() it would
        // deadlock (the continuation would never run on the same thread). A successful
        // return proves no sync-over-async.
        var owned = new YieldingAsyncDisposalClient();
        var options = ModelOptions(_ => owned);
        await using var manager = CreateManager(options);

        // Non-positive timeout triggers a pre-run exit AFTER the factory created the client.
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1", Timeout = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        // StartAsync returned → no deadlock. The client must have been disposed.
        Assert.Equal(SubAgentStatus.Failed, info.Status);
        Assert.Equal(string.Empty, info.Id);
        Assert.True(owned.Disposed.IsCompleted);
        Assert.Equal(1, owned.AsyncDisposeCount);
        Assert.Equal(0, owned.SyncDisposeCount);
    }

    /// <summary>
    /// Client whose DisposeAsync yields before recording, to detect sync-over-async
    /// deadlocks. Implements IAsyncDisposable only (no IDisposable).
    /// </summary>
    private sealed class YieldingAsyncDisposalClient : IChatClient, IAsyncDisposable
    {
        private int _asyncDisposeCount;
        private int _syncDisposeCount;
        private readonly TaskCompletionSource<bool> _disposedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AsyncDisposeCount => Volatile.Read(ref _asyncDisposeCount);
        public int SyncDisposeCount => Volatile.Read(ref _syncDisposeCount);
        public Task Disposed => _disposedSignal.Task;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async ValueTask DisposeAsync()
        {
            // Yield first so a synchronous GetAwaiter().GetResult() would deadlock
            // waiting for the continuation that never runs.
            await Task.Yield();
            Interlocked.Increment(ref _asyncDisposeCount);
            _disposedSignal.TrySetResult(true);
        }

        public void Dispose() => Interlocked.Increment(ref _syncDisposeCount);
    }

    [Fact]
    public async Task PostSlot_Rollback_Disposes_Both_Cts_Via_Hook()
    {
        // The OnSlotAcquiredBeforeStart hook throws AFTER the two CTSs are constructed
        // as locals but BEFORE the Entry is inserted. The catch path must dispose both
        // CTSs (and the owned client) and release the slot. We assert the slot is freed
        // (a subsequent start succeeds) and the client disposed.
        //
        // CTS disposal is internal; we assert the observable contract: slot released,
        // client disposed, no orphaned entry. A leaked slot would block the next start
        // when MaxConcurrentSubAgents == 1.
        var owned = new DisposalTrackingClient();
        var options = ModelOptions(_ => owned);
        options.MaxConcurrentSubAgents = 1;
        await using var manager = CreateManager(options);

        manager.OnSlotAcquiredBeforeStart = () => throw new InvalidOperationException("hook boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new SubAgentRequest { Task = "work", Model = "m1" }, TestContext.Current.CancellationToken));

        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(manager.GetStatus());

        // Slot must have been released. If either CTS leak or slot leak occurred, the
        // semaphore count would be wrong and this start would block (test would time out).
        manager.OnSlotAcquiredBeforeStart = null;
        var second = new DisposalTrackingClient();
        options.ClientFactory = _ => second;
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "again", Model = "m1" }, TestContext.Current.CancellationToken);
        var results = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(SubAgentStatus.Completed, results[0].Status);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Manager_Disposal_Does_Not_Dispose_DefaultClient_Reference()
    {
        // Strengthened: verify the ACTUAL DefaultClient instance passed to options is
        // not disposed after manager disposal. The fix uses a real retained reference.
        var defaultClient = new DisposalTrackingClient();
        var parentClient = new DisposalTrackingClient();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = DefaultOptions();
        options.DefaultClient = defaultClient;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);

        await manager.DisposeAsync();
        gate.TrySetResult(true);

        Assert.Equal(0, defaultClient.DisposeCount);
        Assert.Equal(0, parentClient.DisposeCount);
    }

    [Fact]
    public async Task Manager_Disposal_Does_Not_Dispose_Parent_Client_When_DefaultClient_Null()
    {
        // Parent-client path: DefaultClient is null, so the no-model path uses the
        // parent client passed to the constructor. It must not be disposed.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentClient = new DisposalTrackingClient(gate: gate);
        var options = DefaultOptions();
        options.DefaultClient = null;
        var manager = new SubAgentManager(options, parentClient, ParentOptions());

        await manager.StartAsync(new SubAgentRequest { Task = "work" }, TestContext.Current.CancellationToken);

        await manager.DisposeAsync();
        gate.TrySetResult(true);

        Assert.Equal(0, parentClient.DisposeCount);
    }

    [Fact]
    public async Task Cts_Allocations_Inside_Try_Slot_Released_Across_Repeated_Hook_Failures()
    {
        // Structural fix verification (iteration 3): both `new CancellationTokenSource()`
        // allocations are INSIDE the try block. If the second allocation threw (or any
        // statement between the first allocation and entry insertion throws), the catch
        // path must dispose the first (non-null) CTS and release the acquired slot.
        //
        // We cannot make `new CancellationTokenSource()` throw deterministically, but the
        // OnSlotAcquiredBeforeStart hook runs AFTER both allocations and BEFORE entry
        // insertion. Throwing it exercises the same catch path that a second-CTS-allocation
        // failure would. We repeat the failure more than MaxConcurrentSubAgents times to
        // prove the slot is released EVERY time — if any single failure leaked the slot,
        // the semaphore count would drop and a later start would block (test would time out).
        var options = ModelOptions(_ => new DisposalTrackingClient());
        options.MaxConcurrentSubAgents = 2;
        await using var manager = CreateManager(options);

        // Fail 3 starts (more than the 2 slots) — each must release its slot.
        manager.OnSlotAcquiredBeforeStart = () => throw new InvalidOperationException("boom");
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
                new SubAgentRequest { Task = $"fail-{i}", Model = "m1" }, TestContext.Current.CancellationToken));
            Assert.Empty(manager.GetStatus());
        }

        // All slots released: two concurrent starts must both proceed without blocking.
        manager.OnSlotAcquiredBeforeStart = null;
        var firstInfo = await manager.StartAsync(
            new SubAgentRequest { Task = "first", Model = "m1" }, TestContext.Current.CancellationToken);
        var secondInfo = await manager.StartAsync(
            new SubAgentRequest { Task = "second", Model = "m1" }, TestContext.Current.CancellationToken);

        // If a slot had leaked, the second start would block indefinitely.
        Assert.NotEqual(firstInfo.Id, secondInfo.Id);
        var results = await manager.AwaitAsync(new[] { firstInfo.Id, secondInfo.Id }, TestContext.Current.CancellationToken);
        Assert.All(results, r => Assert.Equal(SubAgentStatus.Completed, r.Status));
    }

    // ========================================================================
    // SubAgentChanged lifecycle event tests
    // ========================================================================

    /// <summary>
    /// Thread-safe, deterministically awaitable collector for <see cref="SubAgentManager.SubAgentChanged"/>.
    /// The manager fires the terminal event AFTER signalling <c>AwaitAsync</c> waiters, so tests must
    /// NOT assume an event has been delivered just because AwaitAsync/StartAsync returned. Every test
    /// awaits <see cref="WaitForCountAsync"/> before asserting, making assertions independent of
    /// scheduler timing. Events are stored in a <see cref="ConcurrentQueue{T}"/> because the runner
    /// thread writes while the test thread reads.
    /// </summary>
    private sealed class SubAgentEventCollector
    {
        private readonly ConcurrentQueue<SubAgentInfo> _events = new();
        private readonly object _sync = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _waiters = new();
        private int _count;

        /// <summary>The handler to subscribe to the event under test.</summary>
        public void Handle(SubAgentInfo info)
        {
            _events.Enqueue(info);
            var reached = Interlocked.Increment(ref _count);

            List<TaskCompletionSource<bool>>? toSignal = null;
            lock (_sync)
            {
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Count <= reached)
                    {
                        (toSignal ??= new List<TaskCompletionSource<bool>>()).Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            if (toSignal is null) return;
            foreach (var signal in toSignal)
                signal.TrySetResult(true);
        }

        /// <summary>Number of events delivered so far.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Snapshot of the delivered events, in delivery order.</summary>
        public IReadOnlyList<SubAgentInfo> Events => _events.ToArray();

        /// <summary>
        /// Waits until at least <paramref name="count"/> events have been delivered.
        /// Fails the test rather than hanging if delivery never happens.
        /// </summary>
        public async Task WaitForCountAsync(int count)
        {
            TaskCompletionSource<bool> signal;
            lock (_sync)
            {
                if (Volatile.Read(ref _count) >= count) return;
                signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, signal));
            }

            var finished = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(ReferenceEquals(finished, signal.Task),
                $"Timed out waiting for {count} SubAgentChanged event(s); only {Count} arrived.");
        }

        /// <summary>
        /// Asserts no further events arrive beyond <paramref name="expected"/>. Uses a short settle
        /// delay: a spurious extra event would have to be produced by an already-completed run, so
        /// any additional delivery lands well within this window.
        /// </summary>
        public async Task AssertNoMoreThanAsync(int expected)
        {
            await Task.Delay(250);
            Assert.Equal(expected, Count);
        }
    }

    [Fact]
    public async Task SubAgentChanged_Fires_Running_At_Start()
    {
        // A real model is configured so the emitted Model field is actually covered:
        // the default (null-model) path would make a Model assertion vacuous.
        var options = DefaultOptions();
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4"));
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        options.ClientFactory = _ => new CapturingClient("done", gate: gate);

        await using var manager = CreateManager(options);
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "do work", Model = "gpt-4" },
            TestContext.Current.CancellationToken);

        // Do NOT assume StartAsync returning implies the handler ran.
        await collector.WaitForCountAsync(1);

        var running = collector.Events[0];
        Assert.Equal(info.Id, running.Id);
        Assert.Equal("do work", running.Task);
        Assert.Equal("gpt-4", running.Model);
        Assert.Equal(SubAgentStatus.Running, running.Status);
        Assert.Null(running.CompletedAt);
        Assert.Null(running.Summary);

        // The sub-agent is still gated, so the terminal event cannot have raced ahead
        // and overwritten the Running payload asserted above.
        Assert.Equal(1, collector.Count);

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Fires_Terminal_Completed()
    {
        var client = new CapturingClient("summary", inputTokens: 7, outputTokens: 3);
        await using var manager = CreateManager(parentClient: client);
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        // Complete() signals AwaitAsync waiters BEFORE raising the terminal event,
        // so the event must be awaited explicitly.
        await collector.WaitForCountAsync(2);

        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Running, events[0].Status);

        var terminal = events[1];
        Assert.Equal(info.Id, terminal.Id);
        Assert.Equal(SubAgentStatus.Completed, terminal.Status);
        Assert.Equal("summary", terminal.Summary);
        Assert.Equal(7, terminal.InputTokens);
        Assert.Equal(3, terminal.OutputTokens);
        Assert.NotNull(terminal.CompletedAt);

        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Fires_Terminal_Failed()
    {
        await using var manager = CreateManager(DefaultOptions(), new ThrowingClient());
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Running, events[0].Status);
        Assert.Equal(SubAgentStatus.Failed, events[1].Status);
        Assert.Equal(info.Id, events[1].Id);
        Assert.NotNull(events[1].Error);
        Assert.Null(events[1].Summary);
        Assert.NotNull(events[1].CompletedAt);

        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Fires_Terminal_TimedOut()
    {
        var options = DefaultOptions();
        options.DefaultTimeout = TimeSpan.FromMilliseconds(100);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("never", gate: gate);
        await using var manager = CreateManager(options, client);
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "slow" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Running, events[0].Status);
        Assert.Equal(SubAgentStatus.TimedOut, events[1].Status);
        Assert.Equal(info.Id, events[1].Id);
        Assert.NotNull(events[1].Error);
        Assert.Null(events[1].Summary);
        Assert.NotNull(events[1].CompletedAt);

        gate.SetResult(true);
        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Fires_Terminal_Cancelled()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("blocked", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "blocked" },
            TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(1);
        Assert.Equal(SubAgentStatus.Running, collector.Events[0].Status);

        await manager.CancelAllAsync();
        await collector.WaitForCountAsync(2);

        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Cancelled, events[1].Status);
        Assert.Equal(info.Id, events[1].Id);
        Assert.NotNull(events[1].CompletedAt);

        gate.SetResult(true);
        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Per_Handler_Payload_Is_Isolated()
    {
        // Gate the run so the Running event is the only event in flight while the
        // payloads captured below are compared and mutated.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("done", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);

        var collectorA = new SubAgentEventCollector();
        var collectorB = new SubAgentEventCollector();
        manager.SubAgentChanged += collectorA.Handle;
        manager.SubAgentChanged += collectorB.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);

        await collectorA.WaitForCountAsync(1);
        await collectorB.WaitForCountAsync(1);

        var a = collectorA.Events[0];
        var b = collectorB.Events[0];
        Assert.NotSame(a, b);
        Assert.Equal(a.Id, b.Id);
        Assert.Null(b.Summary);

        // Mutate one handler's payload; the other's must remain unchanged.
        a.Summary = "mutated-by-a";
        Assert.Null(b.Summary);
        Assert.NotEqual("mutated-by-a", b.Summary);

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collectorA.WaitForCountAsync(2);
        await collectorB.WaitForCountAsync(2);

        // Terminal payloads are distinct instances too.
        Assert.NotSame(collectorA.Events[1], collectorB.Events[1]);
    }

    [Fact]
    public async Task SubAgentChanged_Throwing_Handler_Does_Not_Break_Others()
    {
        await using var manager = CreateManager();
        var throwCount = 0;
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += _ =>
        {
            Interlocked.Increment(ref throwCount);
            throw new InvalidOperationException("handler boom");
        };
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        // The throwing branch actually ran for both transitions...
        Assert.Equal(2, Volatile.Read(ref throwCount));
        // ...and the healthy subscriber still received both.
        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Running, events[0].Status);
        Assert.Equal(SubAgentStatus.Completed, events[1].Status);

        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Validation_Failure_Fires_Nothing()
    {
        await using var manager = CreateManager();
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "   " },
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(SubAgentStatus.Failed, info.Status);

        // Nothing was started, so nothing can arrive later either.
        await collector.AssertNoMoreThanAsync(0);
        Assert.Empty(manager.GetStatus());
    }

    [Fact]
    public async Task SubAgentChanged_Running_Fires_Before_Terminal()
    {
        // Gating the client guarantees the Running event is observed while the run is
        // still in flight — proving the ordering rather than inferring it after the fact.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingClient("done", gate: gate);
        await using var manager = CreateManager(DefaultOptions(), client);
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);

        await collector.WaitForCountAsync(1);
        Assert.Equal(SubAgentStatus.Running, collector.Events[0].Status);
        // Still gated: the terminal event provably has not fired yet.
        Assert.Equal(1, collector.Count);

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        var events = collector.Events;
        Assert.Equal(SubAgentStatus.Running, events[0].Status);
        Assert.Equal(SubAgentStatus.Completed, events[1].Status);
    }

    [Fact]
    public async Task SubAgentChanged_SingleSubscriber_Exactly_Two_Events()
    {
        await using var manager = CreateManager();
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        // Settle, then assert nothing extra arrived.
        await collector.AssertNoMoreThanAsync(2);
    }

    [Fact]
    public async Task SubAgentChanged_Second_Complete_Is_Ignored_No_Extra_Event()
    {
        // Deterministically invokes the private Complete(...) a SECOND time on the same
        // entry with a DIFFERENT terminal status. The `if (entry.Terminal != 0) return;`
        // guard must make the second call a no-op: no extra event, and the recorded
        // snapshot must still reflect the FIRST transition.
        //
        // Removing that guard makes this test fail three ways: a third event is raised,
        // the event payload reports Failed, and GetStatus reports the overwritten status.
        await using var manager = CreateManager(DefaultOptions(), new CapturingClient("first-wins"));
        var collector = new SubAgentEventCollector();
        manager.SubAgentChanged += collector.Handle;

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "work" },
            TestContext.Current.CancellationToken);
        await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);
        await collector.WaitForCountAsync(2);

        Assert.Equal(SubAgentStatus.Completed, collector.Events[1].Status);
        Assert.Equal("first-wins", collector.Events[1].Summary);
        var firstCompletedAt = collector.Events[1].CompletedAt;
        Assert.NotNull(firstCompletedAt);

        // Reach the live Entry the manager is tracking and re-invoke Complete on it.
        var entriesField = typeof(SubAgentManager)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entries = entriesField.GetValue(manager)!;
        var tryGet = entries.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { info.Id, null };
        Assert.True((bool)tryGet.Invoke(entries, args)!);
        var entry = args[1]!;

        var completeMethod = typeof(SubAgentManager)
            .GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Second Complete with a DIFFERENT status and payload — must be swallowed entirely.
        completeMethod.Invoke(manager, new object?[]
        {
            entry, SubAgentStatus.Failed, "second-summary", "second-error", null
        });

        // No third event, and the terminal payload already delivered is untouched.
        await collector.AssertNoMoreThanAsync(2);
        Assert.Equal(SubAgentStatus.Completed, collector.Events[1].Status);
        Assert.Equal("first-wins", collector.Events[1].Summary);
        Assert.Null(collector.Events[1].Error);
        Assert.Equal(firstCompletedAt, collector.Events[1].CompletedAt);

        // The manager's own state also still reflects the FIRST transition.
        var status = manager.GetStatus(info.Id);
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Completed, status[0].Status);
        Assert.Equal("first-wins", status[0].Summary);
        Assert.Null(status[0].Error);
    }
}
