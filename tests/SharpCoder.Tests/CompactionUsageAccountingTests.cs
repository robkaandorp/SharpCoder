using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoder;

namespace SharpCoder.Tests;

/// <summary>
/// Deterministic tests for Checkpoint 2 of the usage-accounting goal: compaction calls are recorded
/// inside <see cref="ContextCompactor"/> itself, exactly once per summary call, with
/// <see cref="UsageSource.Compaction"/> and the compaction client's model.
/// <para>
/// Covered here: threshold-triggered (auto) compaction, forced compaction, direct
/// <c>ForceCompactAsync</c> / <c>CompactOldestPercentAsync</c> calls by a host, chunked
/// summarisation (one recorded call per chunk), compaction appearing in the triggering execution's
/// <see cref="AgentResult.TokenUsage"/>, the session-null mid-loop overload, failed summary calls,
/// a throwing <see cref="AgentOptions.OnUsage"/> handler, model attribution for a distinct
/// <see cref="AgentOptions.CompactionClient"/>, and the absence of double counting.
/// </para>
/// <para>
/// There is no wall-clock timing anywhere in this file: no delay, sleep or timeout is used as a
/// synchronisation device.
/// </para>
/// </summary>
public class CompactionUsageAccountingTests
{
    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>
    /// Deterministic summarisation client: reports the same usage on every call, optionally fails
    /// the first call, and exposes its model through <see cref="ChatClientMetadata"/> when set.
    /// </summary>
    private sealed class CompactionFakeClient : IChatClient
    {
        private readonly string _summary;
        private readonly int _failFirstCalls;
        private readonly long? _inputPerCall;
        private readonly long? _outputPerCall;
        private readonly long? _cachedInputPerCall;
        private readonly long? _reasoningPerCall;
        private readonly string? _responseModelId;
        private int _calls;

        internal CompactionFakeClient(
            string summary = "Compacted summary text.",
            string? metadataModelId = null,
            string? responseModelId = null,
            long? inputPerCall = null,
            long? outputPerCall = null,
            long? cachedInputPerCall = null,
            long? reasoningPerCall = null,
            int failFirstCalls = 0)
        {
            _summary = summary;
            MetadataModelId = metadataModelId;
            _responseModelId = responseModelId;
            _inputPerCall = inputPerCall;
            _outputPerCall = outputPerCall;
            _cachedInputPerCall = cachedInputPerCall;
            _reasoningPerCall = reasoningPerCall;
            _failFirstCalls = failFirstCalls;
        }

        internal string? MetadataModelId { get; }

        internal int CallCount => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            if (index < _failFirstCalls)
                throw new InvalidOperationException($"summarisation call {index} failed");

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _summary))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = _responseModelId
            };

            if (_inputPerCall is not null || _outputPerCall is not null)
            {
                response.Usage = new UsageDetails
                {
                    InputTokenCount = _inputPerCall,
                    OutputTokenCount = _outputPerCall,
                    CachedInputTokenCount = _cachedInputPerCall,
                    ReasoningTokenCount = _reasoningPerCall
                };
            }

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Compaction never streams.");

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) && MetadataModelId is not null
                ? new ChatClientMetadata("compaction-fake", new Uri("https://example.invalid"), MetadataModelId)
                : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Logger that throws from <c>LogWarning</c> — exactly the write the OnUsage failure-reporting
    /// boundary performs — while every other level is ignored. This is the discriminating fake for
    /// the containment contract: without the nested best-effort guard the warning write would escape
    /// the recording boundary and fail otherwise successful compaction.
    /// </summary>
    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private int _warnings;

        /// <summary>How many warning writes were attempted (each one threw).</summary>
        internal int WarningWriteAttempts => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != Microsoft.Extensions.Logging.LogLevel.Warning) return;

            Interlocked.Increment(ref _warnings);
            throw new InvalidOperationException("logger exploded");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static AgentSession LargeSession(int messageCount, int charsPerMessage = 200)
    {
        var session = AgentSession.Create("compaction-usage-session");
        for (var i = 0; i < messageCount; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}: " + new string('a', charsPerMessage)));
        }

        return session;
    }

    private static AgentOptions CompactionOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        MaxContextTokens = 100_000,
        CompactionThreshold = 0.8,
        CompactionRetainRecent = 2,
        Logger = NullLogger.Instance
    };

    private static UsageEntry SingleEntry(UsageSummary summary, UsageSource source)
        => Assert.Single(summary.Entries, e => e.Source == source);

    private static long CallsForSource(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => (long)e.Usage.Calls);

    // ========================================================================
    // 1. Auto (threshold-triggered) compaction
    // ========================================================================

    [Fact]
    public async Task CompactIfNeededAsync_AutoCompaction_RecordsCompactionEntryIntoSessionAndOnUsage()
    {
        var client = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 1_200, outputPerCall: 40,
            cachedInputPerCall: 900, reasoningPerCall: 5);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        var options = CompactionOptions();
        options.MaxContextTokens = 1_000; // ~600 estimated tokens > 800? no — force via LastKnownContextTokens
        session.LastKnownContextTokens = 900; // above the 800 threshold

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted, "Auto compaction should have run.");
        Assert.Equal(1, client.CallCount);

        // Session usage: one Compaction entry with the compaction client's model and exact usage.
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal("compaction-model", entry.Model);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(1_200, entry.Usage.InputTokens);
        Assert.Equal(40, entry.Usage.OutputTokens);
        Assert.Equal(900, entry.Usage.CachedInputTokens);
        Assert.Equal(1, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(5, entry.Usage.ReasoningTokens);
        Assert.Equal(1, entry.Usage.ReasoningReportedCalls);

        // The legacy cumulative counters track the recorded call too.
        Assert.Equal(1_200, session.InputTokensUsed);
        Assert.Equal(40, session.OutputTokensUsed);
        Assert.Equal(1, session.Usage.Total.Calls);

        // OnUsage fired once, with the Compaction source, and saw the session already updated.
        var recorded = Assert.Single(events);
        Assert.Equal(UsageSource.Compaction, recorded.Source);
        Assert.Equal("compaction-model", recorded.Model);
        Assert.Null(recorded.SubAgentId);
        Assert.Equal(1, recorded.Usage.Calls);
        Assert.Equal(1_200, recorded.Usage.InputTokens);
    }

    [Fact]
    public async Task CompactIfNeededAsync_NoUsageReported_CountsCallWithZeroTokensAndNoReportedCounters()
    {
        // The summaries come back without any usage information at all.
        var client = new CompactionFakeClient(metadataModelId: "compaction-model");
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        session.LastKnownContextTokens = 900;
        var options = CompactionOptions();
        options.MaxContextTokens = 1_000;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(0, entry.Usage.InputTokens);
        Assert.Equal(0, entry.Usage.OutputTokens);
        Assert.Equal(0, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(0, entry.Usage.ReasoningReportedCalls);
        Assert.Equal(0, session.InputTokensUsed);
        Assert.Equal(0, session.OutputTokensUsed);
    }

    [Fact]
    public async Task CompactIfNeededAsync_NoMetadataAndNoResponseModel_RecordsNullModel()
    {
        var client = new CompactionFakeClient(inputPerCall: 10, outputPerCall: 1);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        session.LastKnownContextTokens = 900;
        var options = CompactionOptions();
        options.MaxContextTokens = 1_000;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Null(entry.Model);
        Assert.Equal(10, entry.Usage.InputTokens);
    }

    [Fact]
    public async Task CompactIfNeededAsync_NoMetadata_ResponseModelIdIsTheFallback()
    {
        var client = new CompactionFakeClient(responseModelId: "response-model", inputPerCall: 10, outputPerCall: 1);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        session.LastKnownContextTokens = 900;
        var options = CompactionOptions();
        options.MaxContextTokens = 1_000;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        Assert.Equal("response-model", SingleEntry(session.Usage, UsageSource.Compaction).Model);
    }

    // ========================================================================
    // 2. Host-initiated (direct) compaction — no host-side recording code
    // ========================================================================

    [Fact]
    public async Task ForceCompactAsync_DirectHostCall_RecordsCompactionEntryIntoSession()
    {
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 500, outputPerCall: 20);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        var options = CompactionOptions();

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal("compaction-model", entry.Model);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(500, entry.Usage.InputTokens);
        Assert.Equal(500, session.InputTokensUsed);
        Assert.Equal(20, session.OutputTokensUsed);
        Assert.Single(events);
        Assert.Equal(UsageSource.Compaction, events[0].Source);
    }

    [Fact]
    public async Task CompactOldestPercentAsync_DirectHostCall_RecordsCompactionEntryIntoSession()
    {
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 700, outputPerCall: 30);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(20);
        var options = CompactionOptions();

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.CompactOldestPercentAsync(session, options, 50, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal("compaction-model", entry.Model);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(700, entry.Usage.InputTokens);
        Assert.Equal(700, session.InputTokensUsed);
        Assert.Equal(30, session.OutputTokensUsed);
        Assert.Single(events);
    }

    [Fact]
    public async Task CompactIfNeededAsync_BelowThresholdAndNothingToCompact_RecordsNothing()
    {
        // No call is made, so no call may be recorded: the failure modes must stay silent.
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 10, outputPerCall: 1);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12); // no LastKnownContextTokens → estimate is well below threshold
        var options = CompactionOptions();

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.False(compacted);
        Assert.Equal(0, client.CallCount);
        Assert.Empty(session.Usage.Entries);
        Assert.Empty(events);
        Assert.Equal(0, session.InputTokensUsed);
    }

    // ========================================================================
    // 3. Failed summary calls
    // ========================================================================

    [Fact]
    public async Task CompactIfNeededAsync_FailingSummaryCall_RecordsZeroTokenCompactionCall()
    {
        // ForceCompactAsync propagates nothing (it catches), and the failed call is still recorded
        // as one Compaction call with zero tokens and no reported optional counters.
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", failFirstCalls: 1);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        var options = CompactionOptions();

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.False(compacted, "A failed summary call means compaction did not succeed.");
        Assert.Equal(1, client.CallCount);

        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(0, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(0, entry.Usage.ReasoningReportedCalls);

        var recorded = Assert.Single(events);
        Assert.Equal(UsageSource.Compaction, recorded.Source);
        Assert.Equal(1, recorded.Usage.Calls);
        Assert.Equal(0, session.InputTokensUsed);
    }

    // ========================================================================
    // 4. Chunked summarisation: one recorded call per chunk
    // ========================================================================

    [Fact]
    public async Task CompactIfNeededAsync_ChunkedSummarization_RecordsOneCallPerChunk()
    {
        // Small budget forces the chunked path: several model calls, one recorded call each.
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 10, outputPerCall: 1);
        var compactor = new ContextCompactor(client);
        var session = AgentSession.Create("chunked-usage-session");
        for (var i = 0; i < 12; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message-{i}: " + new string('x', 500)));
        }

        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            MaxContextTokens = 1_000,
            CompactionMaxTokens = 300,
            CompactionThreshold = 0.5,
            CompactionRetainRecent = 2,
            Logger = NullLogger.Instance
        };

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        Assert.True(client.CallCount > 1, $"Expected the chunked path to make several calls, got {client.CallCount}.");

        // Exactly one recorded call per chunk — the count matches the client's own call counter.
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(client.CallCount, entry.Usage.Calls);
        Assert.Equal(client.CallCount * 10, entry.Usage.InputTokens);
        Assert.Equal(client.CallCount * 1, entry.Usage.OutputTokens);
        Assert.Equal("compaction-model", entry.Model);
        Assert.Equal(session.InputTokensUsed, entry.Usage.InputTokens);

        // One OnUsage event per chunk as well.
        Assert.Equal(client.CallCount, events.Count);
        Assert.All(events, recorded =>
        {
            Assert.Equal(UsageSource.Compaction, recorded.Source);
            Assert.Equal("compaction-model", recorded.Model);
            Assert.Equal(1, recorded.Usage.Calls);
        });
    }

    [Fact]
    public async Task ForceCompactAsync_ChunkedWithFailingCalls_RecordsEveryAttemptExactlyOnce()
    {
        // substituteNullSummary: false makes the first failing chunk abort the operation; the
        // failed call is recorded, and the call count matches the client's.
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", failFirstCalls: 2);
        var compactor = new ContextCompactor(client);
        var session = AgentSession.Create("chunked-failure-session");
        for (var i = 0; i < 15; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message-{i}: " + new string('x', 500)));
        }

        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            CompactionMaxTokens = 200,
            CompactionRetainRecent = 5,
            Logger = NullLogger.Instance
        };

        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;

        var compacted = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.False(compacted);
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(client.CallCount, entry.Usage.Calls);
        Assert.Equal(client.CallCount, events.Count);
    }

    // ========================================================================
    // 5. The session-null mid-loop overload
    // ========================================================================

    [Fact]
    public async Task CompactIfNeededAsync_LiveMessagesWithNullSession_ReportsOnUsageOnly()
    {
        // With session == null there is nothing to update on a session; the recorder created for a
        // null-session execution reports to OnUsage (and to its own execution-local summary).
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 42, outputPerCall: 2);
        var compactor = new ContextCompactor(client);
        var messages = new List<ChatMessage> { new ChatMessage(ChatRole.System, "system") };
        for (var i = 0; i < 12; i++)
            messages.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"Message-{i}: " + new string('x', 400)));

        // No session means the threshold check uses the heuristic estimate from the live list
        // (~1_200 tokens from 12 x 400 chars), which exceeds a 1_000-token window at 0.8. A large
        // CompactionMaxTokens keeps the old slice inside a single (non-chunked) summary call, so
        // exactly one compaction call is recorded.
        var options = CompactionOptions();
        options.MaxContextTokens = 1_000;
        options.CompactionMaxTokens = 100_000;
        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;
        var recorder = new UsageRecorder(options, session: null, NullLogger.Instance);

        var compacted = await compactor.CompactIfNeededAsync(
            null, messages, options, recorder, TestContext.Current.CancellationToken);

        Assert.True(compacted);
        var entry = SingleEntry(recorder.Summary, UsageSource.Compaction);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(42, entry.Usage.InputTokens);

        var recorded = Assert.Single(events);
        Assert.Equal(UsageSource.Compaction, recorded.Source);
        Assert.Equal(42, recorded.Usage.InputTokens);
    }

    [Fact]
    public async Task CompactIfNeededAsync_ThrowingOnUsageHandler_NeverFailsCompaction()
    {
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 100, outputPerCall: 5);
        var compactor = new ContextCompactor(client);
        var session = LargeSession(12);
        session.LastKnownContextTokens = 90_000; // above the 80_000 threshold
        var options = CompactionOptions();

        var throwingHandlerCalls = 0;
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref throwingHandlerCalls);
            throw new InvalidOperationException("host handler boom");
        };

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(compacted, "A throwing OnUsage handler must not fail or alter compaction.");
        Assert.Equal(1, Volatile.Read(ref throwingHandlerCalls));

        // The call is recorded regardless: session state is unaffected by the handler failure.
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(100, entry.Usage.InputTokens);
        Assert.Equal(100, session.InputTokensUsed);
        Assert.Equal(5, session.OutputTokensUsed);
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);
    }

    [Fact]
    public async Task CompactIfNeededAsync_ThrowingOnUsageHandlerWithThrowingLogger_StillCompletes()
    {
        // MAJOR-1 on the compaction path: the recorder's failure-reporting boundary must not be able
        // to throw. The handler throws AND the configured logger throws from LogWarning, so without
        // the nested best-effort guard the logging exception would escape the summary call's
        // finally and turn successful compaction into a failed one (or an exception).
        var throwingLogger = new ThrowingLogger();
        var client = new CompactionFakeClient(metadataModelId: "compaction-model", inputPerCall: 100, outputPerCall: 5);
        var compactor = new ContextCompactor(client, throwingLogger);
        var session = LargeSession(12);
        session.LastKnownContextTokens = 90_000;
        var options = CompactionOptions();
        options.Logger = throwingLogger;

        var handlerThrowCount = 0;
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref handlerThrowCount);
            throw new InvalidOperationException("host handler boom");
        };

        var compacted = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        // No outcome change: compaction succeeded and produced its summary message.
        Assert.True(compacted, "A throwing OnUsage handler plus a throwing logger must not fail compaction.");
        Assert.Equal(1, Volatile.Read(ref handlerThrowCount));
        Assert.Equal(1, throwingLogger.WarningWriteAttempts);
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);

        // The call is still fully recorded.
        var entry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(100, entry.Usage.InputTokens);
        Assert.Equal(100, session.InputTokensUsed);
        Assert.Equal(5, session.OutputTokensUsed);
    }

    // ========================================================================
    // 6. Compaction during a CodingAgent execution
    // ========================================================================

    /// <summary>Main (agent) client for execution-driven compaction tests.</summary>
    private sealed class MainAgentClient : IChatClient
    {
        private readonly string _metadataModelId;
        private int _calls;

        internal MainAgentClient(string metadataModelId) => _metadataModelId = metadataModelId;

        internal int CallCount => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 1_000, OutputTokenCount = 10 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("main-fake", new Uri("https://example.invalid"), _metadataModelId)
                : null;

        public void Dispose() { }
    }

    private static AgentOptions ExecutionOptions()
    {
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            EnableBash = false,
            EnableFileOps = false,
            EnableSkills = false,
            AutoLoadWorkspaceInstructions = false,
            SystemPrompt = "You are a test agent.",
            MaxContextTokens = 200_000,
            CompactionThreshold = 0.5,
            CompactionRetainRecent = 1,
            Logger = NullLogger.Instance
        };
        return options;
    }

    [Fact]
    public async Task Execution_PreRunAutoCompaction_AppearsInAgentResultTokenUsageWithCompactionSource()
    {
        var compactionClient = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 900, outputPerCall: 33);
        var mainClient = new MainAgentClient("main-model");

        var options = ExecutionOptions();
        options.CompactionClient = compactionClient;
        options.MaxContextTokens = 1_000;

        var session = LargeSession(12);
        session.LastKnownContextTokens = 900; // above the 500 threshold

        await using var agent = new CodingAgent(mainClient, options);
        var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal(1, compactionClient.CallCount);
        Assert.Equal(1, mainClient.CallCount);

        // The compaction is a Compaction entry, carrying the COMPACTION client's model.
        var compactionEntry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal("compaction-model", compactionEntry.Model);
        Assert.Equal(1, compactionEntry.Usage.Calls);
        Assert.Equal(900, compactionEntry.Usage.InputTokens);
        Assert.Equal(33, compactionEntry.Usage.OutputTokens);

        // ...and the agent round is an Agent entry with the MAIN client's model — no mixing.
        var agentEntry = SingleEntry(session.Usage, UsageSource.Agent);
        Assert.Equal("main-model", agentEntry.Model);
        Assert.Equal(1_000, agentEntry.Usage.InputTokens);
        Assert.Equal(10, agentEntry.Usage.OutputTokens);

        // The execution snapshot carries both entries; the Agent totals are unaffected by compaction.
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(1_900, result.TokenUsage.Total.InputTokens);
        Assert.Equal(43, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(900, SingleEntry(result.TokenUsage, UsageSource.Compaction).Usage.InputTokens);
        Assert.Equal(1_000, SingleEntry(result.TokenUsage, UsageSource.Agent).Usage.InputTokens);

        // Session cumulative counters cover both calls.
        Assert.Equal(1_900, session.InputTokensUsed);
        Assert.Equal(43, session.OutputTokensUsed);
    }

    [Fact]
    public async Task Execution_PreRunAutoCompaction_UsesMainClientWhenNoCompactionClientConfigured()
    {
        // ContextCompactor falls back to the main client; the recorded model must then be the
        // main client's, because that is the client that actually answered.
        var mainClient = new MainAgentClient("main-model");
        var options = ExecutionOptions();
        options.MaxContextTokens = 1_000;

        var session = LargeSession(12);
        session.LastKnownContextTokens = 900;

        await using var agent = new CodingAgent(mainClient, options);
        var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal(2, mainClient.CallCount); // one compaction summary + one agent round
        var compactionEntry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal("main-model", compactionEntry.Model);
        Assert.Equal(1_000, compactionEntry.Usage.InputTokens);
        Assert.Equal(1, compactionEntry.Usage.Calls);

        var agentEntry = SingleEntry(session.Usage, UsageSource.Agent);
        Assert.Equal(1, agentEntry.Usage.Calls);
    }

    [Fact]
    public async Task Execution_CompactionDoesNotDoubleCount_TheSameCallNeverAppearsAsAgent()
    {
        var compactionClient = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 111, outputPerCall: 7);
        var mainClient = new MainAgentClient("main-model");
        var options = ExecutionOptions();
        options.CompactionClient = compactionClient;
        options.MaxContextTokens = 1_000;

        var session = LargeSession(12);
        session.LastKnownContextTokens = 900;

        await using var agent = new CodingAgent(mainClient, options);
        var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        // Every model call that was made is accounted for exactly once: one compaction call
        // (111 tokens) and one agent call (1_000 tokens) — 1_111 tokens, not 1_222.
        Assert.Equal(1, compactionClient.CallCount);
        Assert.Equal(1, mainClient.CallCount);
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(1_111, result.TokenUsage.Total.InputTokens);

        // The Agent bucket holds exactly the main client's single call, and the Compaction bucket
        // holds exactly the compaction client's single call.
        var agentEntry = SingleEntry(result.TokenUsage, UsageSource.Agent);
        Assert.Equal(1, agentEntry.Usage.Calls);
        Assert.Equal(1_000, agentEntry.Usage.InputTokens);
        Assert.DoesNotContain(result.TokenUsage.Entries, e => e.Model == "compaction-model" && e.Source == UsageSource.Agent);

        var compactionEntry = SingleEntry(result.TokenUsage, UsageSource.Compaction);
        Assert.Equal(1, compactionEntry.Usage.Calls);
        Assert.Equal(111, compactionEntry.Usage.InputTokens);
        Assert.DoesNotContain(result.TokenUsage.Entries, e => e.Model == "main-model" && e.Source == UsageSource.Compaction);

        Assert.Equal(2, session.Usage.Total.Calls);
        Assert.Equal(1_111, session.InputTokensUsed);
    }

    [Fact]
    public async Task Execution_MidLoopManualToolLoopCompaction_AppearsInAgentResultTokenUsage()
    {
        // The MAIN client's round usage is sized so the pre-run threshold check cannot trigger:
        // with MaxContextTokens = 4_000 and threshold 0.5, the pre-run check needs ~2_000+ tokens
        // but the session starts at only ~600 estimated tokens AND has no LastKnownContextTokens.
        // Round 1 then REPORTS 2_100 input tokens, which the mid-loop check turns into
        // max(2_100, estimate) >= 2_000 — so the ONLY compaction that can happen is the mid-loop
        // one, between the two agent rounds. That ordering is what the assertions below pin down.
        var compactionClient = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 250, outputPerCall: 12);
        var mainClient = new ScriptedMainStreamingClient(inputTokenCount: 2_100, outputTokenCount: 5, toolRounds: 1);
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            EnableBash = false,
            EnableFileOps = false,
            EnableSkills = false,
            AutoLoadWorkspaceInstructions = false,
            SystemPrompt = "You are a test agent.",
            ShowToolCallsInStream = true,
            MaxContextTokens = 4_000,
            CompactionThreshold = 0.5,
            CompactionRetainRecent = 1,
            Logger = NullLogger.Instance,
            CompactionClient = compactionClient
        };
        options.CustomTools = [AIFunctionFactory.Create((string id) => $"tool-result:{id}", "do_thing")];

        var session = AgentSession.Create("mid-loop-compaction-usage");
        for (var i = 0; i < 3; i++)
            session.MessageHistory.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"History {i}: " + new string('h', 200)));

        // Below the pre-run threshold: no LastKnownContextTokens and a small estimated context.
        Assert.Equal(0, session.LastKnownContextTokens);

        var observedOrder = new List<string>();
        options.OnUsage = usageEvent => observedOrder.Add(usageEvent.Source.ToString());

        await using var agent = new CodingAgent(mainClient, options);

        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, "do it", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.Completed) result = update.Result;
        }

        Assert.NotNull(result);
        Assert.Equal("Success", result!.Status);

        // Exactly one compaction happened, and it was the MID-LOOP one: the pre-run check ran before
        // any report and the session was below its threshold then.
        Assert.Equal(1, compactionClient.CallCount);
        Assert.Equal(2, mainClient.StreamingCallCount);

        // Ordering, not just presence: Agent round 1 → Compaction → Agent round 2. If compaction had
        // happened in the pre-run check (or not at all), this sequence would not match.
        Assert.Equal(new[] { "Agent", "Compaction", "Agent" }, observedOrder);

        // Compaction calls are Compaction entries in the execution snapshot, with the compaction
        // client's model, and they are not counted under Agent.
        var compactionEntry = SingleEntry(result.TokenUsage, UsageSource.Compaction);
        Assert.Equal("compaction-model", compactionEntry.Model);
        Assert.Equal(1, compactionEntry.Usage.Calls);
        Assert.Equal(250, compactionEntry.Usage.InputTokens);
        Assert.Equal(12, compactionEntry.Usage.OutputTokens);

        var agentEntry = SingleEntry(result.TokenUsage, UsageSource.Agent);
        Assert.Equal(2, agentEntry.Usage.Calls);
        Assert.Equal(4_200, agentEntry.Usage.InputTokens);
        Assert.Equal(10, agentEntry.Usage.OutputTokens);

        // Session state agrees, and the totals are the plain sum of both sources.
        Assert.Equal(1, CallsForSource(session.Usage, UsageSource.Compaction));
        Assert.Equal(agentEntry.Usage.InputTokens + compactionEntry.Usage.InputTokens, session.InputTokensUsed);
        Assert.Equal(agentEntry.Usage.OutputTokens + compactionEntry.Usage.OutputTokens, session.OutputTokensUsed);
    }

    /// <summary>
    /// Main client for the mid-loop compaction tests: round 1 proposes a tool call (so the mid-loop
    /// check runs and compacts between rounds), later rounds finish the run with text. Every round
    /// reports the same usage.
    /// </summary>
    private sealed class ScriptedMainStreamingClient : IChatClient
    {
        private readonly long _inputTokenCount;
        private readonly long _outputTokenCount;
        private readonly int _toolRounds;
        private int _calls;

        internal ScriptedMainStreamingClient(long inputTokenCount = 100, long outputTokenCount = 5, int toolRounds = 1)
        {
            _inputTokenCount = inputTokenCount;
            _outputTokenCount = outputTokenCount;
            _toolRounds = toolRounds;
        }

        internal int StreamingCallCount => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test drives the streaming path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            await Task.Yield();

            if (index < _toolRounds)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent($"call_{index}", "do_thing", new Dictionary<string, object?> { ["id"] = "x" })]);
                yield return new ChatResponseUpdate
                {
                    Contents = [new UsageContent(new UsageDetails { InputTokenCount = _inputTokenCount, OutputTokenCount = _outputTokenCount })]
                };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("All done.")]);
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = _inputTokenCount, OutputTokenCount = _outputTokenCount })]
            };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("main-fake", new Uri("https://example.invalid"), "main-model")
                : null;

        public void Dispose() { }
    }

    // ========================================================================
    // 7. The mid-loop overload during an execution (stateless run: no session)
    // ========================================================================

    [Fact]
    public async Task Execution_StatelessMidLoopCompaction_RecordsIntoExecutionSnapshotAndOnUsageOnly()
    {
        // ExecutionStreamingAsync with session == null still runs the manual tool loop, so the
        // mid-loop compaction overload is called with a null session. The call must be recorded in
        // the EXECUTION snapshot and reported through OnUsage — with no session to update.
        var compactionClient = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 60, outputPerCall: 3);
        var mainClient = new ScriptedMainStreamingClient();

        var events = new List<UsageEvent>();
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            EnableBash = false,
            EnableFileOps = false,
            EnableSkills = false,
            AutoLoadWorkspaceInstructions = false,
            SystemPrompt = "You are a test agent.",
            ShowToolCallsInStream = true,
            MaxContextTokens = 1_000,
            CompactionThreshold = 0.5,
            CompactionRetainRecent = 1,
            Logger = NullLogger.Instance,
            CompactionClient = compactionClient,
            OnUsage = events.Add
        };
        options.CustomTools = [AIFunctionFactory.Create((string id) => $"tool-result:{id}", "do_thing")];

        await using var agent = new CodingAgent(mainClient, options);

        // A stateless run has no session history, so the live prompt must itself be large enough to
        // trip the heuristic threshold (~1_600 tokens from 6_400 chars) — that is what makes the
        // mid-loop check compact with a null session.
        var userMessage = new string('y', 6_400);

        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(null, userMessage, TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.Completed) result = update.Result;
        }

        Assert.NotNull(result);
        Assert.Equal("Success", result!.Status);
        Assert.True(compactionClient.CallCount >= 1, "A stateless run with a large prompt should compact mid-loop.");

        // Compaction is visible in the execution snapshot with the compaction client's model.
        var compactionEntry = SingleEntry(result.TokenUsage, UsageSource.Compaction);
        Assert.Equal("compaction-model", compactionEntry.Model);
        Assert.Equal(compactionClient.CallCount, compactionEntry.Usage.Calls);
        Assert.Equal(compactionClient.CallCount * 60, compactionEntry.Usage.InputTokens);

        // ...and in OnUsage, alongside the Agent rounds.
        var compactionEvents = events.Where(e => e.Source == UsageSource.Compaction).ToList();
        Assert.Equal(compactionClient.CallCount, compactionEvents.Count);
        Assert.All(compactionEvents, e => Assert.Equal("compaction-model", e.Model));
        Assert.Contains(events, e => e.Source == UsageSource.Agent);
    }

    // ========================================================================
    // 8. Repeated compaction accumulates without touching the Agent totals
    // ========================================================================

    [Fact]
    public async Task Execution_MultipleCompactions_AccumulateIntoOneEntryAndLeaveAgentTotalsAlone()
    {
        // Two executions sharing the session: each auto-compacts before its round, so the session
        // accumulates two compaction calls while each execution's Agent bucket holds its own round.
        var compactionClient = new CompactionFakeClient(
            metadataModelId: "compaction-model", inputPerCall: 800, outputPerCall: 25);
        var mainClient = new MainAgentClient("main-model");

        var options = ExecutionOptions();
        options.CompactionClient = compactionClient;
        options.MaxContextTokens = 1_000;

        var session = LargeSession(12);
        session.LastKnownContextTokens = 900; // above the 500 threshold

        await using var agent = new CodingAgent(mainClient, options);

        var first = await agent.ExecuteAsync(session, "first", TestContext.Current.CancellationToken);
        // Force the second turn to compact again: the threshold check reads the exact token count.
        session.LastKnownContextTokens = 900;
        var second = await agent.ExecuteAsync(session, "second", TestContext.Current.CancellationToken);

        Assert.Equal("Success", first.Status);
        Assert.Equal("Success", second.Status);
        Assert.Equal(2, compactionClient.CallCount);
        Assert.Equal(2, mainClient.CallCount);

        // Both compaction calls collapse into a single Compaction entry per (source, model).
        var compactionEntry = SingleEntry(session.Usage, UsageSource.Compaction);
        Assert.Equal(2, compactionEntry.Usage.Calls);
        Assert.Equal(1_600, compactionEntry.Usage.InputTokens);
        Assert.Equal(50, compactionEntry.Usage.OutputTokens);

        // The Agent entry holds exactly the two agent rounds — unchanged by compaction bookkeeping.
        var agentEntry = SingleEntry(session.Usage, UsageSource.Agent);
        Assert.Equal(2, agentEntry.Usage.Calls);
        Assert.Equal(2_000, agentEntry.Usage.InputTokens);
        Assert.Equal(20, agentEntry.Usage.OutputTokens);

        // Each execution snapshot carries only its own calls (one compaction + one agent round).
        Assert.Equal(800, SingleEntry(first.TokenUsage, UsageSource.Compaction).Usage.InputTokens);
        Assert.Equal(1_000, SingleEntry(first.TokenUsage, UsageSource.Agent).Usage.InputTokens);
        Assert.Equal(800, SingleEntry(second.TokenUsage, UsageSource.Compaction).Usage.InputTokens);
        Assert.Equal(2, second.TokenUsage.Total.Calls);

        Assert.Equal(3_600, session.InputTokensUsed);
        Assert.Equal(70, session.OutputTokensUsed);
    }
}
