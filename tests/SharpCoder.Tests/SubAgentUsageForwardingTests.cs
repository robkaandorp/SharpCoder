using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoder;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Deterministic tests for sub-agent usage forwarding: every model call a sub-agent makes is
/// forwarded — live, exactly once — to the usage accounting of the parent execution whose
/// <c>start_sub_agent</c> tool call started it, labelled with the sub-agent's id and the child
/// client's model, and mapped to <see cref="UsageSource.SubAgent"/> (or
/// <see cref="UsageSource.SubAgentCompaction"/> for a compaction call inside the child).
/// <para>
/// Covered: both parent execution paths (<see cref="CodingAgent.ExecuteAsync(AgentSession, string, CancellationToken)"/>
/// and <c>ExecuteStreamingAsync</c> with <c>ShowToolCallsInStream = true</c>), the child
/// <see cref="AgentOptions.OnUsage"/> forwarder wiring, the source mapping function, the
/// manager-level fallback recorder of a host-initiated <c>StartAsync</c>, the running
/// <see cref="SubAgentInfo.Usage"/> totals (including a cancelled call), late calls that end after
/// the parent execution returned, deterministic concurrent recording, and a throwing parent handler.
/// </para>
/// <para>
/// There is no wall-clock timing anywhere in this file: no delay, sleep, timeout or
/// <c>CancelAfter</c> is used as a synchronisation device. Every rendezvous is a
/// <see cref="TaskCompletionSource{TResult}"/> (or a <see cref="Barrier"/> for the concurrency
/// test); a call that must stay in flight until it is cancelled awaits a TCS registered on its own
/// cancellation token, so it can only end when that token fires.
/// </para>
/// </summary>
public class SubAgentUsageForwardingTests
{
    /// <summary>Model reported through the parent client's metadata.</summary>
    private const string ParentModel = "parent-model";

    /// <summary>Model reported through the child client's metadata (different from the parent's).</summary>
    private const string ChildModel = "child-model";

    /// <summary>The parent's per-round usage: three parent rounds give 300 input and 30 output.</summary>
    private const long ParentInputPerRound = 100;
    private const long ParentOutputPerRound = 10;

    /// <summary>The child's two-round usage: 10 + 15 input and 1 + 2 output.</summary>
    private const long ChildFirstInput = 10;
    private const long ChildFirstOutput = 1;
    private const long ChildSecondInput = 15;
    private const long ChildSecondOutput = 2;
    private const long ChildInputTotal = ChildFirstInput + ChildSecondInput;
    private const long ChildOutputTotal = ChildFirstOutput + ChildSecondOutput;

    /// <summary>The two parent execution paths that must both forward sub-agent usage.</summary>
    public enum ParentExecutionPath
    {
        /// <summary><see cref="CodingAgent.ExecuteAsync(AgentSession, string, CancellationToken)"/>.</summary>
        ExecuteAsync,

        /// <summary><c>ExecuteStreamingAsync</c> with <c>ShowToolCallsInStream = true</c> (manual tool loop).</summary>
        StreamingManualToolLoop
    }

    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>A single scripted parent round: a tool call, or the final text answer.</summary>
    private sealed class ParentRound
    {
        private ParentRound()
        {
        }

        internal string? ToolName { get; private init; }

        internal Dictionary<string, object?>? ToolArgs { get; private init; }

        internal string? Text { get; private init; }

        internal static ParentRound Call(string toolName, Dictionary<string, object?>? args = null)
            => new() { ToolName = toolName, ToolArgs = args };

        internal static ParentRound Say(string text) => new() { Text = text };
    }

    /// <summary>
    /// Parent chat client: replays a script of rounds in order on BOTH entry points, reports the
    /// same usage on every call, exposes <see cref="ParentModel"/> through
    /// <see cref="ChatClientMetadata"/>, and invokes <c>beforeRound</c> at the START of each round —
    /// before that round's request is served — so a test can hook production seams (for example
    /// <see cref="SubAgentManager.OnSubAgentOptionsCreated"/>) before any sub-agent can start.
    /// </summary>
    private sealed class ScriptedParentClient : IChatClient
    {
        private readonly IReadOnlyList<ParentRound> _rounds;
        private readonly Action<int>? _beforeRound;
        private int _responseCalls;
        private int _streamingCalls;
        private int _callIdSeed;

        internal ScriptedParentClient(IReadOnlyList<ParentRound> rounds, Action<int>? beforeRound = null)
        {
            _rounds = rounds;
            _beforeRound = beforeRound;
        }

        /// <summary>How many model calls the parent has served (both entry points combined).</summary>
        internal int CallCount => Volatile.Read(ref _responseCalls) + Volatile.Read(ref _streamingCalls);

        private ParentRound RoundFor(int index)
        {
            _beforeRound?.Invoke(index);
            return index < _rounds.Count ? _rounds[index] : ParentRound.Say("parent done");
        }

        private string NextCallId() => $"parent_call_{Interlocked.Increment(ref _callIdSeed)}";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _ = messages.ToList();
            var round = RoundFor(Interlocked.Increment(ref _responseCalls) - 1);

            if (round.ToolName is not null)
            {
                var message = new ChatMessage(ChatRole.Assistant,
                    new AIContent[]
                    {
                        new FunctionCallContent(NextCallId(), round.ToolName, round.ToolArgs ?? new Dictionary<string, object?>())
                    });
                return Task.FromResult(new ChatResponse(message)
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                    ModelId = ParentModel,
                    Usage = Details(ParentInputPerRound, ParentOutputPerRound)
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "parent done"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = ParentModel,
                Usage = Details(ParentInputPerRound, ParentOutputPerRound)
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToList();
            var round = RoundFor(Interlocked.Increment(ref _streamingCalls) - 1);
            await Task.Yield();

            if (round.ToolName is not null)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = ParentModel,
                    Contents =
                    [
                        new FunctionCallContent(NextCallId(), round.ToolName, round.ToolArgs ?? new Dictionary<string, object?>())
                    ]
                };
                yield return ParentUsageUpdate();
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
                yield break;
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = ParentModel,
                Contents = [new TextContent(round.Text ?? "parent done")]
            };
            yield return ParentUsageUpdate();
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("parent-provider", new Uri("https://example.invalid"), ParentModel)
                : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Child chat client: each call index is served by a caller-supplied delegate, so a test
    /// controls per-round usage, tool calls and gating. Reports its own model through
    /// <see cref="ChatClientMetadata"/>, which is what the recorded child calls are attributed to.
    /// </summary>
    private sealed class ScriptedChildClient : IChatClient
    {
        private readonly IReadOnlyList<Func<int, CancellationToken, Task<ChatResponse>>> _rounds;
        private int _calls;

        internal ScriptedChildClient(params Func<int, CancellationToken, Task<ChatResponse>>[] rounds)
            => _rounds = rounds;

        /// <summary>How many model calls the child has served.</summary>
        internal int CallCount => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _ = messages.ToList();
            var index = Interlocked.Increment(ref _calls) - 1;
            return index < _rounds.Count
                ? _rounds[index](index, cancellationToken)
                : Task.FromResult(TextChildResponse("child done", 0, 0));
        }

        /// <summary>The child runs through <see cref="CodingAgent.ExecuteAsync"/>, which never streams.</summary>
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A sub-agent run never streams.");

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("child-provider", new Uri("https://example.invalid"), ChildModel)
                : null;

        public void Dispose() { }
    }

    // ========================================================================
    // Child round builders
    // ========================================================================

    /// <summary>A child round that proposes a registered child tool call and reports usage.</summary>
    private static Func<int, CancellationToken, Task<ChatResponse>> ChildToolRound(
        string toolName, Dictionary<string, object?> args, long input, long output)
        => (_, _) => Task.FromResult(ToolChildResponse(toolName, args, input, output));

    /// <summary>A child round that finishes the run with text and reports usage.</summary>
    private static Func<int, CancellationToken, Task<ChatResponse>> ChildTextRound(string text, long input, long output)
        => (_, _) => Task.FromResult(TextChildResponse(text, input, output));

    /// <summary>
    /// A child round that signals entry and then stays in flight until its own token is cancelled —
    /// the "must stay in flight until cancelled" shape, awaited on a TCS registered on that token,
    /// so it can end only when the token fires (never on a wall-clock timeout).
    /// </summary>
    private static Func<int, CancellationToken, Task<ChatResponse>> ChildRoundParkedUntilCancelled(
        TaskCompletionSource<bool> entered)
        => async (_, ct) =>
        {
            entered.TrySetResult(true);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                await cancelled.Task.ConfigureAwait(false);
            }

            throw new OperationCanceledException(ct);
        };

    /// <summary>
    /// A child round that signals entry and stays in flight until the test releases the gate.
    /// <para>
    /// The wait is cancellation-aware: the call also ends when its own token is cancelled, so a later
    /// failure inside the test — which unwinds into the agent's disposal, cancelling the sub-agent —
    /// can never park here and turn that failure into a hang. Cancellation is not a synchronisation
    /// device: only the test's explicit release lets the call return a response, and no clock is
    /// involved on either path.
    /// </para>
    /// </summary>
    private static Func<int, CancellationToken, Task<ChatResponse>> ChildRoundGated(
        TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, long input, long output)
        => async (_, ct) =>
        {
            entered.TrySetResult(true);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(release.Task, cancelled.Task).ConfigureAwait(false);
                if (ReferenceEquals(finished, cancelled.Task))
                {
                    ct.ThrowIfCancellationRequested();
                }
            }

            ct.ThrowIfCancellationRequested();
            return TextChildResponse("late child answer", input, output);
        };

    // ========================================================================
    // Response / update builders
    // ========================================================================

    private static UsageDetails Details(long? input, long? output)
        => new UsageDetails { InputTokenCount = input, OutputTokenCount = output };

    private static ChatResponseUpdate ParentUsageUpdate()
        => new ChatResponseUpdate
        {
            ModelId = ParentModel,
            Contents = [new UsageContent(Details(ParentInputPerRound, ParentOutputPerRound))]
        };

    private static ChatResponse ToolChildResponse(
        string toolName, Dictionary<string, object?> args, long input, long output)
    {
        var callId = $"child_call_{Guid.NewGuid():N}";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            new AIContent[] { new FunctionCallContent(callId, toolName, args) }))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = ChildModel,
            Usage = Details(input, output)
        };
    }

    private static ChatResponse TextChildResponse(string text, long input, long output)
        => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = ChildModel,
            Usage = Details(input, output)
        };

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>
    /// A temporary working directory containing one readable file, so a child's two-round tool loop
    /// (round 1 proposes <c>read_file</c>, round 2 answers) always resolves to a real tool result.
    /// </summary>
    private sealed class TempWorkDir : IDisposable
    {
        internal TempWorkDir(string fileName = "note.txt", string content = "hello world")
        {
            Dir = Path.Combine(Path.GetTempPath(), "subagent-usage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, fileName), content);
        }

        internal string Dir { get; }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AgentOptions ParentOptions(string workDir, bool fileOps = false) => new()
    {
        WorkDirectory = workDir,
        MaxSteps = 25,
        EnableBash = false,
        EnableFileOps = fileOps,
        EnableFileWrites = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
    };

    /// <summary>
    /// Builds the parent options for one execution path. This is where the path is really selected:
    /// the manual tool loop is entered only when <see cref="AgentOptions.ShowToolCallsInStream"/> is
    /// <c>true</c> (<see cref="CodingAgent.ExecuteStreamingAsync"/> branches on the property), so the
    /// streaming case sets it explicitly instead of relying on the property's default. Setting it
    /// here is what makes the <see cref="ParentExecutionPath.StreamingManualToolLoop"/> case exercise
    /// <c>CodingAgent.StreamWithToolCallsAsync</c> rather than the default streaming path.
    /// </summary>
    private static AgentOptions ParentOptions(string workDir, ParentExecutionPath path, bool fileOps = false)
    {
        var options = ParentOptions(workDir, fileOps);
        options.ShowToolCallsInStream = path == ParentExecutionPath.StreamingManualToolLoop;
        return options;
    }

    private static SubAgentOptions ChildOptions(ScriptedChildClient child) => new()
    {
        DefaultClient = child,
        DefaultEnableFileOps = true
    };

    /// <summary>The parent's standard script: start a sub-agent, await it, then answer.</summary>
    private static ParentRound[] StandardParentRounds() =>
    [
        ParentRound.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "read the note" }),
        ParentRound.Call("await_sub_agents"),
        ParentRound.Say("parent done"),
    ];

    /// <summary>A child whose first call proposes <c>read_file</c> and whose second answers.</summary>
    private static ScriptedChildClient TwoRoundChild() => new(
        ChildToolRound("read_file", new Dictionary<string, object?> { ["file_path"] = "note.txt" },
            ChildFirstInput, ChildFirstOutput),
        ChildTextRound("child answer", ChildSecondInput, ChildSecondOutput));

    /// <summary>
    /// Runs the parent through the given path and returns the completed result. Every streaming update
    /// is handed to <paramref name="observeUpdate"/> (when supplied) as it arrives, so a streaming cell
    /// can prove which branch actually produced the run.
    /// </summary>
    private static async Task<AgentResult> RunParentAsync(
        ParentExecutionPath path,
        CodingAgent agent,
        AgentSession session,
        CancellationToken ct,
        Action<StreamingUpdate>? observeUpdate = null)
    {
        if (path == ParentExecutionPath.ExecuteAsync)
            return await agent.ExecuteAsync(session, "delegate the work", ct);

        AgentResult? result = null;
        await foreach (var update in agent.ExecuteStreamingAsync(session, "delegate the work", ct))
        {
            observeUpdate?.Invoke(update);
            if (update.Kind == StreamingUpdateKind.Completed)
                result = update.Result;
        }

        Assert.NotNull(result);
        return result!;
    }

    private static IReadOnlyList<UsageEntry> EntriesFor(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).ToList();

    private static long CallsFor(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => (long)e.Usage.Calls);

    private static long InputFor(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => e.Usage.InputTokens);

    private static long OutputFor(UsageSummary summary, UsageSource source)
        => summary.Entries.Where(e => e.Source == source).Sum(e => e.Usage.OutputTokens);

    /// <summary>
    /// Waits for an entry rendezvous that the production code signals, observing the TEST's
    /// cancellation token as well so a failure elsewhere can never leave the test waiting on a signal
    /// that will not come. This is hang protection only: the wait still ends solely on the signal (or
    /// on cancellation), never on a clock, and it is not used as a synchronisation device.
    /// </summary>
    private static async Task AwaitRendezvousAsync(Task signal, CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
        {
            await signal.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            var finished = await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, signal)) ct.ThrowIfCancellationRequested();
        }

        await signal.ConfigureAwait(false);
    }

    /// <summary>
    /// A stable "Source=count" rendering of the events seen for every <see cref="UsageSource"/>,
    /// so a test can pin the whole source breakdown in one assertion and a mis-sourced event cannot
    /// hide behind an aggregate.
    /// </summary>
    private static string SourceBreakdown(IEnumerable<UsageEvent> events)
    {
        var list = events.ToList();
        return string.Join(", ", Enum.GetValues<UsageSource>()
            .Select(source => $"{source}={list.Count(e => e.Source == source)}"));
    }

    // ========================================================================
    // 1. End to end, on both execution paths
    // ========================================================================

    [Theory]
    [InlineData(ParentExecutionPath.ExecuteAsync)]
    [InlineData(ParentExecutionPath.StreamingManualToolLoop)]
    public async Task EndToEnd_BothExecutionPaths_ForwardEveryChildCallExactlyOnce(ParentExecutionPath path)
    {
        using var work = new TempWorkDir();
        var child = TwoRoundChild();

        var events = new ConcurrentQueue<UsageEvent>();
        var options = ParentOptions(work.Dir, path, fileOps: true);
        options.OnUsage = events.Enqueue;
        options.SubAgents = ChildOptions(child);

        // The path really is selected by the options, not by the enum member's name: the manual tool
        // loop only runs when this flag is on.
        Assert.Equal(path == ParentExecutionPath.StreamingManualToolLoop, options.ShowToolCallsInStream);

        var parent = new ScriptedParentClient(StandardParentRounds());
        await using var agent = new CodingAgent(parent, options);
        var session = AgentSession.Create("forwarding-" + path);

        var updates = new List<StreamingUpdate>();
        var result = await RunParentAsync(path, agent, session, TestContext.Current.CancellationToken, updates.Add);

        Assert.Equal("Success", result.Status);
        Assert.Equal(3, parent.CallCount);
        Assert.Equal(2, child.CallCount);

        // ---- The manual tool loop really ran (not the default streaming path) ----
        // StreamWithToolCallsAsync is the ONLY code path that mirrors tool invocations into the text
        // stream, writing markdown inline-code lines with the 🔧 marker followed by a blockquote with
        // the result's first line. Requiring that mirror for the streaming cell is what distinguishes
        // it from the default streaming path: with ShowToolCallsInStream left at its default the
        // deltas below would be empty and this case would silently re-test the default path.
        var mirroredText = string.Concat(updates.Where(u => u.Kind == StreamingUpdateKind.TextDelta).Select(u => u.Text));
        if (path == ParentExecutionPath.StreamingManualToolLoop)
        {
            Assert.Contains("\U0001F527 start_sub_agent(", mirroredText);
            Assert.Contains("\U0001F527 await_sub_agents(", mirroredText);
            Assert.Contains("\U0001F527", mirroredText);
        }
        else
        {
            // ExecuteAsync is not a streaming entry point at all, so no update can be observed.
            Assert.Empty(updates);
        }

        // ---- OnUsage: exactly two forwarded events, child model, sub-agent id ----
        // Filtering per source is what makes this a forwarding proof: a wrongly sourced event (for
        // example a child call reported as a parent Agent round) leaves `forwarded` empty and shows
        // up below in the parent-source count instead of being hidden by a total-only check.
        Assert.Equal(
            "Agent=3, Compaction=0, SubAgent=2, SubAgentCompaction=0",
            SourceBreakdown(events));
        var forwarded = events.Where(e => e.Source == UsageSource.SubAgent).ToList();
        Assert.Equal(2, forwarded.Count);
        Assert.All(forwarded, e => Assert.Equal("sub-1", e.SubAgentId));
        Assert.All(forwarded, e => Assert.Equal(ChildModel, e.Model));
        Assert.All(forwarded, e => Assert.Equal(1, e.Usage.Calls));
        Assert.Equal(ChildInputTotal, forwarded.Sum(e => e.Usage.InputTokens));
        Assert.Equal(ChildOutputTotal, forwarded.Sum(e => e.Usage.OutputTokens));

        // The parent's own rounds are reported under their own source and model, untouched: exactly
        // one event per parent round, and none of them carries a sub-agent id.
        var parentEvents = events.Where(e => e.Source != UsageSource.SubAgent).ToList();
        Assert.Equal(3, parentEvents.Count);
        Assert.All(parentEvents, e => Assert.Equal(UsageSource.Agent, e.Source));
        Assert.All(parentEvents, e => Assert.Equal(ParentModel, e.Model));
        Assert.All(parentEvents, e => Assert.Null(e.SubAgentId));

        // ---- AgentResult.TokenUsage: a (SubAgent, child model) entry summing the child's calls ----
        var subAgentEntry = Assert.Single(EntriesFor(result.TokenUsage, UsageSource.SubAgent));
        Assert.Equal(ChildModel, subAgentEntry.Model);
        Assert.Equal(2, subAgentEntry.Usage.Calls);
        Assert.Equal(ChildInputTotal, subAgentEntry.Usage.InputTokens);
        Assert.Equal(ChildOutputTotal, subAgentEntry.Usage.OutputTokens);

        // ...and the parent's own Agent entry holds only the parent's three rounds.
        var parentEntry = Assert.Single(EntriesFor(result.TokenUsage, UsageSource.Agent));
        Assert.Equal(ParentModel, parentEntry.Model);
        Assert.Equal(3, parentEntry.Usage.Calls);
        Assert.Equal(ParentInputPerRound * 3, parentEntry.Usage.InputTokens);
        Assert.Equal(ParentOutputPerRound * 3, parentEntry.Usage.OutputTokens);

        Assert.Equal(5, result.TokenUsage.Total.Calls);
        Assert.Equal(ParentInputPerRound * 3 + ChildInputTotal, result.TokenUsage.Total.InputTokens);
        Assert.Equal(ParentOutputPerRound * 3 + ChildOutputTotal, result.TokenUsage.Total.OutputTokens);

        // ---- Session: the child's tokens are included ----
        Assert.Equal(ParentInputPerRound * 3 + ChildInputTotal, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 3 + ChildOutputTotal, session.OutputTokensUsed);
        Assert.Equal(2, CallsFor(session.Usage, UsageSource.SubAgent));
        Assert.Equal(ChildInputTotal, InputFor(session.Usage, UsageSource.SubAgent));
        Assert.Equal(ChildOutputTotal, OutputFor(session.Usage, UsageSource.SubAgent));
        Assert.Equal(3, CallsFor(session.Usage, UsageSource.Agent));

        // ---- The sub-agent itself is tracked, terminated and carries its own totals ----
        var manager = agent.ActiveSubAgentManager;
        Assert.NotNull(manager);
        var final = manager!.GetStatus("sub-1");
        Assert.Single(final);
        Assert.Equal(SubAgentStatus.Completed, final[0].Status);
        Assert.Equal("child answer", final[0].Summary);
        Assert.Equal(ChildInputTotal, final[0].InputTokens);
        Assert.Equal(ChildOutputTotal, final[0].OutputTokens);
        Assert.Equal(2, final[0].Usage!.Calls);
    }

    // ========================================================================
    // 2. The child's own OnUsage is a forwarder, never the parent's delegate
    // ========================================================================

    [Fact]
    public async Task ChildOptions_GetTheirOwnForwarder_NotTheParentDelegate()
    {
        using var work = new TempWorkDir();
        var child = TwoRoundChild();

        var events = new ConcurrentQueue<UsageEvent>();
        var options = ParentOptions(work.Dir, fileOps: true);
        options.OnUsage = events.Enqueue;
        options.SubAgents = ChildOptions(child);

        // The seam is installed at the START of the parent's first round — after the sub-agent tools
        // (and therefore the lazily created manager) exist, but strictly before any sub-agent can
        // start, so the child's options are captured deterministically.
        AgentOptions? childOptions = null;
        CodingAgent? agentRef = null;
        var parent = new ScriptedParentClient(StandardParentRounds(), beforeRound: index =>
        {
            if (index != 0) return;

            // Building the parent's first request has already created the manager and its tools, so
            // no sub-agent can be started before this hook is installed.
            Assert.NotNull(agentRef);
            Assert.NotNull(agentRef!.ActiveSubAgentManager);
            agentRef.ActiveSubAgentManager!.OnSubAgentOptionsCreated = o => childOptions = o;
        });

        var agent = new CodingAgent(parent, options);
        agentRef = agent;
        await using var _ = agent;
        var session = AgentSession.Create("child-forwarder");

        var result = await agent.ExecuteAsync(session, "delegate the work", TestContext.Current.CancellationToken);

        Assert.True(result.Status == "Success",
            $"the parent run must succeed; status={result.Status}, message={result.Message}");
        Assert.NotNull(childOptions);
        Assert.NotNull(childOptions!.OnUsage);

        // The child's handler is a DIFFERENT delegate instance from the one stored on the parent's
        // options (the very instance the parent recorder reads per call), so the parent's handler was
        // not simply handed down to the child.
        Assert.NotSame(options.OnUsage, childOptions.OnUsage);

        // The parent's execution itself already recorded its three rounds plus the child's two.
        Assert.Equal(ParentInputPerRound * 3 + ChildInputTotal, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 3 + ChildOutputTotal, session.OutputTokensUsed);

        // The captured delegate really is the forwarding seam: invoking it with a child event
        // reaches the PARENT recorder (session + OnUsage) mapped to the sub-agent.
        childOptions.OnUsage!(new UsageEvent(
            UsageSource.Compaction, "child-compaction-model",
            new TokenUsage { InputTokens = 5, OutputTokens = 6, Calls = 1 }));

        var compactionEvent = Assert.Single(events, e => e.Source == UsageSource.SubAgentCompaction);
        Assert.Equal("sub-1", compactionEvent.SubAgentId);
        Assert.Equal("child-compaction-model", compactionEvent.Model);
        Assert.Equal(5, compactionEvent.Usage.InputTokens);
        Assert.Equal(6, compactionEvent.Usage.OutputTokens);

        Assert.Equal(ParentInputPerRound * 3 + ChildInputTotal + 5, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 3 + ChildOutputTotal + 6, session.OutputTokensUsed);
        Assert.Equal(6, OutputFor(session.Usage, UsageSource.SubAgentCompaction));

        // ...and it also joins that sub-agent's own running total.
        var childInfo = Assert.Single(agent.ActiveSubAgentManager!.GetStatus("sub-1"));
        Assert.Equal(3, childInfo.Usage!.Calls);
        Assert.Equal(ChildInputTotal + 5, childInfo.InputTokens);
        Assert.Equal(ChildOutputTotal + 6, childInfo.OutputTokens);

        // The parent's own events were still delivered straight through the parent's delegate.
        Assert.Equal(3, events.Count(e => e.Source == UsageSource.Agent));
    }

    // ========================================================================
    // 3. The mapping function
    // ========================================================================

    [Fact]
    public void MapChildUsage_MapsAgentToSubAgent_AndCompactionToSubAgentCompaction()
    {
        var childUsage = new TokenUsage
        {
            InputTokens = 11,
            OutputTokens = 2,
            CachedInputTokens = 3,
            ReasoningTokens = 4,
            Calls = 1,
            CachedInputReportedCalls = 1,
            ReasoningReportedCalls = 1
        };

        var mappedAgent = SubAgentManager.MapChildUsage(
            "sub-7", new UsageEvent(UsageSource.Agent, ChildModel, childUsage.Clone()));

        Assert.Equal(UsageSource.SubAgent, mappedAgent.Source);
        Assert.Equal("sub-7", mappedAgent.SubAgentId);
        Assert.Equal(ChildModel, mappedAgent.Model);
        Assert.Equal(11, mappedAgent.Usage.InputTokens);
        Assert.Equal(2, mappedAgent.Usage.OutputTokens);
        Assert.Equal(3, mappedAgent.Usage.CachedInputTokens);
        Assert.Equal(4, mappedAgent.Usage.ReasoningTokens);
        Assert.Equal(1, mappedAgent.Usage.Calls);
        Assert.Equal(1, mappedAgent.Usage.CachedInputReportedCalls);
        Assert.Equal(1, mappedAgent.Usage.ReasoningReportedCalls);

        var mappedCompaction = SubAgentManager.MapChildUsage(
            "sub-7", new UsageEvent(UsageSource.Compaction, "child-compaction-model", childUsage.Clone()));

        Assert.Equal(UsageSource.SubAgentCompaction, mappedCompaction.Source);
        Assert.Equal("sub-7", mappedCompaction.SubAgentId);
        Assert.Equal("child-compaction-model", mappedCompaction.Model);
        Assert.Equal(11, mappedCompaction.Usage.InputTokens);
        Assert.Equal(2, mappedCompaction.Usage.OutputTokens);
        Assert.Equal(1, mappedCompaction.Usage.Calls);

        // A child whose client exposes no metadata and whose response named no model stays unknown;
        // the id and the source mapping are unaffected.
        var mappedUnknownModel = SubAgentManager.MapChildUsage(
            "sub-7", new UsageEvent(UsageSource.Agent, null, childUsage.Clone()));
        Assert.Null(mappedUnknownModel.Model);
        Assert.Equal(UsageSource.SubAgent, mappedUnknownModel.Source);
        Assert.Equal("sub-7", mappedUnknownModel.SubAgentId);
    }

    // ========================================================================
    // 4. Host-initiated StartAsync (no execution): the manager-level fallback recorder
    // ========================================================================

    [Fact]
    public async Task DirectStartAsync_WithNoExecution_ForwardsToTheParentOptionsOnUsage()
    {
        using var work = new TempWorkDir();
        var child = TwoRoundChild();

        var events = new ConcurrentQueue<UsageEvent>();
        var parentOptions = ParentOptions(work.Dir, fileOps: true);
        parentOptions.OnUsage = events.Enqueue;

        // The public overload: a host starts a sub-agent with no parent execution to bind to, so the
        // manager's own fallback recorder is the sink.
        await using var manager = new SubAgentManager(ChildOptions(child), child, parentOptions, logger: null);
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "read the note" }, TestContext.Current.CancellationToken);
        var final = await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken);

        Assert.Equal("sub-1", info.Id);
        var childInfo = Assert.Single(final);
        Assert.Equal(SubAgentStatus.Completed, childInfo.Status);
        Assert.Equal(2, child.CallCount);

        var forwarded = events.Where(e => e.Source == UsageSource.SubAgent).ToList();
        Assert.Equal(2, forwarded.Count);
        Assert.All(forwarded, e => Assert.Equal("sub-1", e.SubAgentId));
        Assert.All(forwarded, e => Assert.Equal(ChildModel, e.Model));
        Assert.All(forwarded, e => Assert.Equal(1, e.Usage.Calls));
        Assert.Equal(ChildInputTotal, forwarded.Sum(e => e.Usage.InputTokens));
        Assert.Equal(ChildOutputTotal, forwarded.Sum(e => e.Usage.OutputTokens));

        Assert.Equal(2, childInfo.Usage!.Calls);
        Assert.Equal(ChildInputTotal, childInfo.Usage.InputTokens);
        Assert.Equal(ChildOutputTotal, childInfo.Usage.OutputTokens);
        Assert.Equal(ChildInputTotal, childInfo.InputTokens);
        Assert.Equal(ChildOutputTotal, childInfo.OutputTokens);
    }

    // ========================================================================
    // 5. SubAgentInfo totals
    // ========================================================================

    [Fact]
    public async Task SubAgentInfo_TwoRoundChild_ReportsTheRunningTotals()
    {
        using var work = new TempWorkDir();
        var child = TwoRoundChild();

        var parentOptions = ParentOptions(work.Dir, fileOps: true);
        await using var manager = new SubAgentManager(ChildOptions(child), child, parentOptions, logger: null);
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "read the note" }, TestContext.Current.CancellationToken);
        var childInfo = Assert.Single(await manager.AwaitAsync(new[] { info.Id }, TestContext.Current.CancellationToken));

        Assert.Equal(SubAgentStatus.Completed, childInfo.Status);
        Assert.Null(childInfo.Error);
        Assert.Equal(ChildInputTotal, childInfo.InputTokens);
        Assert.Equal(ChildOutputTotal, childInfo.OutputTokens);
        Assert.NotNull(childInfo.Usage);
        Assert.Equal(2, childInfo.Usage!.Calls);
        Assert.Equal(ChildInputTotal, childInfo.Usage.InputTokens);
        Assert.Equal(ChildOutputTotal, childInfo.Usage.OutputTokens);
    }

    [Fact]
    public async Task SubAgentInfo_CancelledMidSecondCall_CountsTheCancelledCallAndKeepsTheFirstCallsTokens()
    {
        using var work = new TempWorkDir();
        var secondCallEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new ScriptedChildClient(
            ChildToolRound("read_file", new Dictionary<string, object?> { ["file_path"] = "note.txt" },
                ChildFirstInput, ChildFirstOutput),
            ChildRoundParkedUntilCancelled(secondCallEntered));

        var events = new ConcurrentQueue<UsageEvent>();
        var parentOptions = ParentOptions(work.Dir, fileOps: true);
        parentOptions.OnUsage = events.Enqueue;

        await using var manager = new SubAgentManager(ChildOptions(child), child, parentOptions, logger: null);
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "read the note" }, TestContext.Current.CancellationToken);

        // The second call is provably in flight: the child's client signalled entry, and the first
        // call's usage was recorded before that call could be issued (the child's tool result has to
        // come back before round two starts), so the running total is observable mid-run. The
        // rendezvous observes the test token so a failure before the signal cannot wait indefinitely.
        await AwaitRendezvousAsync(secondCallEntered.Task, TestContext.Current.CancellationToken);

        var running = Assert.Single(manager.GetStatus(info.Id));
        Assert.Equal(SubAgentStatus.Running, running.Status);
        Assert.NotNull(running.Usage);
        Assert.Equal(1, running.Usage!.Calls);
        Assert.Equal(ChildFirstInput, running.Usage.InputTokens);
        Assert.Equal(ChildFirstOutput, running.Usage.OutputTokens);
        Assert.Equal(ChildFirstInput, running.InputTokens);
        Assert.Equal(ChildFirstOutput, running.OutputTokens);
        Assert.Single(events, e => e.Source == UsageSource.SubAgent);

        await manager.CancelAllAsync();

        var final = Assert.Single(manager.GetStatus(info.Id));
        Assert.Equal(SubAgentStatus.Cancelled, final.Status);
        Assert.NotNull(final.Usage);
        Assert.Equal(2, final.Usage!.Calls);
        Assert.Equal(ChildFirstInput, final.Usage.InputTokens);
        Assert.Equal(ChildFirstOutput, final.Usage.OutputTokens);
        Assert.Equal(ChildFirstInput, final.InputTokens);
        Assert.Equal(ChildFirstOutput, final.OutputTokens);

        // The cancelled call was forwarded live as well: one event per ended call, zero tokens for
        // the call that never returned a response.
        var forwarded = events.Where(e => e.Source == UsageSource.SubAgent).ToList();
        Assert.Equal(2, forwarded.Count);
        Assert.Equal(ChildFirstInput, forwarded.Sum(e => e.Usage.InputTokens));
        Assert.Equal(ChildFirstOutput, forwarded.Sum(e => e.Usage.OutputTokens));
        Assert.All(forwarded, e => Assert.Equal("sub-1", e.SubAgentId));
    }

    // ========================================================================
    // 6. Late calls: a call ending after the parent execution returned
    // ========================================================================

    [Fact]
    public async Task LateChildCall_ReachesTheSessionAndOnUsage_ButNeverChangesTheReturnedSnapshot()
    {
        using var work = new TempWorkDir();
        var childEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new ScriptedChildClient(ChildRoundGated(childEntered, release, input: 7, output: 8));

        var events = new ConcurrentQueue<UsageEvent>();
        var options = ParentOptions(work.Dir);
        options.OnUsage = events.Enqueue;
        options.SubAgents = ChildOptions(child);

        // The parent starts the sub-agent and returns WITHOUT awaiting it.
        var parent = new ScriptedParentClient(
        [
            ParentRound.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "slow work" }),
            ParentRound.Say("parent returned early"),
        ]);

        await using var agent = new CodingAgent(parent, options);
        var session = AgentSession.Create("late-call");
        var result = await agent.ExecuteAsync(session, "delegate the work", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal("parent returned early", result.Message);
        Assert.Equal(2, parent.CallCount);

        // The child's only call cannot have ended yet (it is parked on a gate only this test can
        // release), so nothing was forwarded: no sub-agent entry anywhere, and the session holds
        // exactly the parent's own two rounds.
        Assert.DoesNotContain(events, e => e.Source == UsageSource.SubAgent);
        Assert.Empty(EntriesFor(result.TokenUsage, UsageSource.SubAgent));
        Assert.Equal(0, CallsFor(result.TokenUsage, UsageSource.SubAgent));
        Assert.Equal(2, CallsFor(result.TokenUsage, UsageSource.Agent));
        Assert.Equal(ParentInputPerRound * 2, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 2, session.OutputTokensUsed);

        // Release the in-flight call, which therefore ends strictly after the parent execution
        // returned (the execution is already over at this point in the test). The rendezvous observes
        // the test token, so a failure before the child signals can never wait indefinitely.
        await AwaitRendezvousAsync(childEntered.Task, TestContext.Current.CancellationToken);

        var manager = agent.ActiveSubAgentManager;
        Assert.NotNull(manager);

        SubAgentInfo childInfo;
        UsageEvent forwarded;
        try
        {
            release.TrySetResult(true);
            childInfo = Assert.Single(await manager!.AwaitAsync(null, TestContext.Current.CancellationToken));
        }
        finally
        {
            // Always unblock the gated call BEFORE the agent is disposed below: disposal cancels and
            // awaits the sub-agent, so an unreleased gate would otherwise turn a later assertion
            // failure into a hang instead of a diagnostic. (The gated round is cancellation-aware as
            // well; this finally keeps the shutdown path identical on success and on failure.)
            release.TrySetResult(true);
        }

        Assert.Equal(SubAgentStatus.Completed, childInfo.Status);
        Assert.Equal("late child answer", childInfo.Summary);

        // It was recorded live into the parent session and reported through the parent's OnUsage...
        forwarded = Assert.Single(events, e => e.Source == UsageSource.SubAgent);
        Assert.Equal("sub-1", forwarded.SubAgentId);
        Assert.Equal(ChildModel, forwarded.Model);
        Assert.Equal(7, forwarded.Usage.InputTokens);
        Assert.Equal(8, forwarded.Usage.OutputTokens);
        Assert.Equal(ParentInputPerRound * 2 + 7, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 2 + 8, session.OutputTokensUsed);
        Assert.Equal(1, CallsFor(session.Usage, UsageSource.SubAgent));

        // ...but the already-returned AgentResult.TokenUsage snapshot is detached and unchanged.
        Assert.Empty(EntriesFor(result.TokenUsage, UsageSource.SubAgent));
        Assert.Equal(0, CallsFor(result.TokenUsage, UsageSource.SubAgent));
        Assert.Equal(ParentInputPerRound * 2, result.TokenUsage.Total.InputTokens);
        Assert.Equal(2, result.TokenUsage.Total.Calls);

        // The sub-agent's own running total saw the late call.
        Assert.Equal(1, childInfo.Usage!.Calls);
        Assert.Equal(7, childInfo.InputTokens);
        Assert.Equal(8, childInfo.OutputTokens);
    }

    // ========================================================================
    // 7. Concurrent recording into one session loses nothing
    // ========================================================================

    /// <summary>
    /// Hang protection for the worker joins, and nothing else: if the workers have not all stopped
    /// within this TOTAL budget, the test fails with diagnostics instead of blocking the test host
    /// forever. It is deliberately NOT a synchronisation device — no correctness decision is made on
    /// elapsed time, the guard is thousands of times longer than the workers ever need (the whole test
    /// runs in tens of milliseconds), and the 40 000-record determinism assertions below are decided
    /// purely by the recorded state.
    /// <para>
    /// The diagnostic reported when the budget expires is rendered from
    /// <see cref="WorkerProgress"/> alone — test-owned counters maintained by the workers themselves —
    /// so that <em>reporting</em> the stall can never itself block (see the note on that type).
    /// </para>
    /// </summary>
    private static readonly TimeSpan WorkerJoinBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Test-owned, lock-free progress for the concurrency workers, maintained by the workers themselves
    /// with <see cref="Interlocked"/> and <see cref="Volatile"/> operations only: it never enters a
    /// production lock, and it never reads production state.
    /// <para>
    /// WHY THIS EXISTS — the hang diagnostic must not depend on anything production can block.
    /// <see cref="UsageSummary"/> guards <c>Total</c>, <c>Entries</c> and <c>Snapshot</c> with the same
    /// private monitor its <c>Add</c> takes, and <see cref="UsageRecorder.Record"/> calls that
    /// <c>Add</c> for every call. So a worker that is stuck inside a record (exactly the situation the
    /// join budget just detected) can be holding that monitor, and a diagnostic that read
    /// <c>session.Usage.Total</c> would block on it FOREVER — reporting the stall after the budget had
    /// already expired, which defeats the hang guard instead of surfacing it. Reading these
    /// <see cref="Volatile"/> int fields and counters instead is always possible, so the failure is
    /// always reported.
    /// </para>
    /// </summary>
    private sealed class WorkerProgress
    {
        /// <summary>Worker created, not yet inside the rendezvous.</summary>
        internal const int StateWaiting = 0;

        /// <summary>Worker has entered the rendezvous.</summary>
        internal const int StateStarted = 1;

        /// <summary>Worker is recording.</summary>
        internal const int StateRecording = 2;

        /// <summary>Worker left its loop, normally or through a failure.</summary>
        internal const int StateFinished = 3;

        /// <summary>How many stuck workers the diagnostic lists in full before summarising the rest.</summary>
        private const int ShownWorkers = 12;

        private readonly int[] _states;
        private readonly int[] _completedRecords;
        private readonly int _recordsPerWorker;
        private int _startedWorkers;
        private int _finishedWorkers;
        private int _failedWorkers;
        private int _deliveredEvents;

        internal WorkerProgress(int workerCount, int recordsPerWorker)
        {
            _states = new int[workerCount];
            _completedRecords = new int[workerCount];
            _recordsPerWorker = recordsPerWorker;
        }

        /// <summary>Number of completed recordings that were delivered to the host callback.</summary>
        internal int DeliveredEvents => Volatile.Read(ref _deliveredEvents);

        /// <summary>Marks the worker as having entered the rendezvous.</summary>
        internal void MarkStarted(int worker)
        {
            Volatile.Write(ref _states[worker], StateStarted);
            Interlocked.Increment(ref _startedWorkers);
        }

        /// <summary>Marks the worker as actively recording.</summary>
        internal void MarkRecording(int worker) => Volatile.Write(ref _states[worker], StateRecording);

        /// <summary>Counts one completed record for <paramref name="worker"/>.</summary>
        internal void MarkRecordCompleted(int worker) => Interlocked.Increment(ref _completedRecords[worker]);

        /// <summary>Marks the worker as having left its loop, recording whether it failed.</summary>
        internal void MarkFinished(int worker, bool failed)
        {
            Volatile.Write(ref _states[worker], StateFinished);
            if (failed) Interlocked.Increment(ref _failedWorkers);
            Interlocked.Increment(ref _finishedWorkers);
        }

        /// <summary>Counts one model call delivered to the host callback.</summary>
        internal void MarkEventDelivered() => Interlocked.Increment(ref _deliveredEvents);

        /// <summary>
        /// Renders the diagnostic for a join that exceeded the budget, from test-owned state ONLY: every
        /// read below is a <see cref="Volatile"/> read of an <c>int</c> field or an
        /// <see cref="Interlocked"/> counter, so no production lock can be acquired here and the message
        /// can always be built — and therefore the failure always reported — even while a stuck worker
        /// holds a production lock.
        /// </summary>
        /// <param name="joinIndex">The worker whose join exceeded the budget.</param>
        /// <returns>A one-line report naming the workers that did not finish, their state, how many records each completed and which iteration it was on.</returns>
        internal string DescribeJoinFailure(int joinIndex)
        {
            var notFinished = new List<int>();
            for (var worker = 0; worker < _states.Length; worker++)
            {
                if (Volatile.Read(ref _states[worker]) != StateFinished) notFinished.Add(worker);
            }

            var detail = string.Join("; ", notFinished.Take(ShownWorkers).Select(DescribeWorker));
            if (notFinished.Count > ShownWorkers)
                detail += $"; ... (+{notFinished.Count - ShownWorkers} more)";

            return $"join loop stopped at worker #{joinIndex} (iteration {joinIndex + 1}/{_states.Length}). " +
                   $"started={Volatile.Read(ref _startedWorkers)}, finished={Volatile.Read(ref _finishedWorkers)}, " +
                   $"failed={Volatile.Read(ref _failedWorkers)}, eventsDelivered={DeliveredEvents}. " +
                   $"Not finished ({notFinished.Count} of {_states.Length}): " +
                   (notFinished.Count == 0 ? "(none)" : detail);
        }

        /// <summary>Renders one worker's state, completed-record count and current iteration.</summary>
        private string DescribeWorker(int worker)
        {
            var state = Volatile.Read(ref _states[worker]);
            var completed = Volatile.Read(ref _completedRecords[worker]);
            var stateName = state switch
            {
                StateWaiting => "waiting-for-rendezvous",
                StateStarted => "started",
                StateRecording => "recording",
                _ => "finished"
            };

            // A worker still recording is on the iteration AFTER the ones it has completed.
            var iteration = Math.Min(completed + 1, _recordsPerWorker);
            return $"#{worker}(state={stateName}, completed={completed}/{_recordsPerWorker}, iteration={iteration})";
        }
    }

    /// <summary>
    /// Bounded, diagnostic join of the concurrency workers, on one shared budget. Returns normally only
    /// once every worker has actually stopped; otherwise it fails the test with diagnostics, so a
    /// locking regression surfaces as a named failure instead of hanging the suite.
    /// <para>
    /// Nothing on the failure path may acquire a production lock: from the first instruction down to the
    /// <see cref="Assert.Fail(string)"/> call, this method touches only the <paramref name="workers"/>
    /// list, <paramref name="progress"/> (test-owned counters), the test's cancellation token and
    /// <see cref="Environment.TickCount64"/> — which is what makes the report itself unhangable.
    /// </para>
    /// <para>
    /// Worker threads are created as background threads, so an abandoned stuck worker can never keep the
    /// test process alive. The caller must therefore dispose the rendezvous barrier ONLY after this
    /// returns, because until then a worker may still be using it.
    /// </para>
    /// </summary>
    /// <param name="workers">The started workers, in creation order.</param>
    /// <param name="progress">Test-owned per-worker progress, read without any production lock.</param>
    /// <param name="ct">The test's token; observed between joins so cancellation unwinds promptly.</param>
    private static void JoinWorkersWithDiagnostics(
        IReadOnlyList<Thread> workers,
        WorkerProgress progress,
        CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)WorkerJoinBudget.TotalMilliseconds;
        for (var index = 0; index < workers.Count; index++)
        {
            // The test token is observed between joins, so cancelling the test unwinds promptly; the
            // worker threads are background threads and are left to die with the process.
            ct.ThrowIfCancellationRequested();

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || !workers[index].Join((int)Math.Min(remaining, int.MaxValue)))
            {
                Assert.Fail(
                    $"worker thread {index} did not stop within the shared {WorkerJoinBudget.TotalSeconds:0}s hang budget " +
                    $"(workers={workers.Count}). {progress.DescribeJoinFailure(index)}");
            }
        }
    }

    [Fact]
    public async Task ConcurrentRecord_OnOneRecorderAndSession_LosesNoUpdate()
    {
        // Many more workers than hardware threads on purpose: the counter update this test guards is a
        // read-modify-write pair, so a lost update needs two workers to interleave BETWEEN the read and
        // the write. Oversubscription makes the OS preempt a worker inside that window, which is what
        // turns "the lock was removed" into an observable shortfall instead of a lucky pass. The exact
        // 40 000-record totals below are unchanged and are what decide correctness.
        const int workers = 200;
        const int recordsPerWorker = 200;
        const int total = workers * recordsPerWorker;

        var session = AgentSession.Create("concurrent-forwarding");
        var options = new AgentOptions { WorkDirectory = Path.GetTempPath() };
        var recorder = new UsageRecorder(options, session, NullLogger.Instance);

        // Test-owned, lock-free progress AND a test-owned delivered-event counter. The diagnostic
        // printed when the join budget expires is rendered entirely from this state, so it can never
        // block on a production lock (see WorkerProgress).
        var progress = new WorkerProgress(workers, recordsPerWorker);
        options.OnUsage = _ =>
        {
            progress.MarkEventDelivered();
        };

        // Dedicated threads released together by a barrier: every worker is provably running and
        // about to record when the barrier opens, and the barrier (not a clock) is the only
        // synchronisation device.
        var failures = new ConcurrentQueue<Exception>();
        var barrier = new Barrier(workers);
        var threads = new Thread[workers];
        for (var w = 0; w < workers; w++)
        {
            var workerIndex = w;
            threads[w] = new Thread(() =>
            {
                var failed = false;
                try
                {
                    barrier.SignalAndWait();
                    progress.MarkStarted(workerIndex);
                    progress.MarkRecording(workerIndex);
                    for (var i = 0; i < recordsPerWorker; i++)
                    {
                        recorder.Record(new UsageEvent(
                            UsageSource.Agent, "concurrent-model",
                            new TokenUsage { InputTokens = 1, OutputTokens = 1, Calls = 1 }));
                        progress.MarkRecordCompleted(workerIndex);
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    failures.Enqueue(ex);
                }
                finally
                {
                    progress.MarkFinished(workerIndex, failed);
                }
            })
            {
                // Background threads: if one is ever abandoned by the hang budget below it cannot keep
                // the test process alive (cleanup on failure).
                IsBackground = true,
                Name = $"concurrent-recorder-{workerIndex}"
            };
        }

        foreach (var thread in threads) thread.Start();

        // Bounded diagnostic joins (hang protection only; see JoinWorkersWithDiagnostics). Every worker
        // has provably left its loop once this returns, so the production reads below cannot contend
        // with a recording worker. The barrier is disposed strictly AFTER every worker has stopped
        // using it.
        JoinWorkersWithDiagnostics(threads, progress, TestContext.Current.CancellationToken);
        barrier.Dispose();

        Assert.Empty(failures);

        // ---- Success path: only now are production accounting structures read ----
        // Every count must be EXACT, not merely close: the session's cumulative counters are updated
        // under the session's usage lock, so no read-modify-write can lose an addition.
        Assert.Equal(total, session.InputTokensUsed);
        Assert.Equal(total, session.OutputTokensUsed);
        Assert.Equal(total, progress.DeliveredEvents);

        // The grouped summary is guarded by its own internal lock, so it must agree with the
        // counters exactly (a lock-free counter update would make these two disagree).
        Assert.Equal(total, session.Usage.Total.Calls);
        Assert.Equal(total, session.Usage.Total.InputTokens);
        Assert.Equal(total, session.Usage.Total.OutputTokens);

        var entry = Assert.Single(session.Usage.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal("concurrent-model", entry.Model);
        Assert.Equal(total, entry.Usage.Calls);
        Assert.Equal(total, entry.Usage.InputTokens);
        Assert.Equal(total, entry.Usage.OutputTokens);
    }

    // ========================================================================
    // 8. A throwing parent OnUsage cannot fail either run
    // ========================================================================

    [Fact]
    public async Task ThrowingParentOnUsage_DoesNotFailTheSubAgentOrTheParentRun()
    {
        using var work = new TempWorkDir();
        var child = TwoRoundChild();

        var throwCount = 0;
        var options = ParentOptions(work.Dir, fileOps: true);
        options.OnUsage = _ =>
        {
            Interlocked.Increment(ref throwCount);
            throw new InvalidOperationException("parent OnUsage exploded");
        };
        options.SubAgents = ChildOptions(child);

        var parent = new ScriptedParentClient(StandardParentRounds());
        await using var agent = new CodingAgent(parent, options);
        var session = AgentSession.Create("throwing-parent-handler");

        var result = await agent.ExecuteAsync(session, "delegate the work", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);

        // Three parent rounds plus two forwarded child calls: the handler threw for all five and was
        // contained every time, so the child's own calls still went through the forwarding seam.
        Assert.Equal(5, throwCount);
        Assert.Equal(2, child.CallCount);

        var manager = agent.ActiveSubAgentManager;
        Assert.NotNull(manager);
        var childInfo = Assert.Single(manager!.GetStatus("sub-1"));
        Assert.Equal(SubAgentStatus.Completed, childInfo.Status);
        Assert.Equal("child answer", childInfo.Summary);
        Assert.Null(childInfo.Error);
        Assert.Equal(ChildInputTotal, childInfo.InputTokens);
        Assert.Equal(ChildOutputTotal, childInfo.OutputTokens);
        Assert.Equal(2, childInfo.Usage!.Calls);

        // The accounting itself is intact for both the parent's own rounds and the child's calls.
        Assert.Equal(ParentInputPerRound * 3, InputFor(session.Usage, UsageSource.Agent));
        Assert.Equal(ChildInputTotal, InputFor(session.Usage, UsageSource.SubAgent));
        Assert.Equal(ParentInputPerRound * 3 + ChildInputTotal, session.InputTokensUsed);
        Assert.Equal(ParentOutputPerRound * 3 + ChildOutputTotal, session.OutputTokensUsed);
        Assert.Equal(3, CallsFor(result.TokenUsage, UsageSource.Agent));
        Assert.Equal(2, CallsFor(result.TokenUsage, UsageSource.SubAgent));
    }
}
