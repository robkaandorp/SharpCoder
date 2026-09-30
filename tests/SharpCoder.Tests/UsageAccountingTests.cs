using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

/// <summary>
/// Deterministic tests for SharpCoder's accurate per-call token accounting:
/// recording on all three execution paths (ExecuteAsync, ExecuteStreamingAsync with
/// <c>ShowToolCallsInStream = false</c> and the manual tool loop with
/// <c>ShowToolCallsInStream = true</c>), the recording contract for failed, partial and
/// early-disposed calls, model attribution, <see cref="AgentOptions.OnUsage"/>, the detached
/// <see cref="AgentResult.TokenUsage"/> snapshot, execution isolation and session persistence.
/// <para>
/// Every fake client is scripted per call index; interleaving is gated exclusively with
/// <see cref="TaskCompletionSource{TResult}"/> — there is no wall-clock timing, delay or timeout
/// anywhere in this file.
/// </para>
/// </summary>
public class UsageAccountingTests
{
    // ========================================================================
    // Scripted fake client
    // ========================================================================

    /// <summary>
    /// Deterministic chat client: each call index (counted separately per method) is served by a
    /// caller-supplied delegate, so a test controls per-round usage, tool calls, failures and gating.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<int, Task<ChatResponse>> _onGetResponse;
        private readonly Func<int, IAsyncEnumerable<ChatResponseUpdate>> _onGetStreaming;
        private int _responseCalls;
        private int _streamingCalls;

        internal ScriptedChatClient(
            Func<int, Task<ChatResponse>>? onGetResponse = null,
            Func<int, IAsyncEnumerable<ChatResponseUpdate>>? onGetStreaming = null,
            string? metadataModelId = null)
        {
            _onGetResponse = onGetResponse ?? (_ => throw new NotSupportedException("Non-streaming call was not scripted."));
            _onGetStreaming = onGetStreaming ?? (_ => throw new NotSupportedException("Streaming call was not scripted."));
            MetadataModelId = metadataModelId;
        }

        /// <summary>Model reported through <see cref="ChatClientMetadata"/>; null means "no metadata".</summary>
        internal string? MetadataModelId { get; }

        internal int ResponseCallCount => Volatile.Read(ref _responseCalls);

        internal int StreamingCallCount => Volatile.Read(ref _streamingCalls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _onGetResponse(Interlocked.Increment(ref _responseCalls) - 1);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _onGetStreaming(Interlocked.Increment(ref _streamingCalls) - 1);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) && MetadataModelId is not null
                ? new ChatClientMetadata("scripted-provider", new Uri("https://example.invalid"), MetadataModelId)
                : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Logger that throws from <c>LogWarning</c> — exactly the write the OnUsage failure-reporting
    /// boundary performs — while every other level is ignored. This is the discriminating fake for
    /// the containment contract: without the nested best-effort guard the warning write would escape
    /// the recording boundary and fail an otherwise successful model call, while a logger that only
    /// threw at unrelated levels would prove nothing about that boundary.
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

    /// <summary>
    /// Logger that records the messages it is asked to write without throwing, so a test can prove
    /// an ordinary handler failure IS reported.
    /// </summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> _entries = new();

        internal IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    // ========================================================================
    // Update / response builders
    // ========================================================================

    private static UsageDetails Details(long? input, long? output, long? cached, long? reasoning)
        => new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = cached,
            ReasoningTokenCount = reasoning
        };

    private static ChatResponseUpdate AssistantText(string text, string? modelId = null)
        => new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ModelId = modelId,
            Contents = [new TextContent(text)]
        };

    private static ChatResponseUpdate ToolCall(string callId, string toolName, string? modelId = null)
        => new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ModelId = modelId,
            Contents = [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["id"] = "x" })]
        };

    private static ChatResponseUpdate UsageUpdate(long? input, long? output, long? cached = null, long? reasoning = null, string? modelId = null)
        => new ChatResponseUpdate
        {
            ModelId = modelId,
            Contents = [new UsageContent(Details(input, output, cached, reasoning))]
        };

    private static ChatResponseUpdate Finished(ChatFinishReason? reason)
        => new ChatResponseUpdate { FinishReason = reason };

    private static ChatResponse TextResponse(string text, long? input, long? output,
        long? cached = null, long? reasoning = null, string? modelId = null)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = modelId,
            Usage = Details(input, output, cached, reasoning)
        };

    private static ChatResponse ToolCallResponse(string callId, string toolName, long? input, long? output,
        long? cached = null, long? reasoning = null, string? modelId = null)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["id"] = "x" })]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = modelId,
            Usage = Details(input, output, cached, reasoning)
        };

    // ========================================================================
    // Streaming scripts
    // ========================================================================

    /// <summary>Round that proposes one tool call and reports usage.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> ToolRoundStream(
        string callId, long? input, long? output, long? cached = null, long? reasoning = null, string? modelId = null)
    {
        await Task.Yield();
        yield return ToolCall(callId, "do_thing", modelId);
        yield return UsageUpdate(input, output, cached, reasoning, modelId);
        yield return Finished(ChatFinishReason.ToolCalls);
    }

    /// <summary>Round that finishes the run with text and reports usage.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> FinalRoundStream(
        long? input, long? output, long? cached = null, long? reasoning = null, string? modelId = null)
    {
        await Task.Yield();
        yield return AssistantText("All done.", modelId);
        yield return UsageUpdate(input, output, cached, reasoning, modelId);
        yield return Finished(ChatFinishReason.Stop);
    }

    /// <summary>Round that reports usage, then fails.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> StreamYieldsUsageThenThrows(
        Exception error, long? input, long? output)
    {
        await Task.Yield();
        yield return UsageUpdate(input, output);
        throw error;
    }

    /// <summary>Round that fails before reporting any usage.</summary>
    private static IAsyncEnumerable<ChatResponseUpdate> StreamThrowsBeforeUsage(Exception error)
        => new ThrowingStream(error);

    /// <summary>
    /// An enumerable whose very first <c>MoveNextAsync</c> throws — the shape a provider produces
    /// when the request itself is rejected (for example a context-overflow error) before any update.
    /// </summary>
    private sealed class ThrowingStream : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly Exception _error;

        internal ThrowingStream(Exception error) => _error = error;

        public ChatResponseUpdate Current => throw new InvalidOperationException("The stream failed before producing an update.");

        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(_error);

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>A long round the consumer may abandon after its first text update.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAbandonedAfterFirstText(long? input, long? output)
    {
        await Task.Yield();
        yield return UsageUpdate(input, output);
        yield return AssistantText("partial text");
        yield return AssistantText("never consumed");
        yield return Finished(ChatFinishReason.Stop);
    }

    /// <summary>A round that proposes another tool call and never finishes the run.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> NeverFinishingRound(
        string callId, long? input, long? output, string? modelId = null)
    {
        await Task.Yield();
        yield return ToolCall(callId, "do_thing", modelId);
        yield return UsageUpdate(input, output, modelId: modelId);
        yield return Finished(ChatFinishReason.ToolCalls);
    }

    /// <summary>A completed round that carries no usage anywhere.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> UsageFreeStream()
    {
        await Task.Yield();
        yield return AssistantText("done");
        yield return Finished(ChatFinishReason.Stop);
    }

    /// <summary>A completed non-streaming response with <c>Usage</c> left null.</summary>
    private static ChatResponse UsageFreeTextResponse(string text)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    /// <summary>
    /// Client whose every round proposes another tool call and reports the same usage, so the step
    /// budget (and therefore MaxStepsReached) is reached deterministically.
    /// </summary>
    private static ScriptedChatClient AlwaysToolCallingClient(
        long? input, long? output, long? cached = null, long? reasoning = null, string? metadataModelId = "metadata-model")
        => new ScriptedChatClient(
            onGetResponse: index => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call_" + index, "do_thing", new Dictionary<string, object?> { ["id"] = "x" })]))
            {
                FinishReason = ChatFinishReason.ToolCalls,
                Usage = Details(input, output, cached, reasoning)
            }),
            onGetStreaming: index => NeverFinishingRound("call_" + index, input, output),
            metadataModelId: metadataModelId);

    /// <summary>
    /// One call whose usage arrives split over several updates, each carrying only part of the
    /// picture. Providers may report one usage item per update, so all of them describe the same
    /// single call and must be summed — the input count arrives twice (100 then 200), so a
    /// last-item-only reader would report 200 instead of 300 and would lose the "reported" state.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> StreamSplitUsage(long firstInput, long secondInput, long output, long cached)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = firstInput, OutputTokenCount = output })] };
        yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = secondInput, CachedInputTokenCount = cached })] };
        yield return AssistantText("done");
        yield return Finished(ChatFinishReason.Stop);
    }

    /// <summary>
    /// Final round that signals entry and then parks on a gate until the test releases it, so the
    /// test can inspect session state while this call is provably still in flight.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> GatedFinalRound(
        TaskCompletionSource<bool> entered, TaskCompletionSource<bool> gate)
    {
        entered.TrySetResult(true);
        await gate.Task.ConfigureAwait(false);
        yield return AssistantText("All done.");
        yield return UsageUpdate(15_000, 20);
        yield return Finished(ChatFinishReason.Stop);
    }

    // ========================================================================
    // Options / execution helpers
    // ========================================================================

    /// <summary>The three execution paths that must all record usage.</summary>
    public enum UsageExecutionPath
    {
        /// <summary><c>ExecuteAsync</c>, which relies on <c>FunctionInvokingChatClient</c>.</summary>
        ExecuteAsync,

        /// <summary><c>ExecuteStreamingAsync</c> with <c>ShowToolCallsInStream = false</c>.</summary>
        StreamingDefault,

        /// <summary><c>ExecuteStreamingAsync</c> with <c>ShowToolCallsInStream = true</c> (manual tool loop).</summary>
        StreamingManualToolLoop
    }

    private static AgentOptions ToolLoopOptions(UsageExecutionPath path)
    {
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            EnableBash = false,
            EnableFileOps = false,
            EnableSkills = false,
            AutoLoadWorkspaceInstructions = false,
            SystemPrompt = "You are a test agent.",
            ShowToolCallsInStream = path == UsageExecutionPath.StreamingManualToolLoop
        };
        options.CustomTools = [AIFunctionFactory.Create((string id) => $"tool-result:{id}", "do_thing")];
        return options;
    }

    private static async Task<AgentResult> RunAsync(UsageExecutionPath path, CodingAgent agent, AgentSession session, CancellationToken ct)
    {
        if (path == UsageExecutionPath.ExecuteAsync)
            return await agent.ExecuteAsync(session, "Do the thing", ct);

        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, "Do the thing", ct))
        {
            if (update.Kind == StreamingUpdateKind.Completed)
                result = update.Result;
        }

        Assert.NotNull(result);
        return result!;
    }

    /// <summary>Two-round tool loop: round 1 proposes a tool call, round 2 finishes with text.</summary>
    private static ScriptedChatClient ToolLoopClient(
        long? firstInput, long? firstOutput, long? secondInput, long? secondOutput,
        long? firstCached = null, long? firstReasoning = null,
        long? secondCached = null, long? secondReasoning = null,
        string? metadataModelId = "metadata-model", string? responseModelId = null)
        => new ScriptedChatClient(
            onGetResponse: index => Task.FromResult(index == 0
                ? ToolCallResponse("call_1", "do_thing", firstInput, firstOutput, firstCached, firstReasoning, responseModelId)
                : TextResponse("All done.", secondInput, secondOutput, secondCached, secondReasoning, responseModelId)),
            onGetStreaming: index => index == 0
                ? ToolRoundStream("call_1", firstInput, firstOutput, firstCached, firstReasoning, responseModelId)
                : FinalRoundStream(secondInput, secondOutput, secondCached, secondReasoning, responseModelId),
            metadataModelId: metadataModelId);

    private static long CallsForSource(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => (long)e.Usage.Calls);

    private static long InputTokensForSource(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => e.Usage.InputTokens);

    // ========================================================================
    // 1. The three execution paths: every round is recorded
    // ========================================================================

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_TwoRoundToolLoop_RecordsEveryRoundsUsage(UsageExecutionPath path)
    {
        var client = ToolLoopClient(firstInput: 10_000, firstOutput: 10, secondInput: 15_000, secondOutput: 20);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("two-round-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal(2, client.ResponseCallCount + client.StreamingCallCount);

        // Session cumulative counters sum BOTH rounds; the old streaming under-count reported only
        // the final round (15_000 / 20).
        Assert.Equal(25_000, session.InputTokensUsed);
        Assert.Equal(30, session.OutputTokensUsed);
        Assert.NotEqual(15_000, session.InputTokensUsed);

        // LastKnownContextTokens keeps its old meaning: the final round's input, never a sum.
        Assert.Equal(15_000, session.LastKnownContextTokens);

        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(25_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(30, result.TokenUsage.Total.OutputTokens);

        Assert.Equal(2, session.Usage.Total.Calls);
        Assert.Equal(25_000, session.Usage.Total.InputTokens);
        Assert.Equal(30, session.Usage.Total.OutputTokens);

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal("metadata-model", entry.Model);
        Assert.Equal(2, entry.Usage.Calls);
        Assert.Equal(25_000, entry.Usage.InputTokens);
        Assert.Equal(30, entry.Usage.OutputTokens);
    }

    // ========================================================================
    // 2. Cached input / reasoning: sums plus "was it reported?" counters
    // ========================================================================

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_OnlyReportingRounds_ContributeToOptionalCounts(UsageExecutionPath path)
    {
        // Round 1 reports cached input and reasoning; round 2 reports neither.
        var client = ToolLoopClient(
            firstInput: 100, firstOutput: 1, secondInput: 200, secondOutput: 2,
            firstCached: 80, firstReasoning: 7);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("optional-counts-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal(80, session.Usage.Total.CachedInputTokens);
        Assert.Equal(1, session.Usage.Total.CachedInputReportedCalls);
        Assert.Equal(7, session.Usage.Total.ReasoningTokens);
        Assert.Equal(1, session.Usage.Total.ReasoningReportedCalls);

        // The snapshot agrees with the session.
        Assert.Equal(80, result.TokenUsage.Total.CachedInputTokens);
        Assert.Equal(1, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(7, result.TokenUsage.Total.ReasoningTokens);
        Assert.Equal(1, result.TokenUsage.Total.ReasoningReportedCalls);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_NoRoundReportsOptionalCounts_ReportedCallCountersAreZero(UsageExecutionPath path)
    {
        var client = ToolLoopClient(firstInput: 100, firstOutput: 1, secondInput: 200, secondOutput: 2);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("no-optional-counts-" + path);

        await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        // Zero sums AND zero reported-call counters: "not reported" stays distinguishable from 0.
        Assert.Equal(0, session.Usage.Total.CachedInputTokens);
        Assert.Equal(0, session.Usage.Total.CachedInputReportedCalls);
        Assert.Equal(0, session.Usage.Total.ReasoningTokens);
        Assert.Equal(0, session.Usage.Total.ReasoningReportedCalls);
        Assert.Equal(2, session.Usage.Total.Calls);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_EveryRoundReportsOptionalCounts_SumsBothAndCountsBoth(UsageExecutionPath path)
    {
        var client = ToolLoopClient(
            firstInput: 100, firstOutput: 1, secondInput: 200, secondOutput: 2,
            firstCached: 80, firstReasoning: 7, secondCached: 20, secondReasoning: 3);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("both-optional-counts-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal(100, result.TokenUsage.Total.CachedInputTokens);
        Assert.Equal(2, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(10, result.TokenUsage.Total.ReasoningTokens);
        Assert.Equal(2, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal(100, session.Usage.Total.CachedInputTokens);
        Assert.Equal(2, session.Usage.Total.CachedInputReportedCalls);
        Assert.Equal(10, session.Usage.Total.ReasoningTokens);
        Assert.Equal(2, session.Usage.Total.ReasoningReportedCalls);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_ReportedZeroIsDistinguishedFromNotReported(UsageExecutionPath path)
    {
        // Round 1 REPORTS zero cached input and zero reasoning tokens; round 2 does not report them
        // at all (null). The sums are zero either way, so the *ReportedCalls counters are the only
        // observables that separate "the provider said zero" from "the provider said nothing":
        // exactly one call reported each optional count.
        var client = ToolLoopClient(
            firstInput: 100, firstOutput: 1, secondInput: 200, secondOutput: 2,
            firstCached: 0, firstReasoning: 0, secondCached: null, secondReasoning: null);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("reported-zero-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        // Zero sums...
        Assert.Equal(0, result.TokenUsage.Total.CachedInputTokens);
        Assert.Equal(0, result.TokenUsage.Total.ReasoningTokens);
        // ...reported by exactly one of the two calls (not 0: that would mean "never reported";
        // not 2: the second call reported nothing).
        Assert.Equal(1, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(1, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal(2, result.TokenUsage.Total.Calls);

        // The session aggregate makes the same distinction.
        Assert.Equal(0, session.Usage.Total.CachedInputTokens);
        Assert.Equal(1, session.Usage.Total.CachedInputReportedCalls);
        Assert.Equal(0, session.Usage.Total.ReasoningTokens);
        Assert.Equal(1, session.Usage.Total.ReasoningReportedCalls);
        Assert.Equal(2, session.Usage.Total.Calls);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_ReportedZeroInTheFinalRoundOnly_IsStillCounted(UsageExecutionPath path)
    {
        // The mirror arrangement: round 1 reports nothing, round 2 reports zero. A last-round-only
        // presence check would pass this, but a check that also requires a non-null value on the
        // first round cannot: the counter must still be exactly one.
        var client = ToolLoopClient(
            firstInput: 100, firstOutput: 1, secondInput: 200, secondOutput: 2,
            firstCached: null, firstReasoning: null, secondCached: 0, secondReasoning: 0);
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("reported-zero-last-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TokenUsage.Total.CachedInputTokens);
        Assert.Equal(1, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(0, result.TokenUsage.Total.ReasoningTokens);
        Assert.Equal(1, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal(1, session.Usage.Total.CachedInputReportedCalls);
        Assert.Equal(1, session.Usage.Total.ReasoningReportedCalls);
    }

    [Fact]
    public void TokenUsage_FromSingleCall_ReportedZeroIncrementsTheReportedCallCounters()
    {
        // Unit-level companion: a reported zero is present, an absent count is not.
        var reportedZero = TokenUsage.FromSingleCall(new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 1,
            CachedInputTokenCount = 0,
            ReasoningTokenCount = 0
        });

        Assert.Equal(0, reportedZero.CachedInputTokens);
        Assert.Equal(1, reportedZero.CachedInputReportedCalls);
        Assert.Equal(0, reportedZero.ReasoningTokens);
        Assert.Equal(1, reportedZero.ReasoningReportedCalls);

        var notReported = TokenUsage.FromSingleCall(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 1 });

        Assert.Equal(0, notReported.CachedInputTokens);
        Assert.Equal(0, notReported.CachedInputReportedCalls);
        Assert.Equal(0, notReported.ReasoningTokens);
        Assert.Equal(0, notReported.ReasoningReportedCalls);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task EveryExecutionPath_CallWithoutAnyUsageInformation_CountsAsCallWithZeroTokens(UsageExecutionPath path)
    {
        // The provider reports no usage whatsoever: the call must still be counted, with zero tokens
        // and no reported-optional counters.
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(UsageFreeTextResponse("done")),
            onGetStreaming: _ => UsageFreeStream(),
            metadataModelId: "metadata-model");
        await using var agent = new CodingAgent(client, ToolLoopOptions(path));
        var session = AgentSession.Create("no-usage-at-all-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TokenUsage.Total.Calls);
        Assert.Equal(0, result.TokenUsage.Total.InputTokens);
        Assert.Equal(0, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(0, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(0, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal("metadata-model", Assert.Single(result.TokenUsage.Entries).Model);

        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(0, session.InputTokensUsed);
        Assert.Equal(0, session.OutputTokensUsed);
    }

    [Theory]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task StreamingCall_UsageSplitAcrossUpdates_IsOneCallWithAllCounts(UsageExecutionPath path)
    {
        // A single call may deliver input, output and cached input in separate UsageContent items.
        // The whole call must count once, with every count summed in — no last-item-only reading.
        var client = new ScriptedChatClient(
            onGetStreaming: _ => StreamSplitUsage(firstInput: 100, secondInput: 200, output: 30, cached: 120),
            metadataModelId: "metadata-model");
        var options = path == UsageExecutionPath.StreamingManualToolLoop ? ToolLoopOptions(path) : MinimalOptions();
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("split-usage-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.StreamingCallCount);
        Assert.Equal(1, result.TokenUsage.Total.Calls);
        Assert.Equal(300, result.TokenUsage.Total.InputTokens);
        Assert.Equal(30, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(120, result.TokenUsage.Total.CachedInputTokens);
        Assert.Equal(1, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(0, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal(300, session.InputTokensUsed);
        Assert.Equal(30, session.OutputTokensUsed);
        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(300, session.Usage.Total.InputTokens);

        // The input count of that call was reported, so it is the session's known context size —
        // a last-item-only reader would treat the final (output/cached) update as "no input" here.
        Assert.Equal(300, session.LastKnownContextTokens);
    }

    [Fact]
    public void TokenUsage_Add_SumsCountsAndReportedCalls()
    {
        var total = new TokenUsage();
        total.Add(new TokenUsage { InputTokens = 10, Calls = 1, CachedInputReportedCalls = 1, CachedInputTokens = 4 });
        total.Add(new TokenUsage { InputTokens = 5, Calls = 1 });
        total.Add(null);

        Assert.Equal(15, total.InputTokens);
        Assert.Equal(2, total.Calls);
        Assert.Equal(4, total.CachedInputTokens);
        Assert.Equal(1, total.CachedInputReportedCalls);
        Assert.Equal(0, total.ReasoningReportedCalls);
    }

    // ========================================================================
    // 3. Model attribution
    // ========================================================================

    [Fact]
    public async Task ModelAttribution_ClientMetadataWinsOverResponseModelId()
    {
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("done", 10, 1, modelId: "response-model")),
            onGetStreaming: _ => FinalRoundStream(10, 1, modelId: "response-model"),
            metadataModelId: "metadata-model");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("model-metadata");

        await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Equal("metadata-model", entry.Model);
    }

    [Fact]
    public async Task ModelAttribution_ResponseModelIdUsedWhenMetadataAbsent()
    {
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("done", 10, 1, modelId: "response-model")),
            metadataModelId: null);
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("model-response");

        await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Equal("response-model", entry.Model);
    }

    [Fact]
    public async Task ModelAttribution_NoModelInformation_RecordsNullModel()
    {
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("done", 10, 1)),
            metadataModelId: null);
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("model-unknown");

        await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Null(entry.Model);
        Assert.Equal(1, entry.Usage.Calls);
        Assert.Equal(10, entry.Usage.InputTokens);
    }

    [Fact]
    public async Task ModelAttribution_StreamingResponseModelIdUsedWhenMetadataAbsent()
    {
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath(),
            EnableBash = false,
            EnableFileOps = false,
            EnableSkills = false,
            AutoLoadWorkspaceInstructions = false,
            SystemPrompt = "You are a test agent.",
            ShowToolCallsInStream = true
        };
        var client = new ScriptedChatClient(
            onGetStreaming: _ => FinalRoundStream(10, 1, modelId: "stream-response-model"),
            metadataModelId: null);
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("model-stream-response");

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "go", TestContext.Current.CancellationToken)) { }

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Equal("stream-response-model", entry.Model);
    }

    [Fact]
    public void UsageSummary_TwoModelsUnderOneSource_ProducesTwoEntries()
    {
        var summary = new UsageSummary();
        summary.Add(Event(UsageSource.Agent, "model-a", input: 10, output: 1));
        summary.Add(Event(UsageSource.Agent, "model-b", input: 20, output: 2));

        Assert.Equal(2, summary.Entries.Count);
        Assert.Equal(2, summary.Total.Calls);
        Assert.Equal(30, summary.Total.InputTokens);
        Assert.Equal("model-a", summary.Entries[0].Model);
        Assert.Equal("model-b", summary.Entries[1].Model);
    }

    [Fact]
    public void UsageSummary_SameModelUnderTwoSources_ProducesTwoEntries()
    {
        var summary = new UsageSummary();
        summary.Add(Event(UsageSource.Agent, "same-model", input: 10, output: 1));
        summary.Add(Event(UsageSource.Compaction, "same-model", input: 20, output: 2));

        Assert.Equal(2, summary.Entries.Count);
        Assert.Equal(UsageSource.Agent, summary.Entries[0].Source);
        Assert.Equal(UsageSource.Compaction, summary.Entries[1].Source);
        Assert.Equal(30, summary.Total.InputTokens);
    }

    [Fact]
    public void UsageSummary_Snapshot_IsDetachedFromLaterAdds()
    {
        var summary = new UsageSummary();
        summary.Add(Event(UsageSource.Agent, "model-a", input: 10, output: 1));

        var snapshot = summary.Snapshot();
        summary.Add(Event(UsageSource.Agent, "model-a", input: 20, output: 2));

        Assert.Equal(1, snapshot.Total.Calls);
        Assert.Equal(10, snapshot.Total.InputTokens);
        Assert.Equal(10, Assert.Single(snapshot.Entries).Usage.InputTokens);
        Assert.Equal(2, summary.Total.Calls);
        Assert.Equal(30, summary.Total.InputTokens);

        // The entries collection is detached too: mutating it cannot reach the summary.
        snapshot.Entries[0].Usage.InputTokens = 999;
        Assert.Equal(30, summary.Total.InputTokens);
    }

    // ========================================================================
    // 4. AgentResult snapshot lifecycle
    // ========================================================================

    [Fact]
    public async Task Execute_ResultSnapshot_IsNotChangedByLaterRecordedCalls()
    {
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("done", 10, 1)),
            metadataModelId: "metadata-model");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("snapshot-isolation");

        var first = await agent.ExecuteAsync(session, "one", TestContext.Current.CancellationToken);
        var second = await agent.ExecuteAsync(session, "two", TestContext.Current.CancellationToken);

        // Both executions record into the same session, but each result holds its own snapshot:
        // each snapshot describes only its own call (the script reports 10 input tokens per call).
        Assert.Equal(1, first.TokenUsage.Total.Calls);
        Assert.Equal(10, first.TokenUsage.Total.InputTokens);
        Assert.Equal(1, second.TokenUsage.Total.Calls);
        Assert.Equal(10, second.TokenUsage.Total.InputTokens);
        Assert.NotSame(first.TokenUsage, second.TokenUsage);

        // The session accumulates the calls of both executions.
        Assert.Equal(2, session.Usage.Total.Calls);
        Assert.Equal(20, session.InputTokensUsed);
        Assert.Equal(2, session.OutputTokensUsed);
        Assert.Equal(20, session.Usage.Total.InputTokens);

        // Recording more calls (here and later) never reaches the snapshots already handed out.
        session.Usage.Add(Event(UsageSource.Agent, "metadata-model", input: 999, output: 99));
        Assert.Equal(10, first.TokenUsage.Total.InputTokens);
        Assert.Equal(1, first.TokenUsage.Total.Calls);
        Assert.Equal(10, second.TokenUsage.Total.InputTokens);
        Assert.Equal(1, second.TokenUsage.Total.Calls);
        Assert.Equal(3, session.Usage.Total.Calls);
    }

    [Fact]
    public void UsageEvent_CopiesTheUsageItIsGiven()
    {
        var mutable = new TokenUsage { InputTokens = 5, Calls = 1 };
        var recordedEvent = new UsageEvent(UsageSource.Agent, "model-a", mutable);

        mutable.InputTokens = 999;
        Assert.Equal(5, recordedEvent.Usage.InputTokens);

        // An event always describes exactly one call, whatever the caller passed in.
        Assert.Equal(1, recordedEvent.Usage.Calls);
        var manyCalls = new UsageEvent(UsageSource.Agent, "model-a", new TokenUsage { Calls = 7 });
        Assert.Equal(1, manyCalls.Usage.Calls);
    }

    // ========================================================================
    // 5. OnUsage
    // ========================================================================

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task OnUsage_FiresOncePerCallWithSourceAndModel(UsageExecutionPath path)
    {
        var events = new ConcurrentQueue<UsageEvent>();
        var client = ToolLoopClient(firstInput: 10_000, firstOutput: 10, secondInput: 15_000, secondOutput: 20);
        var options = ToolLoopOptions(path);
        options.OnUsage = events.Enqueue;
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("onusage-" + path);

        await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        var recorded = events.ToArray();
        Assert.Equal(2, recorded.Length);
        foreach (var recordedEvent in recorded)
        {
            Assert.Equal(UsageSource.Agent, recordedEvent.Source);
            Assert.Equal("metadata-model", recordedEvent.Model);
            Assert.Null(recordedEvent.SubAgentId);
            Assert.Equal(1, recordedEvent.Usage.Calls);
        }

        Assert.Equal(10_000, recorded[0].Usage.InputTokens);
        Assert.Equal(10, recorded[0].Usage.OutputTokens);
        Assert.Equal(15_000, recorded[1].Usage.InputTokens);
        Assert.Equal(20, recorded[1].Usage.OutputTokens);
    }

    [Fact]
    public async Task OnUsage_ObservesSessionAlreadyUpdatedAndRunsOutsideTheRecordingLock()
    {
        // What this proves: ORDERING — the session's cumulative counters and usage summary are
        // already updated when the callback runs, so the callback sees the recorded call rather
        // than the state before it.
        // What this does NOT prove: that the callback runs with no internal lock held. The reads
        // below happen on the same thread that invoked the callback, and C# locks are re-entrant,
        // so they would succeed even if the recorder held a lock across the call.
        // Why there is no cross-thread probe here: UsageSummary guards its state with a PRIVATE
        // monitor object, so a test cannot acquire it; any non-blocking evidence would need a
        // timeout-bounded wait on a background acquire, which is exactly the kind of wall-clock
        // synchronisation this suite forbids. The lock-freedom claim therefore rests on the
        // recorder's code shape (Record contains no lock), not on an observation.
        var observedSessionCounts = new List<long>();
        var observedSessionCalls = new List<int>();
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("done", 42, 3)),
            metadataModelId: "metadata-model");
        var options = MinimalOptions();
        AgentSession? session = null;
        options.OnUsage = usageEvent =>
        {
            observedSessionCounts.Add(session!.InputTokensUsed);
            observedSessionCalls.Add(session.Usage.Total.Calls);
            Assert.Equal(42, usageEvent.Usage.InputTokens);
        };

        await using var agent = new CodingAgent(client, options);
        session = AgentSession.Create("onusage-reentrant");

        await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 42 }, observedSessionCounts);
        Assert.Equal(new[] { 1 }, observedSessionCalls);
    }

    [Fact]
    public async Task OnUsage_ThrowingHandler_DoesNotFailOrAlterTheRun()
    {
        var throwCount = 0;
        var client = ToolLoopClient(firstInput: 10_000, firstOutput: 10, secondInput: 15_000, secondOutput: 20);
        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref throwCount);
            throw new InvalidOperationException("handler boom");
        };
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("onusage-throwing");

        var result = await RunAsync(UsageExecutionPath.StreamingManualToolLoop, agent, session,
            TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal(2, Volatile.Read(ref throwCount));
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(25_000, session.InputTokensUsed);
        Assert.Equal(2, session.Usage.Total.Calls);
    }

    [Fact]
    public async Task OnUsage_ThrowingHandlerWithThrowingLogger_StillLeavesTheRunSuccessful()
    {
        // MAJOR-1 regression: the OnUsage failure-reporting boundary must itself be unable to throw.
        // Here the handler throws AND the logger's LogWarning throws; without the nested best-effort
        // guard the logging exception would escape Record, pass through the model call's finally and
        // turn this otherwise successful response into an Error result (or a thrown exception).
        var handlerThrowCount = 0;
        var throwingLogger = new ThrowingLogger();
        var client = ToolLoopClient(firstInput: 10_000, firstOutput: 10, secondInput: 15_000, secondOutput: 20);
        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        options.Logger = throwingLogger;
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref handlerThrowCount);
            throw new InvalidOperationException("handler boom");
        };
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("onusage-throwing-logger");

        var result = await RunAsync(UsageExecutionPath.StreamingManualToolLoop, agent, session,
            TestContext.Current.CancellationToken);

        // No outcome change: the run is still a success and every call is still accounted for.
        Assert.Equal("Success", result.Status);
        Assert.Equal(2, Volatile.Read(ref handlerThrowCount));
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(25_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(30, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(25_000, session.InputTokensUsed);
        Assert.Equal(2, session.Usage.Total.Calls);

        // The logger was genuinely asked to report the two handler failures (its write attempts are
        // what threw), so the guard contained real logging exceptions rather than skipping logging.
        Assert.Equal(2, throwingLogger.WarningWriteAttempts);
    }

    [Fact]
    public async Task OnUsage_ThrowingHandlerWithNormalLogger_IsLoggedAndDoesNotChangeTheRun()
    {
        // The companion contract: with a working logger, an ordinary handler failure IS reported —
        // it is caught, logged as a warning, and the run proceeds unchanged.
        var handlerThrowCount = 0;
        var logger = new RecordingLogger();
        var client = ToolLoopClient(firstInput: 10_000, firstOutput: 10, secondInput: 15_000, secondOutput: 20);
        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        options.Logger = logger;
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref handlerThrowCount);
            throw new InvalidOperationException("handler boom");
        };
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("onusage-normal-logger");

        var result = await RunAsync(UsageExecutionPath.StreamingManualToolLoop, agent, session,
            TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal(2, Volatile.Read(ref handlerThrowCount));
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(25_000, session.InputTokensUsed);

        // Both failures were logged, each carrying the handler's exception as the log exception.
        var warnings = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Contains("OnUsage", w.Message, StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(w.Exception);
        });
    }

    // ========================================================================
    // 6. Failed, partial and early-disposed calls
    // ========================================================================

    [Fact]
    public async Task ExecuteAsync_CallThrowsBeforeAnyUsage_RecordsZeroTokenCall()
    {
        var client = new ScriptedChatClient(
            onGetResponse: _ => throw new InvalidOperationException("provider exploded"),
            metadataModelId: "metadata-model");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("throw-before-usage");

        var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        // The failed attempt is still one recorded call, with zero tokens and no reported options.
        Assert.Equal(1, result.TokenUsage.Total.Calls);
        Assert.Equal(0, result.TokenUsage.Total.InputTokens);
        Assert.Equal(0, result.TokenUsage.Total.CachedInputReportedCalls);
        Assert.Equal(0, result.TokenUsage.Total.ReasoningReportedCalls);
        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(0, session.InputTokensUsed);
    }

    [Fact]
    public async Task StreamingDefault_CallYieldsUsageThenThrows_KeepsTheReceivedUsage()
    {
        var client = new ScriptedChatClient(
            onGetStreaming: _ => StreamYieldsUsageThenThrows(new InvalidOperationException("mid-stream boom"), 1_000, 11),
            metadataModelId: "metadata-model");
        var options = MinimalOptions();
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("stream-yields-then-throws");

        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, "go", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.Completed) result = update.Result;
        }

        Assert.NotNull(result);
        Assert.Equal("Error", result!.Status);
        Assert.Equal(1, result.TokenUsage.Total.Calls);
        Assert.Equal(1_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(11, result.TokenUsage.Total.OutputTokens);

        // Usage recorded before the failure is already in the session, too.
        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(1_000, session.InputTokensUsed);
        Assert.Equal(11, session.OutputTokensUsed);
    }

    [Fact]
    public async Task ManualToolLoop_SecondRoundYieldsUsageThenThrows_EveryEndedCallIsRecorded()
    {
        var client = new ScriptedChatClient(
            onGetStreaming: index => index == 0
                ? ToolRoundStream("call_1", 10_000, 10)
                : StreamYieldsUsageThenThrows(new InvalidOperationException("second round boom"), 15_000, 20),
            metadataModelId: "metadata-model");
        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("manual-second-round-throws");

        var result = await RunAsync(UsageExecutionPath.StreamingManualToolLoop, agent, session,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(25_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(30, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(25_000, session.InputTokensUsed);
        Assert.Equal(30, session.OutputTokensUsed);
        Assert.Equal(2, client.StreamingCallCount);
    }

    [Theory]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task StreamingExecution_DisposedEarlyByConsumer_RecordsInFlightCallExactlyOnce(UsageExecutionPath path)
    {
        var client = new ScriptedChatClient(
            onGetStreaming: _ => StreamAbandonedAfterFirstText(100, 5),
            metadataModelId: "metadata-model");
        var options = path == UsageExecutionPath.StreamingManualToolLoop
            ? ToolLoopOptions(path)
            : MinimalOptions();
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("early-dispose-" + path);

        var enumerator = agent.ExecuteStreamingAsync(session, "go", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            bool sawText = false;
            while (!sawText && await enumerator.MoveNextAsync())
            {
                if (enumerator.Current.Kind == StreamingUpdateKind.TextDelta) sawText = true;
            }

            Assert.True(sawText, "The stream must have produced at least one text delta before the consumer abandons it.");
        }
        finally
        {
            // Abandoning the execution disposes it; the in-flight call must be recorded exactly once.
            await enumerator.DisposeAsync();
        }

        Assert.Equal(1, client.StreamingCallCount);
        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(100, session.Usage.Total.InputTokens);
        Assert.Equal(5, session.Usage.Total.OutputTokens);
        Assert.Equal(100, session.InputTokensUsed);
        Assert.Equal(5, session.OutputTokensUsed);
    }

    [Fact]
    public async Task ManualToolLoop_ContextOverflow_RecordsFailedAttemptCompactionAndRetry()
    {
        // The first attempt overflows; the agent force-compacts and retries the round once.
        // The failed attempt and the retry are Agent calls; the compaction's summary call is a
        // Compaction call (Checkpoint 2), and all three are recorded.
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromResult(TextResponse("Compact summary text", 77, 7, modelId: "compaction-model")),
            onGetStreaming: index => index == 0
                ? StreamThrowsBeforeUsage(new InvalidOperationException("model_max_prompt_tokens_exceeded"))
                : FinalRoundStream(5_000, 8),
            metadataModelId: "metadata-model");

        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        options.CompactionThreshold = 0.8;
        options.MaxContextTokens = 100_000;
        options.CompactionRetainRecent = 2;

        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("overflow-retry");
        for (var i = 0; i < 6; i++)
            session.MessageHistory.Add(new ChatMessage(ChatRole.User, $"history {i}"));

        var result = await RunAsync(UsageExecutionPath.StreamingManualToolLoop, agent, session,
            TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);

        // One compaction happened and the round was retried once.
        Assert.Contains(session.MessageHistory, m => m.Text?.Contains("CONTEXT SUMMARY", StringComparison.Ordinal) == true);
        Assert.Equal(2, client.StreamingCallCount);
        Assert.Equal(1, client.ResponseCallCount);

        // The failed attempt is a recorded call with zero tokens; the retry carries its usage.
        Assert.Equal(2, CallsForSource(session.Usage, UsageSource.Agent));
        Assert.Equal(5_000, InputTokensForSource(session.Usage, UsageSource.Agent));

        // The compaction summary call is a Compaction call, recorded exactly once.
        Assert.Equal(1, CallsForSource(session.Usage, UsageSource.Compaction));
        Assert.Equal(77, InputTokensForSource(session.Usage, UsageSource.Compaction));
        var compactionEntry = Assert.Single(session.Usage.Entries, e => e.Source == UsageSource.Compaction);
        // No CompactionClient is configured here, so the compactor summarises with the main client
        // and the recorded model is that client's metadata model (which wins over the response's).
        Assert.Equal("metadata-model", compactionEntry.Model);
        Assert.Equal(7, compactionEntry.Usage.OutputTokens);

        // Session totals sum the Agent and Compaction calls.
        Assert.Equal(5_077, session.InputTokensUsed);
        Assert.Equal(15, session.OutputTokensUsed);

        // The execution's snapshot carries every call, including the Compaction entry.
        Assert.Equal(3, result.TokenUsage.Total.Calls);
        Assert.Equal(5_077, result.TokenUsage.Total.InputTokens);
        Assert.Equal(1, CallsForSource(result.TokenUsage, UsageSource.Compaction));
    }

    // ========================================================================
    // 7. Terminal states carry the full TokenUsage
    // ========================================================================

    [Fact]
    public async Task SessionUsage_IsVisibleWhileTheRunIsStillInFlight()
    {
        // A host polling a session mid-run must see the totals of every call that already ended.
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(
            onGetStreaming: index => index == 0
                ? ToolRoundStream("call_1", 10_000, 10)
                : GatedFinalRound(entered, gate),
            metadataModelId: "metadata-model");
        var options = ToolLoopOptions(UsageExecutionPath.StreamingManualToolLoop);
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("mid-run-visibility");

        var ct = TestContext.Current.CancellationToken;
        // The execution handle is retained OUTSIDE the try so the cleanup below can always join it,
        // even when an assertion fails while the execution is still parked on the gate.
        var execution = Task.Run(async () =>
        {
            await foreach (var _ in agent.ExecuteStreamingAsync(session, "Do the thing", ct)) { }
        }, ct);

        try
        {
            // Wait until round 2 is provably parked inside its model call, then release it.
            await entered.Task;
            Assert.Equal(1, session.Usage.Total.Calls);
            Assert.Equal(10_000, session.InputTokensUsed);
            Assert.Equal(10, session.OutputTokensUsed);

            gate.TrySetResult(true);
            await execution;

            Assert.Equal(2, session.Usage.Total.Calls);
            Assert.Equal(25_000, session.InputTokensUsed);
            Assert.Equal(30, session.OutputTokensUsed);
        }
        finally
        {
            // Release the gate AND join the execution on every exit path, so no model or
            // execution work can run on past the assertion that failed (or past the test itself).
            gate.TrySetResult(true);
            try
            {
                await execution;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The test was cancelled; there is no execution outcome left to observe.
            }
        }
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task NonStreamingHttpFailure_StillPropagates(UsageExecutionPath path)
    {
        // The recorder must not change exception behavior: an HttpRequestException still propagates
        // to the caller instead of being converted into an Error result.
        var client = new ScriptedChatClient(
            onGetResponse: _ => throw new HttpRequestException("transport down"),
            onGetStreaming: _ => StreamThrowsBeforeUsage(new HttpRequestException("transport down")),
            metadataModelId: "metadata-model");
        var options = path == UsageExecutionPath.StreamingManualToolLoop ? ToolLoopOptions(path) : MinimalOptions();
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("http-failure-" + path);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RunAsync(path, agent, session, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task CallerCancellation_StillPropagates(UsageExecutionPath path)
    {
        // The failed attempt is accounted for, yet the caller still observes cancellation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new ScriptedChatClient(
            onGetResponse: _ => Task.FromException<ChatResponse>(new OperationCanceledException(cts.Token)),
            onGetStreaming: _ => StreamThrowsBeforeUsage(new OperationCanceledException(cts.Token)),
            metadataModelId: "metadata-model");
        var options = path == UsageExecutionPath.StreamingManualToolLoop ? ToolLoopOptions(path) : MinimalOptions();
        // Never dispose the agent on a cancelled run: the recorder still records the ended call, and
        // disposal of the (unstarted) sub-agent machinery is irrelevant here.
        var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("cancelled-" + path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(path, agent, session, cts.Token));

        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(0, session.InputTokensUsed);
    }

    [Fact]
    public async Task ExecuteAsync_ErrorAfterSuccessfulRound_CarriesUsageRecordedBeforeTheError()
    {
        // Round 1 completes with usage; round 2 fails, so the Error result must still carry round 1.
        var client = new ScriptedChatClient(
            onGetResponse: index => index == 0
                ? Task.FromResult(ToolCallResponse("call_1", "do_thing", 10_000, 10))
                : throw new InvalidOperationException("second round boom"),
            metadataModelId: "metadata-model");
        var options = ToolLoopOptions(UsageExecutionPath.ExecuteAsync);
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("error-after-round");

        var result = await agent.ExecuteAsync(session, "Do the thing", TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(10_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(10, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(10_000, session.InputTokensUsed);
        Assert.Equal(2, session.Usage.Total.Calls);
    }

    [Fact]
    public async Task StreamingDefault_ErrorAfterSuccessfulRound_CarriesUsageRecordedBeforeTheError()
    {
        var client = new ScriptedChatClient(
            onGetStreaming: index => index == 0
                ? ToolRoundStream("call_1", 10_000, 10)
                : StreamThrowsBeforeUsage(new InvalidOperationException("second round boom")),
            metadataModelId: "metadata-model");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("stream-default-error-after-round");

        var result = await RunAsync(UsageExecutionPath.StreamingDefault, agent, session,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error", result.Status);
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(10_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(10, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(2, session.Usage.Total.Calls);
        Assert.Equal(10_000, session.InputTokensUsed);
    }

    [Theory]
    [InlineData(UsageExecutionPath.ExecuteAsync)]
    [InlineData(UsageExecutionPath.StreamingDefault)]
    [InlineData(UsageExecutionPath.StreamingManualToolLoop)]
    public async Task MaxStepsReached_CarriesEveryRecordedRound(UsageExecutionPath path)
    {
        // Every round proposes another tool call, so the run exhausts MaxSteps = 1 on all three
        // paths (two model calls in total) and terminates as MaxStepsReached — the result must
        // still carry the usage of every round that actually ran.
        var client = AlwaysToolCallingClient(input: 10_000, output: 10);
        var options = ToolLoopOptions(path);
        options.MaxSteps = 1;
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("max-steps-usage-" + path);

        var result = await RunAsync(path, agent, session, TestContext.Current.CancellationToken);

        Assert.Equal("MaxStepsReached", result.Status);
        Assert.Equal(2, result.TokenUsage.Total.Calls);
        Assert.Equal(20_000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(20, result.TokenUsage.Total.OutputTokens);
        Assert.Equal(2, session.Usage.Total.Calls);
        Assert.Equal(20_000, session.InputTokensUsed);
        Assert.Equal(20, session.OutputTokensUsed);
        Assert.Equal(20_000, session.Usage.Total.InputTokens);
    }

    // ========================================================================
    // 8. Isolation and concurrency
    // ========================================================================

    [Fact]
    public async Task OverlappingExecutionsOnOneAgent_KeepSeparateResultAndSessionUsage()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new ScriptedChatClient(
            onGetResponse: index =>
            {
                if (index == 0)
                {
                    firstCallEntered.TrySetResult(true);
                    return WaitForGateAsync(gate, TextResponse("first", 111, 1));
                }

                return Task.FromResult(TextResponse("second", 222, 2));
            },
            metadataModelId: "metadata-model");

        await using var agent = new CodingAgent(client, MinimalOptions());
        var sessionA = AgentSession.Create("overlap-a");
        var sessionB = AgentSession.Create("overlap-b");

        // Retained outside the try so the cleanup can join execution A even when an assertion
        // fails while A is still parked inside its model call.
        Task<AgentResult>? firstExecution = null;

        try
        {
            firstExecution = agent.ExecuteAsync(sessionA, "first", TestContext.Current.CancellationToken);
            await firstCallEntered.Task;

            // Execution A is provably inside its model call and has recorded nothing yet.
            Assert.Empty(sessionA.Usage.Entries);
            Assert.Equal(0, sessionA.InputTokensUsed);

            var secondResult = await agent.ExecuteAsync(sessionB, "second", TestContext.Current.CancellationToken);
            Assert.Equal(222, secondResult.TokenUsage.Total.InputTokens);
            Assert.Equal(1, secondResult.TokenUsage.Total.Calls);
            Assert.Equal(222, sessionB.InputTokensUsed);
            Assert.Equal(1, sessionB.Usage.Total.Calls);

            // B's usage never leaked into A's session while A is still in flight.
            Assert.Equal(0, sessionA.InputTokensUsed);
            Assert.Equal(0, sessionA.Usage.Total.Calls);

            gate.TrySetResult(true);
            var firstResult = await firstExecution;

            Assert.Equal(111, firstResult.TokenUsage.Total.InputTokens);
            Assert.Equal(1, firstResult.TokenUsage.Total.Calls);
            Assert.Equal(111, sessionA.InputTokensUsed);
            Assert.Equal(1, sessionA.Usage.Total.Calls);

            // Each snapshot reflects only its own execution.
            Assert.NotSame(firstResult.TokenUsage, secondResult.TokenUsage);
            Assert.Equal(1, secondResult.TokenUsage.Total.Calls);
            Assert.Equal(222, secondResult.TokenUsage.Total.InputTokens);
        }
        finally
        {
            // Release the gate AND join the retained execution A on every exit path, so a failed
            // assertion cannot leave model/execution work running into agent disposal.
            gate.TrySetResult(true);
            if (firstExecution is not null)
            {
                try
                {
                    await firstExecution;
                }
                catch (OperationCanceledException)
                {
                    // The caller's cancellation token was cancelled; no outcome left to observe.
                }
            }
        }
    }

    private static async Task<ChatResponse> WaitForGateAsync(TaskCompletionSource<bool> gate, ChatResponse response)
    {
        await gate.Task.ConfigureAwait(false);
        return response;
    }

    [Fact]
    public async Task UsageSummary_ConcurrentAdds_LoseNoUpdates()
    {
        var summary = new UsageSummary();
        const int writers = 4;
        const int eventsPerWriter = 250;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var writers2 = new List<Task>();
        for (var writer = 0; writer < writers; writer++)
        {
            var writerIndex = writer;
            writers2.Add(Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var i = 0; i < eventsPerWriter; i++)
                {
                    summary.Add(new UsageEvent(UsageSource.Agent, "model-" + ((writerIndex + i) % 3), new TokenUsage
                    {
                        InputTokens = 1,
                        OutputTokens = 2,
                        CachedInputTokens = 1,
                        ReasoningTokens = 1,
                        Calls = 1,
                        CachedInputReportedCalls = 1,
                        ReasoningReportedCalls = 1
                    }));
                }
            }, TestContext.Current.CancellationToken));
        }

        start.SetResult(true);
        await Task.WhenAll(writers2);

        var expectedCalls = writers * eventsPerWriter;
        Assert.Equal(expectedCalls, summary.Total.Calls);
        Assert.Equal(expectedCalls, summary.Total.InputTokens);
        Assert.Equal(expectedCalls * 2, summary.Total.OutputTokens);
        Assert.Equal(expectedCalls, summary.Total.CachedInputReportedCalls);
        Assert.Equal(expectedCalls, summary.Total.ReasoningReportedCalls);
        Assert.Equal(3, summary.Entries.Count);
        Assert.Equal(expectedCalls, summary.Entries.Sum(e => (long)e.Usage.Calls));
    }

    // ========================================================================
    // 9. AgentSession persistence
    // ========================================================================

    [Fact]
    public async Task AgentSession_Usage_SurvivesSaveAndLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AgentSession.Create("usage-roundtrip");
        session.InputTokensUsed = 107;
        session.OutputTokensUsed = 7;
        session.Usage.Add(new UsageEvent(UsageSource.Agent, "model-a", new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 5,
            CachedInputTokens = 40,
            ReasoningTokens = 3,
            Calls = 1,
            CachedInputReportedCalls = 1,
            ReasoningReportedCalls = 1
        }));
        session.Usage.Add(new UsageEvent(UsageSource.Compaction, "model-b", new TokenUsage { InputTokens = 7, OutputTokens = 2, Calls = 1 }));

        var path = Path.Combine(Path.GetTempPath(), $"usage-session-{Guid.NewGuid()}.json");
        try
        {
            await session.SaveAsync(path, ct);
            var loaded = await AgentSession.LoadAsync(path, ct);

            var entries = loaded.Usage.Entries.OrderBy(e => e.Model, StringComparer.Ordinal).ToList();
            Assert.Equal(2, entries.Count);

            Assert.Equal(UsageSource.Agent, entries[0].Source);
            Assert.Equal("model-a", entries[0].Model);
            Assert.Equal(1, entries[0].Usage.Calls);
            Assert.Equal(100, entries[0].Usage.InputTokens);
            Assert.Equal(5, entries[0].Usage.OutputTokens);
            Assert.Equal(40, entries[0].Usage.CachedInputTokens);
            Assert.Equal(1, entries[0].Usage.CachedInputReportedCalls);
            Assert.Equal(3, entries[0].Usage.ReasoningTokens);
            Assert.Equal(1, entries[0].Usage.ReasoningReportedCalls);

            Assert.Equal(UsageSource.Compaction, entries[1].Source);
            Assert.Equal("model-b", entries[1].Model);
            Assert.Equal(7, entries[1].Usage.InputTokens);

            Assert.Equal(107, loaded.Usage.Total.InputTokens);
            Assert.Equal(7, loaded.Usage.Total.OutputTokens);
            Assert.Equal(2, loaded.Usage.Total.Calls);

            // The legacy cumulative counters are preserved alongside the summary.
            Assert.Equal(107, loaded.InputTokensUsed);
            Assert.Equal(7, loaded.OutputTokensUsed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AgentSession_LegacyJsonWithoutUsageField_LoadsEmptySummaryAndPreservedCounters()
    {
        var ct = TestContext.Current.CancellationToken;
        const string legacyJson = """
        {
          "sessionId": "legacy-usage-session",
          "messages": [],
          "totalToolCalls": 3,
          "inputTokensUsed": 123,
          "outputTokensUsed": 45,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "lastActivityAt": "2026-01-02T00:00:00+00:00",
          "lastKnownContextTokens": 999
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"legacy-session-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, legacyJson, ct);
            var loaded = await AgentSession.LoadAsync(path, ct);

            Assert.Equal("legacy-usage-session", loaded.SessionId);
            Assert.Empty(loaded.Usage.Entries);
            Assert.Equal(0, loaded.Usage.Total.Calls);
            Assert.Equal(123, loaded.InputTokensUsed);
            Assert.Equal(45, loaded.OutputTokensUsed);
            Assert.Equal(3, loaded.TotalToolCalls);
            Assert.Equal(999, loaded.LastKnownContextTokens);

            // Re-saving writes the new field, and the summary stays empty.
            await loaded.SaveAsync(path, ct);
            var reloaded = await AgentSession.LoadAsync(path, ct);
            Assert.Empty(reloaded.Usage.Entries);
            Assert.Equal(123, reloaded.InputTokensUsed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AgentSession_Fork_ResetsUsage()
    {
        var session = AgentSession.Create("fork-usage-source");
        session.InputTokensUsed = 50;
        session.OutputTokensUsed = 5;
        session.Usage.Add(Event(UsageSource.Agent, "model-a", input: 50, output: 5));
        Assert.Equal(1, session.Usage.Total.Calls);

        var forked = session.Fork("fork-usage-target");

        Assert.Empty(forked.Usage.Entries);
        Assert.Equal(0, forked.Usage.Total.Calls);
        Assert.Equal(0, forked.InputTokensUsed);
        Assert.Equal(0, forked.OutputTokensUsed);

        // The original is untouched.
        Assert.Equal(1, session.Usage.Total.Calls);
        Assert.Equal(50, session.InputTokensUsed);
    }

    // ========================================================================
    // Shared helpers
    // ========================================================================

    private static AgentOptions MinimalOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent."
    };

    private static UsageEvent Event(UsageSource source, string? model, long input, long output)
        => new UsageEvent(source, model, new TokenUsage { InputTokens = input, OutputTokens = output, Calls = 1 });
}

