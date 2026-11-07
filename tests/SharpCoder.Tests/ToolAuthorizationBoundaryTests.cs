using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

/// <summary>
/// Additional coverage for the parent-side tool-authorization boundary, owned by the test phase.
/// <para>
/// These tests complement <see cref="ToolAuthorizationTests"/> by pinning the parts of the
/// round-1 contract that the first test file did not exercise:
/// </para>
/// <list type="bullet">
/// <item>enabling authorization AFTER construction must not change an existing agent;</item>
/// <item>the captured pair is used for EVERY execution of the agent that captured it;</item>
/// <item>denial happens before the side effect on the MANUAL streaming dispatch path too;</item>
/// <item>argument-aware authorization for the remaining built-ins (edit_file, grep, glob);</item>
/// <item>direct invocation of a DENIED guarded function;</item>
/// <item>the caller's cancellation token reaches the wrapped function unchanged;</item>
/// <item>the non-invokable custom tool fails closed BEFORE any model call;</item>
/// <item>a callback cancellation is not converted into a denial refusal;</item>
/// <item>the documented trust boundary: a host-retained unwrapped custom function is not guarded;</item>
/// <item>usage and session semantics are unchanged by the authorization boundary.</item>
/// </list>
/// </summary>
public class ToolAuthorizationBoundaryTests
{
    // ========================================================================
    // Fakes and fixtures
    // ========================================================================

    /// <summary>One scripted round: either a tool call or plain assistant text.</summary>
    private sealed class Round
    {
        public string? ToolName;
        public Dictionary<string, object?>? ToolArgs;
        public string? Text;

        public static Round Call(string name, Dictionary<string, object?> args) =>
            new() { ToolName = name, ToolArgs = args };

        public static Round Say(string text) => new() { Text = text };
    }

    /// <summary>
    /// Chat client that replays a script of rounds, records the tool results the model saw and
    /// captures the <see cref="ChatOptions"/> of the last request.
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private int _index;

        public ScriptedClient(params Round[] rounds) => _rounds = rounds;

        public ChatOptions? LastOptions { get; private set; }
        public List<string> ToolResults { get; } = [];
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            CallCount++;

            foreach (var message in messages.ToList())
                foreach (var content in message.Contents)
                    if (content is FunctionResultContent result)
                        ToolResults.Add(ResultToString(result.Result));

            var round = _rounds[_index++];
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? string.Empty))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            CallCount++;

            foreach (var message in messages.ToList())
                foreach (var content in message.Contents)
                    if (content is FunctionResultContent result)
                        ToolResults.Add(ResultToString(result.Result));

            var round = _rounds[_index++];
            ChatResponse response;
            if (round.ToolName is not null)
            {
                response = new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }
            else
            {
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? string.Empty))
                {
                    FinishReason = ChatFinishReason.Stop,
                    Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 }
                };
            }

            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

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

    /// <summary>A fresh, unique work directory for one test; deleted when the test ends.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpcoder-authb-" + Guid.NewGuid().ToString("N"));
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

    private static AgentOptions OptionsFor(string workDirectory) => new()
    {
        WorkDirectory = workDirectory,
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
    };

    private static AIFunction FunctionNamed(ChatOptions? options, string name) =>
        options!.Tools!.OfType<AIFunction>().Single(f => f.Name == name);

    private static List<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.Select(t => t.Name).ToList() ?? [];

    // ========================================================================
    // The captured pair is fixed at construction — including ENABLING late
    // ========================================================================

    [Fact]
    public async Task EnablingCallbackAfterConstruction_DoesNotAuthorizeTheExistingAgent()
    {
        var invocations = 0;
        var authorizations = 0;

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];

        // The agent is constructed WITHOUT an authorization callback...
        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        // ...and authorization is enabled only AFTER construction.
        opts.AuthorizeTool = (_, _) =>
        {
            authorizations++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("Should never be consulted."));
        };

        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The existing agent keeps the policy it captured: no callback at all.
        Assert.Equal(0, authorizations);
        Assert.Equal(1, invocations);
        Assert.False(ToolAuthorizationGuard.IsGuarded(FunctionNamed(client.LastOptions, "do_it")));
    }

    [Fact]
    public async Task ReplacingCallbackAfterConstruction_AppliesToEveryExecutionOfThatAgent()
    {
        var first = 0;
        var second = 0;

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) =>
        {
            first++;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
            Round.Call("do_it", new()), Round.Say("ok"),
            Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        opts.AuthorizeTool = (_, _) =>
        {
            second++;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        await agent.ExecuteAsync("go again", TestContext.Current.CancellationToken);

        // Both executions of the already-constructed agent use the captured delegate.
        Assert.Equal(2, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task AuthorizationCallback_IsInvokedExactlyOncePerToolCall()
    {
        var calls = new ConcurrentQueue<string>();

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools =
        [
            AIFunctionFactory.Create(() => "a", "tool_a"),
            AIFunctionFactory.Create(() => "b", "tool_b")
        ];
        opts.AuthorizeTool = (context, _) =>
        {
            calls.Enqueue(context.ToolName);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
            Round.Call("tool_a", new()), Round.Call("tool_b", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(["tool_a", "tool_b"], calls.ToArray());
    }

    // ========================================================================
    // The MANUAL streaming dispatch path: denial before the side effect
    // ========================================================================

    [Fact]
    public async Task BashTool_Deny_OnManualStreamingPath_RunsNoProcess()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "bash-marker.txt");

        var opts = OptionsFor(dir.Path);
        opts.EnableBash = true;
        opts.ShowToolCallsInStream = true;
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("Shell access is not permitted."));

        var command = $"echo created > \"{marker}\"";
        var client = new ScriptedClient(
            Round.Call("execute_bash_command", new() { ["command"] = command }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);

        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "run it", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
        }

        // The process never started and the model saw the denial reason.
        Assert.False(File.Exists(marker), "the denied bash command must not have run");
        Assert.Contains("Shell access is not permitted.", text.ToString());
    }

    [Fact]
    public async Task Denial_OnManualStreamingPath_IsNotReportedAsAToolError()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.ShowToolCallsInStream = true;
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("Policy says no."));

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "go", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
        }

        var streamed = text.ToString();
        Assert.Contains("Policy says no.", streamed);
        // A denial is a refusal, not an "Error:" report of a thrown exception.
        Assert.DoesNotContain("Error:", streamed);
    }

    // ========================================================================
    // Argument-aware authorization for the remaining built-ins
    // ========================================================================

    [Fact]
    public async Task EditFileTool_Deny_LeavesTheFileUnchanged()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "notes.txt");
        await File.WriteAllTextAsync(target, "original", TestContext.Current.CancellationToken);

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        opts.EnableFileWrites = true;
        opts.AuthorizeTool = (context, _) =>
        {
            // Argument-aware: only the write to this exact path is refused.
            var path = context.Arguments.TryGetValue("filePath", out var value) ? value as string : null;
            return Task.FromResult(path == target
                ? ToolAuthorizationDecision.Deny("Editing notes.txt is blocked.")
                : ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
            Round.Call("edit_file", new()
            {
                ["filePath"] = target,
                ["oldString"] = "original",
                ["newString"] = "tampered"
            }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("edit it", TestContext.Current.CancellationToken);

        Assert.Equal("original", File.ReadAllText(target));
        Assert.Contains("Editing notes.txt is blocked.", client.ToolResults.Single());
    }

    [Fact]
    public async Task GrepTool_Deny_SeesThePatternAndReturnsNoMatches()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, "code.cs"),
            "class Secret { }",
            TestContext.Current.CancellationToken);

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        string? seenPattern = null;
        opts.AuthorizeTool = (context, _) =>
        {
            seenPattern = context.Arguments.TryGetValue("pattern", out var value) ? value as string : null;
            return Task.FromResult(ToolAuthorizationDecision.Deny("Searching is blocked."));
        };

        var client = new ScriptedClient(
            Round.Call("grep", new() { ["pattern"] = "Secret" }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("search it", TestContext.Current.CancellationToken);

        Assert.Equal("Secret", seenPattern);
        var result = client.ToolResults.Single();
        Assert.Contains("Searching is blocked.", result);
        Assert.DoesNotContain("class Secret", result);
    }

    [Fact]
    public async Task GlobTool_Deny_SeesTheGlobPatternAndReturnsNoPaths()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, "a.cs"), "x", TestContext.Current.CancellationToken);

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        string? seenPattern = null;
        opts.AuthorizeTool = (context, _) =>
        {
            seenPattern = context.Arguments.TryGetValue("pattern", out var value) ? value as string : null;
            return Task.FromResult(ToolAuthorizationDecision.Deny("Globbing is blocked."));
        };

        var client = new ScriptedClient(
            Round.Call("glob", new() { ["pattern"] = "*.cs" }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("list it", TestContext.Current.CancellationToken);

        Assert.Equal("*.cs", seenPattern);
        var result = client.ToolResults.Single();
        Assert.Contains("Globbing is blocked.", result);
        Assert.DoesNotContain("a.cs", result);
    }

    // ========================================================================
    // Direct invocation of a guarded function
    // ========================================================================

    [Fact]
    public async Task GuardedFunction_InvokedDirectlyAndDenied_DoesNotRunTheTool()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("Direct calls are blocked too."));

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "do_it");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        Assert.Contains("Direct calls are blocked too.", ResultToString(result));
    }

    [Fact]
    public async Task WrappedFunction_ReceivesTheCallersCancellationToken_Unchanged()
    {
        var callbackToken = default(CancellationToken);
        var functionToken = default(CancellationToken);

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools =
        [
            AIFunctionFactory.Create((string id, CancellationToken ct) => { functionToken = ct; return id; }, "echo_id")
        ];
        opts.AuthorizeTool = (_, ct) =>
        {
            callbackToken = ct;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "echo_id");
        using var cts = new CancellationTokenSource();
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "x" }),
            cts.Token);

        // The callback AND the wrapped function see the very same token instance.
        Assert.Equal(cts.Token, callbackToken);
        Assert.Equal(cts.Token, functionToken);
    }

    [Fact]
    public async Task GuardedFunction_InvokedDirectly_PassesTheSameArgumentsInstanceToTheTool()
    {
        var captured = new ArgumentsCapturingFunction();

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [captured];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "capture");
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["x"] = 1 });
        await tool.InvokeAsync(args, TestContext.Current.CancellationToken);

        // On allow the wrapped function runs completely unchanged, with the very same
        // arguments instance the caller supplied.
        Assert.Same(args, captured.Seen);
    }

    /// <summary>An <see cref="AIFunction"/> that records the arguments instance it received.</summary>
    private sealed class ArgumentsCapturingFunction : AIFunction
    {
        public AIFunctionArguments? Seen { get; private set; }

        public override string Name => "capture";
        public override string Description => "captures its arguments";

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Seen = arguments;
            return new((object?)"captured");
        }
    }

    // ========================================================================
    // Non-invokable custom tools fail closed before any model call
    // ========================================================================

    private static AITool DeclarationTool(string name = "provider_tool")
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        return AIFunctionFactory.CreateDeclaration(name, "provider-executed", schema, null);
    }

    [Fact]
    public async Task NonAIFunctionCustomTool_FailsClosedBeforeAnyModelCall()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [DeclarationTool()];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ExecuteAsync("go", TestContext.Current.CancellationToken));

        // The boundary is enforced while the request is built: no model call was made.
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task NonAIFunctionCustomTool_FailsClosedOnTheStreamingPathToo()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [DeclarationTool()];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in agent.ExecuteStreamingAsync(null, "go", TestContext.Current.CancellationToken))
            {
                // The failure happens while the request is built, before any model round.
            }
        });

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ReservedName_StillWinsOverTheNonInvokableToolFailure()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [DeclarationTool("start_sub_agent")];
        opts.SubAgents = new SharpCoder.SubAgents.SubAgentOptions();
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        // The reserved-name diagnostic keeps its precedence and wording over the
        // non-invokable-tool failure.
        var ex = Assert.Throws<ArgumentException>(() => new CodingAgent(new ScriptedClient(Round.Say("x")), opts));
        Assert.Contains("start_sub_agent", ex.Message);
        Assert.Contains("reserved name", ex.Message);
    }

    // ========================================================================
    // A callback cancellation is not converted into a denial
    // ========================================================================

    [Fact]
    public async Task CallbackCancellation_IsNotConvertedIntoADenialRefusal()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => throw new OperationCanceledException("cancelled by the host");

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The guard never turns a cancellation into a denial refusal: whatever the dispatch
        // path did with the cancellation, it was not the fixed "was not executed" refusal.
        var result = client.ToolResults.Single();
        Assert.DoesNotContain("was not executed", result);
        Assert.DoesNotContain("could not be authorized", result);
    }

    // ========================================================================
    // The documented trust boundary: a host-retained custom function is NOT guarded
    // ========================================================================

    [Fact]
    public async Task HostRetainedCustomFunction_IsNotGuarded_AndBypassesTheCallback()
    {
        var authorizations = 0;
        var hostTool = AIFunctionFactory.Create(() => "host-side", "host_tool");

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [hostTool];
        opts.AuthorizeTool = (_, _) =>
        {
            authorizations++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("never"));
        };

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The REGISTERED tool is guarded...
        var registered = FunctionNamed(client.LastOptions, "host_tool");
        Assert.True(ToolAuthorizationGuard.IsGuarded(registered));
        Assert.NotSame(hostTool, registered);

        // ...but the instance the host kept its own reference to is a trusted host entry
        // point: invoking it directly does not go through the authorization boundary.
        var direct = await hostTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            TestContext.Current.CancellationToken);

        Assert.Equal("host-side", ResultToString(direct));
        Assert.Equal(0, authorizations);
    }

    // ========================================================================
    // Usage and session semantics are unchanged by the boundary
    // ========================================================================

    [Fact]
    public async Task AuthorizationEnabled_KeepsToolCallCountAndUsageSemantics()
    {
        async Task<(int ToolCalls, long SessionToolCalls, long ToolDefinitionTokens, long InputTokens)> RunAsync(bool authorize)
        {
            var opts = OptionsFor(Path.GetTempPath());
            opts.EnableBash = true;
            opts.EnableFileOps = true;
            opts.CustomTools = [AIFunctionFactory.Create((string id) => $"made:{id}", "create_goal")];
            if (authorize)
                opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

            var client = new ScriptedClient(
                Round.Call("create_goal", new() { ["id"] = "g1" }),
                Round.Say("ok"));
            var agent = new CodingAgent(client, opts);
            var session = new AgentSession();
            var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

            return (result.ToolCallCount,
                    session.TotalToolCalls,
                    result.TokenUsage.Total.Estimated.ToolDefinitions,
                    result.TokenUsage.Total.InputTokens);
        }

        var baseline = await RunAsync(authorize: false);
        var guarded = await RunAsync(authorize: true);

        Assert.Equal(baseline.ToolCalls, guarded.ToolCalls);
        Assert.Equal(baseline.SessionToolCalls, guarded.SessionToolCalls);
        Assert.Equal(baseline.ToolDefinitionTokens, guarded.ToolDefinitionTokens);
        Assert.Equal(baseline.InputTokens, guarded.InputTokens);
    }

    [Fact]
    public async Task DeniedToolCall_StillCountsAsAToolCallInTheSession()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("no"));

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        var session = new AgentSession();
        var result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);

        // A refused call is still a tool call the model made: the session accounting does
        // not change when a call is denied.
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, session.TotalToolCalls);
    }

    // ========================================================================
    // The argument snapshot: every JSON value kind, and the never-throw contract
    // ========================================================================

    [Fact]
    public void Snapshot_ConvertsEveryJsonValueKindToPlainClrValues()
    {
        var json = JsonDocument.Parse(
            """{"s":"text","t":true,"f":false,"nul":null,"i":42,"d":1.5,"arr":["a",1,true],"obj":{"k":"v"}}""")
            .RootElement;

        var snapshot = ToolAuthorizationGuard.SnapshotArguments(new Dictionary<string, object?> { ["args"] = json });
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot["args"]);

        Assert.Equal("text", nested["s"]);
        Assert.Equal(true, nested["t"]);
        Assert.Equal(false, nested["f"]);
        Assert.Null(nested["nul"]);
        Assert.Equal(42L, nested["i"]);
        Assert.Equal(1.5m, nested["d"]);

        var arr = Assert.IsAssignableFrom<object?[]>(nested["arr"]);
        Assert.Equal(["a", 1L, true], arr);

        var obj = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(nested["obj"]);
        Assert.Equal("v", obj["k"]);
    }

    [Fact]
    public void Snapshot_ConvertsAJsonDocumentRoot()
    {
        using var document = JsonDocument.Parse("""{"filePath":"a.txt"}""");
        var snapshot = ToolAuthorizationGuard.SnapshotArguments(
            new Dictionary<string, object?> { ["args"] = document });

        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot["args"]);
        Assert.Equal("a.txt", nested["filePath"]);
    }

    [Fact]
    public void Snapshot_NeverThrows_OnAnUnreadableJsonElement()
    {
        // A JsonElement whose backing JsonDocument was disposed cannot be read any more:
        // the snapshot must pass it through rather than throw, so building the snapshot can
        // never prevent the host callback from running and deciding.
        var document = JsonDocument.Parse("""{"filePath":"a.txt"}""");
        var element = document.RootElement;
        document.Dispose();

        var snapshot = ToolAuthorizationGuard.SnapshotArguments(
            new Dictionary<string, object?> { ["filePath"] = element });

        // The unreadable element is passed through as-is (a JsonElement, not a converted
        // string) instead of being converted or throwing.
        var passed = Assert.IsAssignableFrom<JsonElement>(snapshot["filePath"]);
        Assert.Throws<ObjectDisposedException>(() => passed.GetProperty("filePath"));
    }

    [Fact]
    public void Snapshot_ConvertsANumberTooLargeForDecimalToADouble()
    {
        var json = JsonDocument.Parse("""{"huge":1e30}""").RootElement;

        var snapshot = ToolAuthorizationGuard.SnapshotArguments(new Dictionary<string, object?> { ["args"] = json });
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot["args"]);

        // 1e30 does not fit in a decimal, so the snapshot carries it as a double.
        Assert.Equal(1e30d, Assert.IsType<double>(nested["huge"]));
    }

    [Fact]
    public void Snapshot_NeverThrows_OnAnUnreadableJsonDocument()
    {
        var document = JsonDocument.Parse("""{"filePath":"a.txt"}""");
        document.Dispose();

        // A disposed JsonDocument cannot be read either: it must be passed through rather
        // than throw, so the host callback can still run and decide.
        var snapshot = ToolAuthorizationGuard.SnapshotArguments(
            new Dictionary<string, object?> { ["args"] = document });

        Assert.IsType<JsonDocument>(snapshot["args"]);
    }

    [Fact]
    public void Snapshot_PreservesANullArgumentValue()
    {
        var snapshot = ToolAuthorizationGuard.SnapshotArguments(
            new Dictionary<string, object?> { ["maybe"] = null });

        Assert.True(snapshot.ContainsKey("maybe"));
        Assert.Null(snapshot["maybe"]);
    }

    // ========================================================================
    // The wrapper's metadata surface
    // ========================================================================

    [Fact]
    public async Task GuardedTool_ExposesTheWrappedFunctionMetadataAndToString()
    {
        var custom = AIFunctionFactory.Create((string id) => $"made:{id}", "create_goal", "Creates a goal.");

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [custom];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var guarded = FunctionNamed(client.LastOptions, "create_goal");

        // The additional-properties surface of the wrapped function stays reachable.
        Assert.Equal(custom.AdditionalProperties, guarded.AdditionalProperties);
        // ToString() delegates to the wrapped function.
        Assert.Equal(custom.ToString(), guarded.ToString());
    }

    [Fact]
    public async Task GuardedTool_GetServiceRejectsANullServiceType()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "x", "my_tool")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var guarded = FunctionNamed(client.LastOptions, "my_tool");
        Assert.Throws<ArgumentNullException>(() => guarded.GetService(null!));
    }

    // ========================================================================
    // Options defaults
    // ========================================================================

    [Fact]
    public void AuthorizationOptions_DefaultToDisabled()
    {
        var options = new AgentOptions();
        Assert.Null(options.AuthorizeTool);
        Assert.Null(options.ToolAuthorizationCorrelationId);
    }

    [Fact]
    public void AuthorizationOptions_AreSettableAndReadable()
    {
        var options = new AgentOptions();
        options.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);
        options.ToolAuthorizationCorrelationId = "corr-1";

        Assert.NotNull(options.AuthorizeTool);
        Assert.Equal("corr-1", options.ToolAuthorizationCorrelationId);
    }
}
