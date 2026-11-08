using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Tests for sub-agent propagation of the tool-authorization pair, and for the lifetime
/// immutability of the policy an existing agent, an existing <see cref="SubAgentManager"/> and an
/// already-accepted child run under.
/// <para>
/// The contract under test: the pair is captured once when a <see cref="CodingAgent"/> is
/// constructed, handed to the lazily created manager, copied into every
/// <see cref="SubAgentManager"/> run at acceptance time (BEFORE the concurrency slot is awaited), and
/// applied to the child <see cref="AgentOptions"/> the child <see cref="CodingAgent"/> is built from.
/// Later mutation of <see cref="AgentOptions"/> — enable, disable, replace, re-correlate — must never
/// reach any of those three, in either direction.
/// </para>
/// <para>
/// Determinism: no delay, sleep or timeout is used as a synchronisation device. Every rendezvous is a
/// <see cref="TaskCompletionSource{TResult}"/> or a <see cref="SemaphoreSlim"/> the test releases.
/// </para>
/// </summary>
public class ToolAuthorizationPropagationTests
{
    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>A fresh, unique work directory for one test; deleted when the test ends.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sharpcoder-auth-prop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One scripted round: a tool call, or final assistant text.</summary>
    private sealed class Round
    {
        private Round() { }

        internal string? ToolName { get; private init; }

        internal Dictionary<string, object?>? ToolArgs { get; private init; }

        internal string? Text { get; private init; }

        internal static Round Call(string name, Dictionary<string, object?>? args = null) =>
            new() { ToolName = name, ToolArgs = args ?? new Dictionary<string, object?>() };

        internal static Round Say(string text) => new() { Text = text };
    }

    /// <summary>
    /// Chat client that replays a script on BOTH entry points, records every
    /// <see cref="ChatOptions"/> it was given, records every tool result the model saw, and invokes a
    /// per-round hook BEFORE serving that round (so a test can install manager seams before any
    /// sub-agent can start).
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private readonly Action<int>? _beforeRound;
        private int _index;

        internal ScriptedClient(IReadOnlyList<Round> rounds, Action<int>? beforeRound = null)
        {
            _rounds = rounds;
            _beforeRound = beforeRound;
        }

        internal List<ChatOptions?> ReceivedOptions { get; } = [];

        internal List<string> ToolResults { get; } = [];

        internal int CallCount
        {
            get { lock (ReceivedOptions) return ReceivedOptions.Count; }
        }

        private Round Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var snapshot = messages.ToList();
            lock (ReceivedOptions)
            {
                ReceivedOptions.Add(options);
                foreach (var message in snapshot)
                {
                    foreach (var content in message.Contents)
                    {
                        if (content is FunctionResultContent result)
                            ToolResults.Add(ResultToString(result.Result));
                    }
                }
            }

            var index = Interlocked.Increment(ref _index) - 1;
            _beforeRound?.Invoke(index);
            var rounds = _roundsOverride ?? _rounds;
            return index < rounds.Count ? rounds[index] : Round.Say("parent done");
        }

        /// <summary>
        /// Installs a fresh script for the NEXT execution on the same agent and rewinds the cursor, so
        /// a test can drive a second multi-round execution through one long-lived client.
        /// </summary>
        internal void Restart(IReadOnlyList<Round> rounds)
        {
            lock (ReceivedOptions)
            {
                _roundsOverride = rounds;
            }
            Interlocked.Exchange(ref _index, 0);
        }

        private IReadOnlyList<Round>? _roundsOverride;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var round = Next(messages, options);
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"parent_call_{rand.Next()}", round.ToolName, round.ToolArgs)]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "parent done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var round = Next(messages, options);
            await Task.Yield();
            if (round.ToolName is not null)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent($"parent_call_{rand.Next()}", round.ToolName, round.ToolArgs)]
                };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
                yield break;
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(round.Text ?? "parent done")]
            };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        private readonly Random rand = new();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Chat client for a CHILD agent. It replays a script and records the <see cref="ChatOptions"/>
    /// the child sent, so a test can inspect the child's own guarded tool surface.
    /// </summary>
    private sealed class ChildClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private int _index;

        internal ChildClient(params Round[] rounds) => _rounds = rounds;

        internal ConcurrentQueue<ChatOptions?> Options { get; } = new();

        internal ConcurrentQueue<string> ToolResults { get; } = new();

        private Round Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var snapshot = messages.ToList();
            Options.Enqueue(options);
            foreach (var message in snapshot)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent result)
                        ToolResults.Enqueue(ResultToString(result.Result));
                }
            }

            var index = Interlocked.Increment(ref _index) - 1;
            return index < _rounds.Count ? _rounds[index] : Round.Say("child done");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var round = Next(messages, options);
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"child_call_{Interlocked.Increment(ref _callId)}", round.ToolName, round.ToolArgs)]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "child done"))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        private int _callId;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string ResultToString(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText(),
        _ => result.ToString() ?? string.Empty
    };

    /// <summary>Records every authorization check, in order.</summary>
    private sealed class AuthorizationLog
    {
        internal ConcurrentQueue<ToolAuthorizationContext> Calls { get; } = new();

        internal IReadOnlyList<ToolAuthorizationContext> All => Calls.ToArray();

        internal IEnumerable<ToolAuthorizationContext> ForTool(string toolName) =>
            All.Where(c => string.Equals(c.ToolName, toolName, StringComparison.Ordinal));

        internal void Clear()
        {
            while (Calls.TryDequeue(out _)) { }
        }
    }

    // ========================================================================
    // Fixtures
    // ========================================================================

    private static AgentOptions ParentOptions(string workDirectory) => new()
    {
        WorkDirectory = workDirectory,
        EnableBash = false,
        EnableFileOps = true,
        EnableFileWrites = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
    };

    private static SubAgentOptions SubOptions(IChatClient childClient) => new()
    {
        DefaultClient = childClient,
        DefaultEnableBash = false,
        DefaultEnableFileOps = true,
        DefaultEnableFileWrites = false,
        DefaultEnableSkills = false,
    };

    private static AIFunction FunctionNamed(ChatOptions? options, string name) =>
        (options?.Tools ?? []).OfType<AIFunction>().Single(f => f.Name == name);

    private static List<AIFunction> Functions(ChatOptions? options) =>
        (options?.Tools ?? []).OfType<AIFunction>().ToList();

    /// <summary>
    /// Drives one parent execution whose script is: <c>start_sub_agent</c>, <c>await_sub_agents</c>,
    /// then final text. Returns after the parent execution completed.
    /// </summary>
    private static async Task<AgentResult> RunParentAsync(
        CodingAgent agent,
        string task = "delegate the work",
        CancellationToken ct = default)
    {
        return await agent.ExecuteAsync(task, ct);
    }

    private static IReadOnlyList<Round> DelegateScript(string subTask = "inspect the workspace") =>
    [
        Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = subTask }),
        Round.Call("await_sub_agents"),
        Round.Say("parent finished"),
    ];

    // ========================================================================
    // 1. The child receives exactly the parent's constructor-captured pair
    // ========================================================================

    [Fact]
    public async Task Child_InheritsTheParentsCapturedPair()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "user-parent";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // The exactly-one-instance identity the parent constructed with.
        var constructionCallback = parent.AuthorizeTool!;

        var result = await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);

        // The child really ran and was built from the parent's captured pair.
        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Same(constructionCallback, childOptions!.AuthorizeTool);
        Assert.Equal("user-parent", childOptions.ToolAuthorizationCorrelationId);

        // And the child's own tool surface is guarded: its read_file goes through the boundary with
        // the child's captured pair, which equals the parent's.
        var childTools = child.Options.ToArray().LastOrDefault();
        Assert.NotNull(childTools);
        Assert.True(ToolAuthorizationGuard.IsGuarded(FunctionNamed(childTools, "read_file")),
            "the child's own read_file must be behind the authorization boundary");

        var subAgentCall = Assert.Single(log.ForTool("start_sub_agent"));
        Assert.Equal("user-parent", subAgentCall.CorrelationId);
    }

    [Fact]
    public async Task Child_WithAuthorizationOff_RegistersItsToolsUnguarded()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // No AuthorizeTool at all.

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Null(childOptions!.AuthorizeTool);
        Assert.Null(childOptions.ToolAuthorizationCorrelationId);

        var childTools = child.Options.ToArray().LastOrDefault();
        Assert.NotNull(childTools);
        foreach (var tool in childTools!.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool),
                $"child tool '{tool.Name}' must not be wrapped when authorization is off");
    }

    // ========================================================================
    // 2. Denied start_sub_agent spawns no child
    // ========================================================================

    [Fact]
    public async Task DeniedStartSubAgent_SpawnsNoChild()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("should never run"));
        var log = new AuthorizationLog();

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(context.ToolName == "start_sub_agent"
                ? ToolAuthorizationDecision.Deny("This user may not delegate work.")
                : ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "inspect" }),
            Round.Say("parent finished"),
        ]);
        await using var agent = new CodingAgent(client, parent);

        var result = await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);
        Assert.Equal("Success", result.Status);

        // The denial happened BEFORE any effect: no sub-agent was accepted or started.
        Assert.Empty(agent.ActiveSubAgentManager!.GetStatus());
        Assert.Empty(child.Options);
        Assert.Null(agent.ActiveSubAgentManager.LastChildOptions);

        // The authorizer saw the concrete task text, so it could decide on content, not category.
        var startCall = Assert.Single(log.ForTool("start_sub_agent"));
        Assert.Equal("inspect", startCall.Arguments["task"]);

        // The model received the host's refusal, not a sub-agent id.
        var refusal = Assert.Single(client.ToolResults);
        Assert.Contains("This user may not delegate work.", refusal);
        Assert.DoesNotContain("\"id\":", refusal);
    }

    // ========================================================================
    // 3. Denied CHILD read_file performs no file access (path-aware)
    // ========================================================================

    [Fact]
    public async Task DeniedChildReadFile_ByPath_PerformsNoFileAccess()
    {
        using var dir = new TempDir();
        var secretPath = Path.Combine(dir.Path, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "TOPSECRET-CONTENT", TestContext.Current.CancellationToken);

        var log = new AuthorizationLog();
        // The child tries to read the secret file; the parent's policy denies that exact path.
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = secretPath }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            if (!string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                return Task.FromResult(ToolAuthorizationDecision.Allow);

            var allowed = !(context.Arguments.TryGetValue("filePath", out var path)
                            && path is string s
                            && s.EndsWith("secret.txt", StringComparison.Ordinal));
            return Task.FromResult(allowed
                ? ToolAuthorizationDecision.Allow
                : ToolAuthorizationDecision.Deny("That file is off limits to delegated work."));
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        // The CHILD's read_file was authorized by path, with the parent's correlation.
        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal(secretPath, childRead.Arguments["filePath"]);
        Assert.Equal(log.ForTool("start_sub_agent").Single().CorrelationId, childRead.CorrelationId);

        // No file access happened: the secret's contents never reached the child model.
        var childResults = child.ToolResults.ToArray();
        Assert.NotEmpty(childResults);
        foreach (var result in childResults)
        {
            Assert.DoesNotContain("TOPSECRET-CONTENT", result);
            Assert.Contains("That file is off limits to delegated work.", result);
        }
    }

    [Fact]
    public async Task AllowedChildReadFile_ByPath_ReturnsTheContents()
    {
        using var dir = new TempDir();
        var allowedPath = Path.Combine(dir.Path, "allowed.txt");
        await File.WriteAllTextAsync(allowedPath, "PUBLIC-CONTENT", TestContext.Current.CancellationToken);

        var log = new AuthorizationLog();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = allowedPath }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal(allowedPath, childRead.Arguments["filePath"]);
        Assert.Contains(child.ToolResults.ToArray(), r => r.Contains("PUBLIC-CONTENT"));
    }

    // ========================================================================
    // 4. Policy immutability: mutate AgentOptions AFTER construction
    // ========================================================================

    [Fact]
    public async Task MutationAfterConstructionBeforeFirstExecution_CannotChangeTheChildPolicy()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "construction-time-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        // The manager does not exist yet: it is created lazily inside the first execution.
        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        Assert.Null(agent.ActiveSubAgentManager);

        // Mutate the LIVE options after construction but before ANY execution — the window in which
        // a live-options read would silently pick up the new policy.
        var replacementCalls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            replacementCalls++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("replacement policy"));
        };
        parent.ToolAuthorizationCorrelationId = "mutated-user";

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        // The agent and its manager kept the construction-time pair.
        Assert.Equal(0, replacementCalls);
        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("construction-time-user", c.CorrelationId));

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Equal("construction-time-user", childOptions!.ToolAuthorizationCorrelationId);
        Assert.NotSame(parent.AuthorizeTool, childOptions.AuthorizeTool);
    }

    [Fact]
    public async Task AuthorizationEnabledAfterConstruction_CannotGuardAnAgentCreatedWithoutIt()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // Constructed with authorization OFF.

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // Enable authorization on the live options before the first execution.
        var calls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            calls++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("must not apply"));
        };

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        // Neither the parent nor its children gained a policy they were not constructed with.
        Assert.Equal(0, calls);
        Assert.False(ToolAuthorizationGuard.IsGuarded(FunctionNamed(client.ReceivedOptions[0], "start_sub_agent")));

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Null(childOptions!.AuthorizeTool);

        var childTools = child.Options.ToArray().Last();
        foreach (var tool in childTools!.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool), $"child tool '{tool.Name}' must stay unguarded");
    }

    [Fact]
    public async Task AuthorizationDisabledAfterConstruction_CannotUnguardAnAgentCreatedWithIt()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "kept-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // Disable it on the live options before the first execution.
        parent.AuthorizeTool = null;
        parent.ToolAuthorizationCorrelationId = null;

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("kept-user", c.CorrelationId));

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.NotNull(childOptions!.AuthorizeTool);
        Assert.Equal("kept-user", childOptions.ToolAuthorizationCorrelationId);

        var childTools = child.Options.ToArray().Last();
        Assert.True(ToolAuthorizationGuard.IsGuarded(FunctionNamed(childTools, "read_file")));
    }

    // ========================================================================
    // 5. Policy immutability: mutate AgentOptions AFTER start_sub_agent acceptance
    // ========================================================================

    [Fact]
    public async Task MutationAfterAcceptance_CannotChangeAnAcceptedChildPolicy()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }), Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "accepted-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var replacementCalls = 0;
        AgentOptions? childOptions = null;
        CodingAgent? agentRef = null;

        // Install the seams and mutate the live options AFTER acceptance: the hook runs after the
        // manager exists and before the start round is served, then we mutate inside the
        // OnSubAgentOptionsCreated probe — i.e. after the RunSpec was captured, immediately before the
        // child CodingAgent is constructed.
        var client = new ScriptedClient(DelegateScript(), beforeRound: index =>
        {
            if (index != 0) return;
            Assert.NotNull(agentRef);
            var manager = agentRef!.ActiveSubAgentManager;
            Assert.NotNull(manager);
            manager!.OnSubAgentOptionsCreated = options =>
            {
                childOptions = options;
                // Mutation at the last possible moment: after acceptance, before child construction.
                parent.AuthorizeTool = (_, _) =>
                {
                    replacementCalls++;
                    return Task.FromResult(ToolAuthorizationDecision.Deny("replacement after acceptance"));
                };
                parent.ToolAuthorizationCorrelationId = "replaced-user";
            };
        });

        await using var agent = new CodingAgent(client, parent);
        agentRef = agent;

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(childOptions);

        // The accepted child kept the pair it was accepted with.
        Assert.NotSame(parent.AuthorizeTool, childOptions!.AuthorizeTool);
        Assert.Equal("accepted-user", childOptions.ToolAuthorizationCorrelationId);

        // And the child's executed calls all carried the accepted correlation.
        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal("accepted-user", childRead.CorrelationId);
        Assert.Equal(0, replacementCalls);
    }

    [Fact]
    public async Task AcceptedChild_DoesNotLoseAuthorizationWhenTheHostDisablesIt()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "b.txt" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "still-guarded";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        AgentOptions? childOptions = null;
        CodingAgent? agentRef = null;

        var client = new ScriptedClient(DelegateScript(), beforeRound: index =>
        {
            if (index != 0) return;
            agentRef!.ActiveSubAgentManager!.OnSubAgentOptionsCreated = options =>
            {
                childOptions = options;
                // Disable authorization entirely after acceptance.
                parent.AuthorizeTool = null;
                parent.ToolAuthorizationCorrelationId = null;
            };
        });

        await using var agent = new CodingAgent(client, parent);
        agentRef = agent;

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(childOptions);
        Assert.NotNull(childOptions!.AuthorizeTool);
        Assert.Equal("still-guarded", childOptions.ToolAuthorizationCorrelationId);

        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal("still-guarded", childRead.CorrelationId);
    }

    // ========================================================================
    // 6. Existing manager keeps its creation-time policy
    // ========================================================================

    [Fact]
    public async Task ExistingManager_KeepsItsCreationPolicy_WhenTheHostMutatesOptions()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "manager-created-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // First execution creates the manager with the construction-time pair.
        await agent.ExecuteAsync("first", TestContext.Current.CancellationToken);
        var manager = agent.ActiveSubAgentManager;
        Assert.NotNull(manager);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        // Now mutate the live options and run a SECOND execution on the same agent: the same manager
        // is reused, so it must still use the policy fixed when it was created.
        log.Clear();
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("later policy"));
        parent.ToolAuthorizationCorrelationId = "later-user";

        // Rewind the script so the second execution really starts a sub-agent too.
        client.Restart(DelegateScript("inspect again"));

        await agent.ExecuteAsync("second", TestContext.Current.CancellationToken);

        Assert.Equal(1, agent.SubAgentManagerCreateCount);
        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("manager-created-user", c.CorrelationId));
    }

    // ========================================================================
    // 7. Correlation immutability
    // ========================================================================

    [Fact]
    public async Task Correlation_ReplacementNeverAffectsAnExistingAgentOrItsChildren()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "user-original";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // Simulate the host mutating "the" correlation value it handed in: a string cannot be
        // mutated in place, so the only available attack is replacement. Both the parent tools and
        // the child must keep the original.
        var hostSupplied = "user-original";
        parent.ToolAuthorizationCorrelationId = hostSupplied + "-mutated";

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("user-original", c.CorrelationId));

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.Equal("user-original", childOptions!.ToolAuthorizationCorrelationId);
    }

    // ========================================================================
    // 8. Both dispatch paths: the manual streaming loop guard applies too
    // ========================================================================

    [Fact]
    public async Task DeniedStartSubAgent_OnManualStreamingPath_SpawnsNoChild()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("should never run"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // ShowToolCallsInStream routes tool dispatch through CodingAgent's OWN manual loop
        // (the direct AIFunction.InvokeAsync calls), not FunctionInvokingChatClient.
        parent.ShowToolCallsInStream = true;
        parent.AuthorizeTool = (context, _) => Task.FromResult(context.ToolName == "start_sub_agent"
            ? ToolAuthorizationDecision.Deny("No delegation on this path.")
            : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "inspect" }),
            Round.Say("parent finished"),
        ]);
        await using var agent = new CodingAgent(client, parent);

        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "delegate", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
        }

        // The denial happened before any effect on the manual path too.
        Assert.Empty(agent.ActiveSubAgentManager!.GetStatus());
        Assert.Empty(child.Options);
        Assert.Null(agent.ActiveSubAgentManager!.LastChildOptions);

        // The manual loop fed the refusal back to the model as the tool result (the `> …` line it
        // yields for each result), so the host's reason reached the model on this path as well.
        var resultLine = text.ToString()
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("> ", StringComparison.Ordinal));
        Assert.NotNull(resultLine);
        Assert.Contains("No delegation on this path.", resultLine);
    }

    [Fact]
    public async Task ChildCallbackFault_OnManualStreamingPath_IsSanitizedAndFailsClosed()
    {
        using var dir = new TempDir();
        var secretPath = Path.Combine(dir.Path, "secret2.txt");
        await File.WriteAllTextAsync(secretPath, "CHILD-TOPSECRET", TestContext.Current.CancellationToken);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = secretPath }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            // The child's read faults; the parent's own start/await calls are allowed.
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                throw new InvalidOperationException("SECRET-CHILD-DETAIL-99");
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var childResults = child.ToolResults.ToArray();
        Assert.NotEmpty(childResults);
        foreach (var result in childResults)
        {
            Assert.Contains("could not be authorized", result);
            Assert.DoesNotContain("SECRET-CHILD-DETAIL-99", result);
            Assert.DoesNotContain("CHILD-TOPSECRET", result);
        }
    }

    // ========================================================================
    // 9. Non-AIFunction and no-callback compatibility for children
    // ========================================================================

    [Fact]
    public async Task Child_WhenParentPolicyIsOff_KeepsLegacyToolShape()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var childTools = child.Options.ToArray().Last()!;
        var names = childTools.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.Contains("glob", names);
        Assert.Contains("grep", names);
        foreach (var tool in childTools.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool));
    }

    // ========================================================================
    // 12. The manager must not read the LIVE options at lazy creation
    // ========================================================================

    [Fact]
    public async Task ManagerCreatedLazily_UsesTheAgentsCapturedPair_NotTheLiveOptions()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "before-construction";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        Assert.Null(agent.ActiveSubAgentManager);

        // Swap BOTH members between construction and the lazy manager creation. A manager that read
        // the live options here would fix `after-construction` instead.
        var liveCalls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            liveCalls++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("live options policy"));
        };
        parent.ToolAuthorizationCorrelationId = "after-construction";

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, liveCalls);
        var manager = agent.ActiveSubAgentManager!;
        Assert.NotNull(manager.ToolAuthorizationSnapshot);
        Assert.Equal("before-construction", manager.ToolAuthorizationSnapshot!.CorrelationId);
        Assert.All(log.All, c => Assert.Equal("before-construction", c.CorrelationId));

        var childOptions = manager.LastChildOptions!;
        Assert.Equal("before-construction", childOptions.ToolAuthorizationCorrelationId);

        // The sub-agent itself was accepted, so the run really exercised the manager path.
        var status = manager.GetStatus();
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Completed, status[0].Status);
    }

    [Fact]
    public async Task ManagerCreatedLazily_WithAuthorizationOff_CannotAdoptALaterEnabledCallback()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // Constructed with authorization OFF.

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        var calls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            calls++;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };
        parent.ToolAuthorizationCorrelationId = "added-later";

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        var manager = agent.ActiveSubAgentManager!;
        Assert.Null(manager.ToolAuthorizationSnapshot);

        var childOptions = manager.LastChildOptions!;
        Assert.Null(childOptions.AuthorizeTool);
        Assert.Null(childOptions.ToolAuthorizationCorrelationId);
    }

    // ========================================================================
    // 13. Correlation is a defensively copied immutable value
    // ========================================================================

    [Fact]
    public async Task Correlation_IsImmutable_AcrossAgentManagerAndChild()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        // A string is immutable, so the strongest available attack is replacement of the options
        // member AND of the host's own local variable. Prove neither reaches the captured value.
        var hostSupplied = string.Concat("tenant-", "abc");
        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = hostSupplied;
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);

        // "Mutate the original backing data": reassign the host's own variable and the options member.
        hostSupplied = "tenant-mutated";
        parent.ToolAuthorizationCorrelationId = hostSupplied;

        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var manager = agent.ActiveSubAgentManager!;
        Assert.Equal("tenant-abc", manager.ToolAuthorizationSnapshot!.CorrelationId);
        Assert.Equal("tenant-abc", manager.LastChildOptions!.ToolAuthorizationCorrelationId);
        Assert.All(log.All, c => Assert.Equal("tenant-abc", c.CorrelationId));

        // Two captures of the same options never share state: a second agent sees whatever the
        // options hold at ITS construction time.
        var second = ParentOptions(dir.Path);
        second.ToolAuthorizationCorrelationId = "tenant-second";
        second.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);
        var secondCapture = ToolAuthorizationCapture.Capture(second)!;
        second.ToolAuthorizationCorrelationId = "tenant-third";
        Assert.Equal("tenant-second", secondCapture.CorrelationId);
    }

    // ========================================================================
    // 14. Acceptance-time capture survives a concurrency-slot wait
    // ========================================================================

    [Fact]
    public async Task ChildQueuedOnTheSlot_StillUsesTheAcceptanceTimePair()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();

        // The first sub-agent's child client parks on a gate, so it deterministically holds the only
        // concurrency slot until the test releases it (no delay is used as a synchronisation device).
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedClient = new GatedChildClient(gate.Task);

        var parent = ParentOptions(dir.Path);
        var subOptions = SubOptions(gatedClient);
        subOptions.MaxConcurrentSubAgents = 1;
        parent.ToolAuthorizationCorrelationId = "acceptance-time";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var subClient = new ChildClient(Round.Say("unused"));
        await using var manager = new SubAgentManager(subOptions, subClient, parent);

        // Occupy the single slot.
        var first = await manager.StartAsync(
            new SubAgentRequest { Task = "first" }, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(first.Id));

        // Queue a second start; it cannot proceed until the slot frees.
        var secondStart = manager.StartAsync(
            new SubAgentRequest { Task = "second" }, TestContext.Current.CancellationToken);

        // Mutate the live options WHILE the second start is queued on the slot.
        var replacementCalls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            replacementCalls++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("policy changed while queued"));
        };
        parent.ToolAuthorizationCorrelationId = "changed-while-queued";

        gate.SetResult(true);
        var second = await secondStart;
        Assert.False(string.IsNullOrEmpty(second.Id));
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

        // Both children were built from the pair fixed at acceptance, not the mutated options.
        Assert.Equal(0, replacementCalls);
        var childOptions = manager.LastChildOptions!;
        Assert.Equal("acceptance-time", childOptions.ToolAuthorizationCorrelationId);
        Assert.All(log.All, c => Assert.Equal("acceptance-time", c.CorrelationId));
    }

    /// <summary>
    /// Child client that cannot produce a response until <c>gate</c> completes, so a test can hold a
    /// concurrency slot open deterministically.
    /// </summary>
    private sealed class GatedChildClient : IChatClient
    {
        private readonly Task _gate;

        internal GatedChildClient(Task gate) => _gate = gate;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages.ToList();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "gated child done"))
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ========================================================================
    // 10. Manager constructed directly by a host still inherits the parent pair
    // ========================================================================

    [Fact]
    public async Task HostCreatedManager_CapturesTheParentPairAtItsOwnCreation()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.ToolAuthorizationCorrelationId = "host-manager-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var subOptions = SubOptions(child);
        await using var manager = new SubAgentManager(subOptions, child, parent);

        // Mutate AFTER the manager was created: the manager already fixed its pair.
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("too late"));
        parent.ToolAuthorizationCorrelationId = "too-late-user";

        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "inspect" }, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(info.Id));
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

        var childOptions = manager.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Equal("host-manager-user", childOptions!.ToolAuthorizationCorrelationId);

        var childTools = child.Options.ToArray().Last()!;
        Assert.True(ToolAuthorizationGuard.IsGuarded(FunctionNamed(childTools, "read_file")));
    }

    // ========================================================================
    // 11. Direct invocation of a child's guarded function bypasses nothing
    // ========================================================================

    [Fact]
    public async Task ChildGuardedFunction_InvokedDirectlyFromItsChatOptions_StillAuthorizes()
    {
        using var dir = new TempDir();
        var targetPath = Path.Combine(dir.Path, "direct.txt");
        await File.WriteAllTextAsync(targetPath, "DIRECT-CONTENT", TestContext.Current.CancellationToken);

        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "child-direct-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var childTools = child.Options.ToArray().Last()!;
        var readFile = FunctionNamed(childTools, "read_file");
        log.Clear();

        // Invoke the child's guarded read_file DIRECTLY from the child's ChatOptions: the boundary
        // must run there too, with the child's accepted pair.
        var result = await readFile.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["filePath"] = targetPath }),
            TestContext.Current.CancellationToken);

        var read = Assert.Single(log.ForTool("read_file"));
        Assert.Equal(targetPath, read.Arguments["filePath"]);
        Assert.Equal("child-direct-user", read.CorrelationId);
        Assert.Contains("DIRECT-CONTENT", ResultToString(result));
    }

    [Fact]
    public async Task ChildGuardedFunction_DirectlyInvoked_RefusesWhenDenied()
    {
        using var dir = new TempDir();
        var targetPath = Path.Combine(dir.Path, "denied-direct.txt");
        await File.WriteAllTextAsync(targetPath, "MUST-NOT-APPEAR", TestContext.Current.CancellationToken);

        var child = new ChildClient(Round.Say("child done"));
        var denying = false;

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (_, _) => Task.FromResult(denying
            ? ToolAuthorizationDecision.Deny("Direct child reads are blocked.")
            : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(DelegateScript());
        await using var agent = new CodingAgent(client, parent);
        await RunParentAsync(agent, ct: TestContext.Current.CancellationToken);

        var readFile = FunctionNamed(child.Options.ToArray().Last()!, "read_file");
        denying = true;

        var result = await readFile.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["filePath"] = targetPath }),
            TestContext.Current.CancellationToken);

        var text = ResultToString(result);
        Assert.Contains("Direct child reads are blocked.", text);
        Assert.DoesNotContain("MUST-NOT-APPEAR", text);
    }
}
