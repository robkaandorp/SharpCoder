using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

public class SubAgentToolsTests
{
    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>Chat client that returns a fixed text response and captures the ChatOptions it received.</summary>
    private sealed class OptionsCapturingClient : IChatClient
    {
        private readonly string _response;
        private readonly TaskCompletionSource<bool>? _gate;

        public ChatOptions? LastOptions { get; private set; }
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
        public int CallCount { get; private set; }

        public OptionsCapturingClient(string response = "Done.", TaskCompletionSource<bool>? gate = null)
        {
            _response = response;
            _gate = gate;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            LastOptions = options;
            CallCount++;

            if (_gate is not null)
            {
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelTcs.TrySetResult(true)))
                {
                    await Task.WhenAny(_gate.Task, cancelTcs.Task).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _response))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 }
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            LastOptions = options;
            CallCount++;
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(_response)],
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Thread-safe fixed-response client, safe to call from several threads at once.
    /// Used by the concurrent manager-initialization test.
    /// </summary>
    private sealed class ConcurrentCapturingClient : IChatClient
    {
        private readonly Action? _onCall;
        public ConcurrentQueue<ChatOptions?> ReceivedOptions { get; } = new();

        public ConcurrentCapturingClient(Action? onCall = null) => _onCall = onCall;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages.ToList();
            ReceivedOptions.Enqueue(options);
            _onCall?.Invoke();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Enqueue(options);
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("done")],
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A single scripted round: either a tool call or a final text response.</summary>
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
    /// Parent chat client that replays a script of rounds, records every ChatOptions it receives
    /// and every tool result it observes in the incoming message list.
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private readonly Action<int>? _beforeRound;
        private int _index;

        public ChatOptions? LastOptions { get; private set; }
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
        public List<string> ToolResults { get; } = [];

        public ScriptedClient(IReadOnlyList<Round> rounds, Action<int>? beforeRound = null)
        {
            _rounds = rounds;
            _beforeRound = beforeRound;
        }

        private Round Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var list = messages.ToList();
            ReceivedMessages.Add(list);
            LastOptions = options;

            foreach (var content in list.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
                ToolResults.Add(ResultToString(content.Result));

            var i = _index++;
            _beforeRound?.Invoke(i);
            return i < _rounds.Count ? _rounds[i] : Round.Say("done");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var round = Next(messages, options);
            if (round.ToolName is not null)
            {
                var msg = new ChatMessage(ChatRole.Assistant,
                    new AIContent[] { new FunctionCallContent("call_" + Guid.NewGuid().ToString("N"), round.ToolName, round.ToolArgs) });
                return Task.FromResult(new ChatResponse(msg) { FinishReason = ChatFinishReason.ToolCalls });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "done"))
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
                    Contents = [new FunctionCallContent("call_" + Guid.NewGuid().ToString("N"), round.ToolName, round.ToolArgs)]
                };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
                yield break;
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(round.Text ?? "done")],
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static string ResultToString(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? (je.GetString() ?? string.Empty) : je.GetRawText();
        return result.ToString() ?? string.Empty;
    }

    private static async Task<string> InvokeAsync(IList<AITool> tools, string name, Dictionary<string, object?>? args = null)
    {
        var fn = tools.OfType<AIFunction>().Single(f => f.Name == name);
        var result = await fn.InvokeAsync(new AIFunctionArguments(args ?? new Dictionary<string, object?>()));
        return ResultToString(result);
    }

    private static AgentOptions ParentOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = true,
        EnableFileOps = true,
        EnableFileWrites = true,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        AutoLoadWorkspaceInstructions = false,
    };

    private static readonly string[] SubAgentToolNames =
        ["start_sub_agent", "await_sub_agents", "get_sub_agent_status", "list_sub_agent_models"];

    private static List<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.OfType<AIFunction>().Select(f => f.Name).ToList() ?? [];

    private static string SystemPromptOf(IList<ChatMessage> messages) =>
        messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;

    private const string GuidanceFragment = "delegate self-contained subtasks";

    /// <summary>
    /// Asserts the sub-agent is Running and stays Running, giving any erroneous
    /// cancellation propagation from the parent execution time to take effect.
    /// </summary>
    private static async Task AssertStaysRunningAsync(SubAgentManager manager, string id)
    {
        for (var i = 0; i < 10; i++)
        {
            var snapshot = manager.GetStatus(id);
            Assert.Single(snapshot);
            Assert.Equal(SubAgentStatus.Running, snapshot[0].Status);
            await Task.Delay(20);
        }
    }

    /// <summary>Reads the CodingAgent's lazily created sub-agent manager via reflection.</summary>
    private static SubAgentManager GetManager(CodingAgent agent)
    {
        var field = typeof(CodingAgent).GetField("_subAgentManager",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var manager = field!.GetValue(agent) as SubAgentManager;
        Assert.NotNull(manager);
        return manager!;
    }

    // ========================================================================
    // Tool presence / absence
    // ========================================================================

    [Fact]
    public async Task No_SubAgent_Tools_When_SubAgents_Null()
    {
        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, ParentOptions());

        await agent.ExecuteAsync("hello", TestContext.Current.CancellationToken);

        var names = ToolNames(client.LastOptions);
        foreach (var n in SubAgentToolNames)
            Assert.DoesNotContain(n, names);
    }

    [Fact]
    public async Task All_Four_Tools_Present_When_SubAgents_Set()
    {
        var client = new OptionsCapturingClient();
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        var agent = new CodingAgent(client, parent);

        await agent.ExecuteAsync("hello", TestContext.Current.CancellationToken);

        var names = ToolNames(client.LastOptions);
        foreach (var n in SubAgentToolNames)
            Assert.Contains(n, names);
    }

    // ========================================================================
    // Effective-enabled rule
    // ========================================================================

    [Fact]
    public async Task Tools_And_Guidance_Survive_SubAgents_Being_Set_To_Null()
    {
        var client = new OptionsCapturingClient();
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        var agent = new CodingAgent(client, parent);

        await agent.ExecuteAsync("first", TestContext.Current.CancellationToken);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        parent.SubAgents = null;
        await agent.ExecuteAsync("second", TestContext.Current.CancellationToken);

        var names = ToolNames(client.LastOptions);
        foreach (var n in SubAgentToolNames)
            Assert.Contains(n, names);
        Assert.Contains(GuidanceFragment, SystemPromptOf(client.ReceivedMessages[^1]));
        Assert.Equal(1, agent.SubAgentManagerCreateCount);
    }

    // ========================================================================
    // Reserved-name collision
    // ========================================================================

    [Fact]
    public void Reserved_Name_In_CustomTools_Throws_From_Constructor()
    {
        var parent = ParentOptions();
        parent.CustomTools.Add(AIFunctionFactory.Create(() => "x", "start_sub_agent", "collides"));
        parent.SubAgents = new SubAgentOptions();

        var ex = Assert.Throws<ArgumentException>(() => new CodingAgent(new OptionsCapturingClient(), parent));
        Assert.Contains("start_sub_agent", ex.Message);
        Assert.Contains("reserved name", ex.Message);
    }

    [Fact]
    public async Task Reserved_Name_Added_After_Construction_Throws_At_BuildChatOptions()
    {
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        var agent = new CodingAgent(new OptionsCapturingClient(), parent);

        // Post-construction mutation.
        parent.CustomTools.Add(AIFunctionFactory.Create(() => "x", "await_sub_agents", "collides"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => agent.ExecuteAsync("go", TestContext.Current.CancellationToken));
        Assert.Contains("await_sub_agents", ex.Message);
    }

    [Fact]
    public void Reserved_Name_On_NonAIFunction_Tool_Throws_From_Constructor()
    {
        var parent = ParentOptions();
        // A non-invokable declaration still carries a Name and must be rejected.
        var declaration = AIFunctionFactory
            .Create(() => "x", "get_sub_agent_status", "collides").AsDeclarationOnly();
        Assert.False(declaration is AIFunction); // non-invokable: bypasses an AIFunction-only check
        parent.CustomTools.Add(declaration);
        parent.SubAgents = new SubAgentOptions();

        var ex = Assert.Throws<ArgumentException>(() => new CodingAgent(new OptionsCapturingClient(), parent));
        Assert.Contains("get_sub_agent_status", ex.Message);
    }

    [Fact]
    public async Task Reserved_Name_On_NonAIFunction_Tool_Added_After_Construction_Throws_At_BuildChatOptions()
    {
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        var agent = new CodingAgent(new OptionsCapturingClient(), parent);

        var declaration = AIFunctionFactory
            .Create(() => "x", "list_sub_agent_models", "collides").AsDeclarationOnly();
        Assert.False(declaration is AIFunction); // non-invokable: bypasses an AIFunction-only check
        parent.CustomTools.Add(declaration);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => agent.ExecuteAsync("go", TestContext.Current.CancellationToken));
        Assert.Contains("list_sub_agent_models", ex.Message);
    }

    // ========================================================================
    // Capability ceiling (end-to-end through CodingAgent)
    // ========================================================================

    private static async Task<OptionsCapturingClient> RunStartSubAgentAsync(
        Action<AgentOptions> configureParent,
        Action<SubAgentOptions> configureSubAgents,
        Dictionary<string, object?> startArgs)
    {
        var subClient = new OptionsCapturingClient("sub done");
        var parent = ParentOptions();
        configureParent(parent);

        var subOptions = new SubAgentOptions { DefaultClient = subClient };
        configureSubAgents(subOptions);
        parent.SubAgents = subOptions;

        var script = new ScriptedClient(
        [
            Round.Call("start_sub_agent", startArgs),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("all done"),
        ]);

        var agent = new CodingAgent(script, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);
        return subClient;
    }

    [Fact]
    public async Task Ceiling_SubAgent_Gets_No_Bash_When_Parent_Bash_Disabled()
    {
        var subClient = await RunStartSubAgentAsync(
            p => p.EnableBash = false,
            o => o.DefaultEnableBash = true,
            new Dictionary<string, object?> { ["task"] = "analyze", ["enable_bash"] = true });

        Assert.DoesNotContain("execute_bash_command", ToolNames(subClient.LastOptions));
    }

    [Fact]
    public async Task Ceiling_SubAgent_Gets_No_Writes_When_Parent_Writes_Disabled()
    {
        var subClient = await RunStartSubAgentAsync(
            p => p.EnableFileWrites = false,
            o => { o.DefaultEnableFileOps = true; o.DefaultEnableFileWrites = true; },
            new Dictionary<string, object?> { ["task"] = "analyze", ["enable_file_writes"] = true });

        var names = ToolNames(subClient.LastOptions);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("edit_file", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public async Task Ceiling_SubAgent_Gets_Capabilities_When_Parent_Enabled_And_Override_True()
    {
        var subClient = await RunStartSubAgentAsync(
            p => { p.EnableBash = true; p.EnableFileWrites = true; p.EnableFileOps = true; },
            o => { o.DefaultEnableBash = false; o.DefaultEnableFileOps = true; o.DefaultEnableFileWrites = false; },
            new Dictionary<string, object?>
            {
                ["task"] = "analyze",
                ["enable_bash"] = true,
                ["enable_file_writes"] = true
            });

        var names = ToolNames(subClient.LastOptions);
        Assert.Contains("execute_bash_command", names);
        Assert.Contains("write_file", names);
        Assert.Contains("edit_file", names);
    }

    [Fact]
    public async Task Ceiling_FileOps_And_Skills_Follow_Manager_Defaults_Clamped_By_Parent()
    {
        // Parent has file ops but NOT skills; manager defaults enable both.
        var subClient = await RunStartSubAgentAsync(
            p => { p.EnableFileOps = true; p.EnableSkills = false; },
            o => { o.DefaultEnableFileOps = true; o.DefaultEnableSkills = true; },
            new Dictionary<string, object?> { ["task"] = "analyze" });

        var names = ToolNames(subClient.LastOptions);
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("load_skill", names);
        Assert.DoesNotContain("list_skills", names);
    }

    [Fact]
    public async Task Ceiling_FileOps_Disabled_By_Manager_Default_Even_When_Parent_Allows()
    {
        var subClient = await RunStartSubAgentAsync(
            p => { p.EnableFileOps = true; p.EnableSkills = true; },
            o => { o.DefaultEnableFileOps = false; o.DefaultEnableSkills = true; },
            new Dictionary<string, object?> { ["task"] = "analyze" });

        var names = ToolNames(subClient.LastOptions);
        Assert.DoesNotContain("read_file", names);
        Assert.Contains("load_skill", names);
    }

    // ========================================================================
    // Flat design regression
    // ========================================================================

    [Fact]
    public async Task SubAgent_Never_Receives_SubAgent_Tools()
    {
        var subClient = await RunStartSubAgentAsync(
            _ => { }, _ => { },
            new Dictionary<string, object?> { ["task"] = "analyze" });

        var names = ToolNames(subClient.LastOptions);
        foreach (var n in SubAgentToolNames)
            Assert.DoesNotContain(n, names);
    }

    // ========================================================================
    // JSON contracts — direct tool invocation
    // ========================================================================

    private static SubAgentManager CreateManager(SubAgentOptions options, AgentOptions? parent = null, IChatClient? defaultClient = null)
        => new(options, defaultClient ?? new OptionsCapturingClient(), parent ?? ParentOptions(), logger: null);

    [Fact]
    public async Task Start_Returns_Running_Without_Awaiting_Completion()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subClient = new OptionsCapturingClient("slow", gate);
        var options = new SubAgentOptions { DefaultClient = subClient };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" });

        Assert.Equal("{\"id\":\"sub-1\",\"status\":\"Running\"}", json);

        gate.SetResult(true);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_Validation_Failure_Returns_Error_Object_Without_Id()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        options.AvailableModels.Add(new SubAgentModelInfo("m1", "first model", 1000));
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "start_sub_agent",
            new Dictionary<string, object?> { ["task"] = "work", ["model"] = "nope" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
        Assert.Contains("Unknown model 'nope'", doc.RootElement.GetProperty("error").GetString());

        // Nothing was started.
        Assert.Empty(manager.GetStatus());
    }

    [Fact]
    public async Task Await_Returns_Array_With_Expected_Fields()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("summary text") };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" });
        var json = await InvokeAsync(tools, "await_sub_agents");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var item = doc.RootElement[0];
        Assert.Equal("sub-1", item.GetProperty("id").GetString());
        Assert.Equal("Completed", item.GetProperty("status").GetString());
        Assert.Equal("summary text", item.GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("error").ValueKind);
        Assert.Equal(11, item.GetProperty("input_tokens").GetInt64());
        Assert.Equal(7, item.GetProperty("output_tokens").GetInt64());
    }

    [Fact]
    public async Task Status_Always_Returns_Array()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("ok") };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" });
        await InvokeAsync(tools, "await_sub_agents");

        var all = await InvokeAsync(tools, "get_sub_agent_status");
        using (var doc = JsonDocument.Parse(all))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            var item = doc.RootElement[0];
            Assert.Equal("sub-1", item.GetProperty("id").GetString());
            Assert.Equal("Completed", item.GetProperty("status").GetString());
            Assert.NotNull(item.GetProperty("started_at").GetString());
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("completed_at").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("model").ValueKind);
            Assert.Equal("ok", item.GetProperty("summary").GetString());
        }

        var known = await InvokeAsync(tools, "get_sub_agent_status", new Dictionary<string, object?> { ["id"] = "sub-1" });
        using (var doc = JsonDocument.Parse(known))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
        }

        var unknown = await InvokeAsync(tools, "get_sub_agent_status", new Dictionary<string, object?> { ["id"] = "sub-999" });
        Assert.Equal("[]", unknown);
    }

    [Fact]
    public async Task ListModels_Returns_Catalog()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        options.AvailableModels.Add(new SubAgentModelInfo("fast", "A fast model", 128_000));
        options.AvailableModels.Add(new SubAgentModelInfo("plain"));
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "list_sub_agent_models");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("fast", doc.RootElement[0].GetProperty("id").GetString());
        Assert.Equal("A fast model", doc.RootElement[0].GetProperty("description").GetString());
        Assert.Equal(128_000, doc.RootElement[0].GetProperty("context_window").GetInt32());
        Assert.Equal("plain", doc.RootElement[1].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("context_window").ValueKind);
    }

    [Fact]
    public async Task ListModels_Empty_Catalog_Returns_Exact_Message()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "list_sub_agent_models");

        Assert.Equal(
            "{\"models\":[],\"message\":\"No sub-agent models configured; the default model is used.\"}",
            json);
    }

    // ========================================================================
    // Saturated concurrency
    // ========================================================================

    [Fact]
    public async Task Start_Blocks_When_Concurrency_Cap_Is_Reached()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 1,
            DefaultClient = new OptionsCapturingClient("slow", gate)
        };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "first" });

        var second = InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "second" });
        var winner = await Task.WhenAny(second, Task.Delay(300, TestContext.Current.CancellationToken));
        Assert.NotSame(second, winner); // still blocked on the slot

        gate.SetResult(true);
        var json = await second;
        Assert.Equal("{\"id\":\"sub-2\",\"status\":\"Running\"}", json);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Saturated_Slot_Wait_Cancellation_Throws_OperationCanceled_And_Starts_Nothing()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 1,
            DefaultClient = new OptionsCapturingClient("slow", gate)
        };
        await using var manager = CreateManager(options);
        using var cts = new CancellationTokenSource();
        var tools = SubAgentTools.BuildTools(manager, options, cts.Token);

        await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "first" });

        var second = InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "second" });
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        var ex = await Record.ExceptionAsync(async () => await second);
        Assert.IsType<OperationCanceledException>(ex, exactMatch: false);

        // Only the first sub-agent was ever tracked — the cancelled slot wait started nothing.
        var tracked = manager.GetStatus();
        Assert.Single(tracked);
        Assert.Equal("sub-1", tracked[0].Id);
        Assert.Equal(SubAgentStatus.Running, tracked[0].Status);

        gate.SetResult(true);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // Cancellation semantics
    // ========================================================================

    [Fact]
    public async Task Await_Cancellation_Throws_And_Leaves_SubAgent_Running()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("slow", gate) };
        await using var manager = CreateManager(options);
        using var cts = new CancellationTokenSource();
        var tools = SubAgentTools.BuildTools(manager, options, cts.Token);

        await InvokeAsync(tools, "start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" });

        var awaiting = InvokeAsync(tools, "await_sub_agents");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        // Must be a cancellation (TaskCanceledException derives from OperationCanceledException),
        // never a generic failure and never an "Error: ..." JSON result.
        var ex = await Record.ExceptionAsync(async () => await awaiting);
        Assert.IsType<OperationCanceledException>(ex, exactMatch: false);

        // Cancelling the execution must NOT cancel the running sub-agent.
        var status = manager.GetStatus();
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Running, status[0].Status);

        gate.SetResult(true);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Await_Cancellation_Propagates_Through_ExecuteAsync()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("slow", gate) };

        using var cts = new CancellationTokenSource();
        // Cancel just before the second round's tool (await_sub_agents) is invoked.
        var script = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" }),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("never reached"),
        ], beforeRound: i => { if (i == 1) cts.Cancel(); });

        var agent = new CodingAgent(script, parent);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.ExecuteAsync("delegate", cts.Token));

        // The child started in round 0 must still be Running: cancelling the parent
        // execution must never propagate into the sub-agent's own lifetime. Give any
        // (incorrect) cancellation propagation a window to materialize first.
        var manager = GetManager(agent);
        await AssertStaysRunningAsync(manager, "sub-1");

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { "sub-1" }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Await_Cancellation_Propagates_Through_Manual_Streaming_Path()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = ParentOptions();
        parent.ShowToolCallsInStream = true;
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("slow", gate) };

        using var cts = new CancellationTokenSource();
        var script = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "work" }),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("never reached"),
        ], beforeRound: i => { if (i == 1) cts.Cancel(); });

        var agent = new CodingAgent(script, parent);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.ExecuteStreamingAsync(null, "delegate", cts.Token))
            {
            }
        });

        var manager = GetManager(agent);
        await AssertStaysRunningAsync(manager, "sub-1");

        gate.SetResult(true);
        await manager.AwaitAsync(new[] { "sub-1" }, TestContext.Current.CancellationToken);
    }

    // ========================================================================
    // System prompt guidance
    // ========================================================================

    [Fact]
    public async Task System_Prompt_Guidance_Only_When_SubAgents_Enabled()
    {
        var without = new OptionsCapturingClient();
        var agentWithout = new CodingAgent(without, ParentOptions());
        await agentWithout.ExecuteAsync("hi", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(GuidanceFragment, SystemPromptOf(without.ReceivedMessages[0]));

        var with = new OptionsCapturingClient();
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        var agentWith = new CodingAgent(with, parent);
        await agentWith.ExecuteAsync("hi", TestContext.Current.CancellationToken);
        Assert.Contains(GuidanceFragment, SystemPromptOf(with.ReceivedMessages[0]));
    }

    // ========================================================================
    // End-to-end scripted delegation
    // ========================================================================

    [Fact]
    public async Task EndToEnd_Start_Then_Await_Returns_SubAgent_Summary()
    {
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions
        {
            DefaultClient = new OptionsCapturingClient("the sub-agent summary")
        };

        var script = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "analyze the repo" }),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("finished"),
        ]);

        var agent = new CodingAgent(script, parent);
        var result = await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal("finished", result.Message);
        Assert.Contains(script.ToolResults, r => r.Contains("the sub-agent summary"));
        Assert.Contains(script.ToolResults, r => r.Contains("\"status\":\"Running\""));
    }

    // ========================================================================
    // Manager lifetime
    // ========================================================================

    [Fact]
    public void Manager_Created_Once_Under_Truly_Concurrent_Executions()
    {
        // Two dedicated threads are released simultaneously by a Barrier so both can be
        // inside GetOrCreateSubAgentManager at once. Repeated so that a missing
        // double-checked lock is caught reliably rather than probabilistically.
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var parent = ParentOptions();
            parent.SubAgents = new SubAgentOptions { DefaultClient = new ConcurrentCapturingClient() };
            var agent = new CodingAgent(new ConcurrentCapturingClient(), parent);

            using var barrier = new Barrier(2);
            var errors = new ConcurrentQueue<Exception>();

            void Run()
            {
                try
                {
                    barrier.SignalAndWait();
                    agent.ExecuteAsync("go", CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }

            var t1 = new Thread(Run) { IsBackground = true };
            var t2 = new Thread(Run) { IsBackground = true };
            t1.Start();
            t2.Start();
            Assert.True(t1.Join(TimeSpan.FromSeconds(30)));
            Assert.True(t2.Join(TimeSpan.FromSeconds(30)));

            Assert.Empty(errors);
            Assert.Equal(1, agent.SubAgentManagerCreateCount);
        }
    }

    [Fact]
    public async Task SubAgent_From_Execution1_Is_Awaitable_In_Execution2()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = ParentOptions();
        parent.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("cross-exec summary", gate) };

        // Rounds 0-1 belong to execution 1, rounds 2-3 to execution 2.
        var script = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "long work" }),
            Round.Say("started"),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("collected"),
        ]);

        var agent = new CodingAgent(script, parent);
        await agent.ExecuteAsync("exec1", TestContext.Current.CancellationToken);
        gate.SetResult(true);
        var result = await agent.ExecuteAsync("exec2", TestContext.Current.CancellationToken);

        Assert.Equal("collected", result.Message);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);
        Assert.Contains(script.ToolResults, r => r.Contains("cross-exec summary"));
    }

    // ========================================================================
    // Defensive snapshot
    // ========================================================================

    [Fact]
    public async Task Manager_Uses_Defensive_Snapshot_Of_SubAgentOptions()
    {
        var parent = ParentOptions();
        var original = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        original.AvailableModels.Add(new SubAgentModelInfo("original-model", "the original"));
        parent.SubAgents = original;

        var script = new ScriptedClient(
        [
            Round.Say("warm up"),                                             // exec 1 — creates manager
            Round.Call("list_sub_agent_models", new Dictionary<string, object?>()),
            Round.Say("listed"),
        ]);

        var agent = new CodingAgent(script, parent);
        await agent.ExecuteAsync("exec1", TestContext.Current.CancellationToken);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        // (a) mutate the ORIGINAL list, and (b) replace the whole SubAgents instance.
        original.AvailableModels.Add(new SubAgentModelInfo("sneaky-model", "added later"));
        var replacement = new SubAgentOptions();
        replacement.AvailableModels.Add(new SubAgentModelInfo("replacement-model"));
        parent.SubAgents = replacement;

        await agent.ExecuteAsync("exec2", TestContext.Current.CancellationToken);

        var listing = Assert.Single(script.ToolResults);
        Assert.Contains("original-model", listing);
        Assert.DoesNotContain("sneaky-model", listing);
        Assert.DoesNotContain("replacement-model", listing);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);
    }
}
