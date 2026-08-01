using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.SubAgents;
using SharpCoder.Tools;

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

    [Fact]
    public async Task ListModels_Every_Model_Includes_SupportsVision_Property()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        options.AvailableModels.Add(new SubAgentModelInfo("vision-model", "vision", 128_000, supportsVision: true));
        options.AvailableModels.Add(new SubAgentModelInfo("text-model", "text", 8000, supportsVision: false));
        options.AvailableModels.Add(new SubAgentModelInfo("plain")); // 3-param constructor
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "list_sub_agent_models");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            Assert.True(element.TryGetProperty("supports_vision", out var visionProp),
                "Every model must include a supports_vision property.");
            Assert.True(visionProp.ValueKind == JsonValueKind.True || visionProp.ValueKind == JsonValueKind.False,
                "supports_vision must be a JSON boolean.");
        }
    }

    [Fact]
    public async Task ListModels_Vision_Marked_Model_Reports_True()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        options.AvailableModels.Add(new SubAgentModelInfo("gpt-4o", "vision model", 128_000, supportsVision: true));
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "list_sub_agent_models");

        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement[0];
        Assert.Equal("gpt-4o", element.GetProperty("id").GetString());
        Assert.True(element.GetProperty("supports_vision").GetBoolean());
    }

    [Fact]
    public async Task ListModels_Non_Vision_Models_Report_False()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        options.AvailableModels.Add(new SubAgentModelInfo("text-model", "text", 8000, supportsVision: false));
        options.AvailableModels.Add(new SubAgentModelInfo("plain")); // 3-param constructor defaults to false
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var json = await InvokeAsync(tools, "list_sub_agent_models");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.False(doc.RootElement[0].GetProperty("supports_vision").GetBoolean());
        Assert.False(doc.RootElement[1].GetProperty("supports_vision").GetBoolean());
    }

    [Fact]
    public async Task ListModels_Tool_Description_Documents_SupportsVision_Field()
    {
        var options = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
        await using var manager = CreateManager(options);
        var tools = SubAgentTools.BuildTools(manager, options, CancellationToken.None);

        var listTool = tools.OfType<AIFunction>().Single(f => f.Name == "list_sub_agent_models");
        Assert.Contains("supports_vision", listTool.Description);
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

    // ========================================================================
    // image_paths parameter tests
    //
    // These tests either set ImageLoader.FileProbe (a process-global test seam)
    // or rely on it being null so real files on disk are read. They therefore
    // join the "ImageLoader" xUnit collection defined in VisionInfrastructureTests,
    // which serializes every test that touches the seam.
    // ========================================================================

    private static readonly byte[] TinyPng =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] TinyPdf =
        [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'0'];

    /// <summary>
    /// Creates a temp directory, writes a file, and returns a tuple of
    /// (directoryPath, cleanupAction). The cleanup deletes the directory.
    /// </summary>
    private static (string dir, Action cleanup) TempDirWithFile(string fileName, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "subagent-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
        return (dir, () =>
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        });
    }

    /// <summary>
    /// Finds the last user-role message and returns its DataContent items.
    /// </summary>
    private static List<DataContent> UserDataContents(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
                return messages[i].Contents.OfType<DataContent>().ToList();
        }
        return new List<DataContent>();
    }

    /// <summary>
    /// Sub-agent <c>image_paths</c> tests. Serialized with every other ImageLoader
    /// test because <see cref="ImageLoader.FileProbe"/> is process-global: a parallel
    /// test could otherwise substitute a probe for a real-file test here, or clear the
    /// probe one of these tests installed.
    /// </summary>
    [Collection("ImageLoader")]
    public class ImagePathsTests
    {
        [Fact]
        public async Task ImagePaths_Valid_Png_Runs_SubAgent_With_Image_Content()
        {
            var (dir, cleanup) = TempDirWithFile("test.png", TinyPng);
            try
            {
                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = dir,
                    EnableBash = false,
                    EnableFileOps = true,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions { DefaultClient = subClient };
                subOptions.AvailableModels.Add(new SubAgentModelInfo("vision"));
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe the image",
                        ["image_paths"] = new[] { "test.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _));
                Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                // The sub-agent's LLM request must include the PNG bytes.
                Assert.NotEmpty(subClient.ReceivedMessages);
                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal("image/png", dataContents[0].MediaType);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_Valid_Pdf_Runs_SubAgent_With_Pdf_Content()
        {
            var (dir, cleanup) = TempDirWithFile("doc.pdf", TinyPdf);
            try
            {
                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = dir,
                    EnableBash = false,
                    EnableFileOps = true,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions { DefaultClient = subClient };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe the pdf",
                        ["image_paths"] = new[] { "doc.pdf" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _));
                Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                Assert.NotEmpty(subClient.ReceivedMessages);
                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal("application/pdf", dataContents[0].MediaType);
                Assert.Equal(TinyPdf, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_Nonexistent_Returns_Error_No_SubAgent_Tracked()
        {
            var subClient = new OptionsCapturingClient("sub done");
            var parent = new AgentOptions
            {
                WorkDirectory = Path.GetTempPath(),
                EnableBash = false,
                EnableSkills = false,
                AutoLoadWorkspaceInstructions = false,
            };
            var subOptions = new SubAgentOptions { DefaultClient = subClient };
            parent.SubAgents = subOptions;

            await using var manager = CreateManager(subOptions, parent, subClient);
            var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

            var json = await InvokeAsync(tools, "start_sub_agent",
                new Dictionary<string, object?>
                {
                    ["task"] = "describe",
                    ["image_paths"] = new[] { "doesnotexist.png" }
                });

            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("id", out _));
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
            Assert.Empty(manager.GetStatus());
            Assert.Equal(0, subClient.CallCount);
        }

        [Fact]
        public async Task ImagePaths_Escaping_Root_Returns_Error_No_SubAgent_Tracked()
        {
            var subClient = new OptionsCapturingClient("sub done");
            var parent = new AgentOptions
            {
                WorkDirectory = Path.GetTempPath(),
                EnableBash = false,
                EnableSkills = false,
                AutoLoadWorkspaceInstructions = false,
            };
            var subOptions = new SubAgentOptions { DefaultClient = subClient };
            parent.SubAgents = subOptions;

            await using var manager = CreateManager(subOptions, parent, subClient);
            var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

            var json = await InvokeAsync(tools, "start_sub_agent",
                new Dictionary<string, object?>
                {
                    ["task"] = "describe",
                    ["image_paths"] = new[] { "../../../etc/passwd" }
                });

            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("id", out _));
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
            Assert.Empty(manager.GetStatus());
            Assert.Equal(0, subClient.CallCount);
        }

        [Fact]
        public async Task ImagePaths_Over_Limit_Returns_Error_No_SubAgent_Tracked()
        {
            // Use the FileProbe test seam to simulate files whose total exceeds 20 MiB
            // without writing that much to disk. Two "files" each just under 20 MiB
            // but together over the limit. The loader checks cumulative bytes.
            try
            {
                var probeData = new byte[11 * 1024 * 1024]; // 11 MiB each, 22 MiB total
                var parent = new AgentOptions
                {
                    WorkDirectory = Path.GetTempPath(),
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subClient = new OptionsCapturingClient("sub done");
                var subOptions = new SubAgentOptions { DefaultClient = subClient };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                ImageLoader.FileProbe = path =>
                {
                    return (probeData.Length, () => new MemoryStream(probeData));
                };

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { "big1.png", "big2.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.TryGetProperty("id", out _));
                Assert.True(doc.RootElement.TryGetProperty("error", out _));
                Assert.Empty(manager.GetStatus());
                Assert.Equal(0, subClient.CallCount);
            }
            finally
            {
                ImageLoader.FileProbe = null;
            }
        }

        // ========================================================================
        // Working-directory snapshot test
        // ========================================================================

        [Fact]
        public async Task ImagePaths_Resolves_Against_Construction_Time_WorkDir_Snapshot()
        {
            var (dirA, cleanupA) = TempDirWithFile("test.png", TinyPng);
            var dirB = Path.Combine(Path.GetTempPath(), "subagent-img-b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirB);
            try
            {
                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = dirA, // construction-time workdir
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions { DefaultClient = subClient };
                parent.SubAgents = subOptions;

                // Construct the manager with dirA as the workdir.
                await using var manager = CreateManager(subOptions, parent, subClient);

                // Capture the AgentOptions the manager builds for the spawned sub-agent so we
                // can prove RunSpec.WorkDirectory came from the construction-time snapshot.
                AgentOptions? capturedOptions = null;
                manager.OnSubAgentOptionsCreated = o => capturedOptions = o;

                // After construction, change the parent workdir to dirB (no PNG there).
                parent.WorkDirectory = dirB;

                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { "test.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _));
                Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                // The loader resolved against dirA (construction snapshot), so the image content is present.
                Assert.NotEmpty(subClient.ReceivedMessages);
                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal("image/png", dataContents[0].MediaType);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());

                // And RunSpec used the SAME construction-time snapshot: the sub-agent's own
                // working directory is dirA, not the mutated live value dirB.
                Assert.NotNull(capturedOptions);
                Assert.Equal(dirA, capturedOptions!.WorkDirectory);
                Assert.NotEqual(dirB, capturedOptions.WorkDirectory);
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanupA();
                try { Directory.Delete(dirB, recursive: true); } catch (IOException) { }
            }
        }

        // ========================================================================
        // Regression: image_paths omitted
        // ========================================================================

        [Fact]
        public async Task ImagePaths_Omitted_Runs_SubAgent_Without_Image_Content()
        {
            var subClient = new OptionsCapturingClient("sub done");
            var parent = new AgentOptions
            {
                WorkDirectory = Path.GetTempPath(),
                EnableBash = false,
                EnableSkills = false,
                AutoLoadWorkspaceInstructions = false,
            };
            var subOptions = new SubAgentOptions { DefaultClient = subClient };
            parent.SubAgents = subOptions;

            await using var manager = CreateManager(subOptions, parent, subClient);
            var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

            var json = await InvokeAsync(tools, "start_sub_agent",
                new Dictionary<string, object?>
                {
                    ["task"] = "no images here"
                });

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("id", out _));
            Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());

            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

            Assert.NotEmpty(subClient.ReceivedMessages);
            var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
            Assert.Empty(dataContents);
        }

        [Fact]
        public async Task ImagePaths_Null_Runs_SubAgent_Without_Image_Content()
        {
            var subClient = new OptionsCapturingClient("sub done");
            var parent = new AgentOptions
            {
                WorkDirectory = Path.GetTempPath(),
                EnableBash = false,
                EnableSkills = false,
                AutoLoadWorkspaceInstructions = false,
            };
            var subOptions = new SubAgentOptions { DefaultClient = subClient };
            parent.SubAgents = subOptions;

            await using var manager = CreateManager(subOptions, parent, subClient);
            var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

            var json = await InvokeAsync(tools, "start_sub_agent",
                new Dictionary<string, object?>
                {
                    ["task"] = "no images here",
                    ["image_paths"] = null
                });

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("id", out _));
            Assert.Equal("Running", doc.RootElement.GetProperty("status").GetString());

            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

            Assert.NotEmpty(subClient.ReceivedMessages);
            var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
            Assert.Empty(dataContents);
        }

        // ========================================================================
        // Tool description test
        // ========================================================================

        [Fact]
        public async Task StartSubAgent_Tool_Description_Mentions_ImagePaths()
        {
            var subOptions = new SubAgentOptions { DefaultClient = new OptionsCapturingClient() };
            await using var manager = CreateManager(subOptions);
            var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

            var startTool = tools.OfType<AIFunction>().Single(f => f.Name == "start_sub_agent");
            Assert.Contains("image_paths", startTool.Description);
        }
    }

    // ========================================================================
    // AdditionalImagesRoot — validation, canonicalization, snapshot, loading
    // ========================================================================

    /// <summary>
    /// Tests for <see cref="SubAgentOptions.AdditionalImagesRoot"/>: a host-designated directory
    /// sub-agent attachments may be loaded from IN ADDITION to the parent work directory.
    /// Joins the ImageLoader collection because several tests read real files through the loader.
    /// </summary>
    [Collection("ImageLoader")]
    public class AdditionalImagesRootTests
    {
        private static (string root, string primary, string additional, Action cleanup) MakeRoots()
        {
            var root = Path.Combine(Path.GetTempPath(), "subagent-addroot-" + Guid.NewGuid().ToString("N"));
            var primary = Path.Combine(root, "primary");
            var additional = Path.Combine(root, "additional");
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(additional);
            return (root, primary, additional, () =>
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            });
        }

        // ---------- validation ----------

        [Fact]
        public async Task Null_AdditionalImagesRoot_Is_Not_Configured()
        {
            var (_, primary, _, cleanup) = MakeRoots();
            try
            {
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = null };

                await using var manager = new SubAgentManager(subOptions, new OptionsCapturingClient(), parent);

                Assert.Null(manager.AdditionalImagesRoot);
            }
            finally
            {
                cleanup();
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Whitespace_AdditionalImagesRoot_Throws(string configured)
        {
            var (_, primary, _, cleanup) = MakeRoots();
            try
            {
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = configured };

                Assert.Throws<ArgumentException>(() =>
                    new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public void Relative_AdditionalImagesRoot_Throws()
        {
            var (_, primary, _, cleanup) = MakeRoots();
            try
            {
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = Path.Combine("relative", "attachments") };

                var ex = Assert.Throws<ArgumentException>(() =>
                    new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
                Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public void Nonexistent_AdditionalImagesRoot_Throws()
        {
            var (root, primary, _, cleanup) = MakeRoots();
            try
            {
                var missing = Path.Combine(root, "does-not-exist");
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = missing };

                Assert.Throws<ArgumentException>(() =>
                    new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public void File_Not_Directory_AdditionalImagesRoot_Throws()
        {
            var (root, primary, _, cleanup) = MakeRoots();
            try
            {
                var filePath = Path.Combine(root, "afile.txt");
                File.WriteAllText(filePath, "not a directory");
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = filePath };

                var ex = Assert.Throws<ArgumentException>(() =>
                    new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
                Assert.Contains("directory", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task AdditionalImagesRoot_Is_Canonicalized()
        {
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                // Non-canonical input: trailing separator plus a "./nested/.." detour.
                var nonCanonical =
                    Path.Combine(additional, "sub", "..") + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(Path.Combine(additional, "sub"));

                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = nonCanonical };

                await using var manager = new SubAgentManager(subOptions, new OptionsCapturingClient(), parent);

                Assert.Equal(Path.GetFullPath(additional), manager.AdditionalImagesRoot);
                Assert.NotEqual(nonCanonical, manager.AdditionalImagesRoot);
            }
            finally
            {
                cleanup();
            }
        }

        // ---------- fully-qualified enforcement ----------

        [Fact]
        public void Rooted_But_Not_FullyQualified_AdditionalImagesRoot_Throws()
        {
            // Path.IsPathRooted accepts drive-relative ("C:images") and root-relative ("\images")
            // forms on Windows; Path.GetFullPath would then resolve them from the process's current
            // directory/drive. Only fully-qualified paths may be accepted.
            var (_, primary, _, cleanup) = MakeRoots();
            try
            {
                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };

                var candidates = new List<string>();
                if (OperatingSystem.IsWindows())
                {
                    // Drive-relative: resolved against the current directory ON that drive.
                    var drive = Path.GetPathRoot(Directory.GetCurrentDirectory())!.Substring(0, 2); // e.g. "C:"
                    candidates.Add(drive + "images");
                    // Root-relative: resolved against the current drive.
                    candidates.Add(@"\images");
                    candidates.Add("/images-root-relative");
                }
                else
                {
                    // On Unix nothing but a leading '/' is fully qualified.
                    candidates.Add("~/attachments");
                    candidates.Add("./attachments");
                    candidates.Add("attachments");
                }

                foreach (var candidate in candidates)
                {
                    Assert.False(
                        Path.IsPathFullyQualified(candidate),
                        $"Test setup error: '{candidate}' is fully qualified on this platform.");

                    var subOptions = new SubAgentOptions { AdditionalImagesRoot = candidate };
                    var ex = Assert.Throws<ArgumentException>(() =>
                        new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
                    Assert.Contains("fully-qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public void DriveRelative_Root_Rejected_Even_When_Resolved_Directory_Exists()
        {
            // Removal proof for the IsPathFullyQualified guard: build a non-fully-qualified value
            // whose one-argument GetFullPath resolution IS an existing directory. Without the
            // guard, GetFullPath + Directory.Exists would accept it.
            // The directory is created UNDER the process's current directory so no global state
            // (the current directory itself) has to be mutated.
            var (_, primary, _, cleanup) = MakeRoots();
            var leafName = "subagent-cwdrel-" + Guid.NewGuid().ToString("N");
            var underCwd = Path.Combine(Directory.GetCurrentDirectory(), leafName);
            Directory.CreateDirectory(underCwd);
            try
            {
                string candidate;
                if (OperatingSystem.IsWindows())
                {
                    // "C:<leaf>" — drive-relative, resolves against the current dir on that drive.
                    var drive = Path.GetPathRoot(Directory.GetCurrentDirectory())!.Substring(0, 2);
                    candidate = drive + leafName;
                }
                else
                {
                    candidate = leafName;
                }

                Assert.False(Path.IsPathFullyQualified(candidate));
                // Proves the test is non-vacuous: the resolved directory really does exist.
                Assert.True(Directory.Exists(Path.GetFullPath(candidate)));

                var parent = new AgentOptions { WorkDirectory = primary, AutoLoadWorkspaceInstructions = false };
                var subOptions = new SubAgentOptions { AdditionalImagesRoot = candidate };

                var ex = Assert.Throws<ArgumentException>(() =>
                    new SubAgentManager(subOptions, new OptionsCapturingClient(), parent));
                Assert.Contains("fully-qualified", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(underCwd, recursive: true); } catch (IOException) { }
                cleanup();
            }
        }

        // ---------- snapshot through CodingAgent ----------

        [Fact]
        public async Task AdditionalImagesRoot_Survives_CodingAgent_Snapshot()
        {
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                    SubAgents = new SubAgentOptions
                    {
                        DefaultClient = new OptionsCapturingClient(),
                        AdditionalImagesRoot = additional
                    }
                };

                await using var agent = new CodingAgent(new OptionsCapturingClient(), parent);
                await agent.ExecuteAsync("hello", TestContext.Current.CancellationToken);

                var manager = agent.ActiveSubAgentManager;
                Assert.NotNull(manager);
                Assert.Equal(Path.GetFullPath(additional), manager!.AdditionalImagesRoot);
            }
            finally
            {
                cleanup();
            }
        }

        [Fact]
        public async Task Post_Construction_Mutation_Does_Not_Widen_Roots()
        {
            var (root, primary, additional, cleanup) = MakeRoots();
            try
            {
                var widened = Path.Combine(root, "widened");
                Directory.CreateDirectory(widened);
                var leakPath = Path.Combine(widened, "leak.png");
                File.WriteAllBytes(leakPath, TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = additional
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);

                // Mutate AFTER construction — must not affect the snapshot.
                subOptions.AdditionalImagesRoot = widened;

                Assert.Equal(Path.GetFullPath(additional), manager.AdditionalImagesRoot);

                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);
                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { leakPath }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.TryGetProperty("id", out _));
                Assert.Contains("escapes the work directory", doc.RootElement.GetProperty("error").GetString()!);
                Assert.Empty(manager.GetStatus());
                Assert.Equal(0, subClient.CallCount);
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        // ---------- loading through the start_sub_agent tool ----------

        [Fact]
        public async Task ImagePaths_Absolute_Under_AdditionalRoot_Loads()
        {
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                var attachmentPath = Path.Combine(additional, "attachment.png");
                File.WriteAllBytes(attachmentPath, TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = additional
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe the attachment",
                        ["image_paths"] = new[] { attachmentPath }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _), json);

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                Assert.NotEmpty(subClient.ReceivedMessages);
                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal("image/png", dataContents[0].MediaType);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_Absolute_Under_AdditionalRoot_Rejected_When_Not_Configured()
        {
            // Removal proof: identical setup minus AdditionalImagesRoot must fail.
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                var attachmentPath = Path.Combine(additional, "attachment.png");
                File.WriteAllBytes(attachmentPath, TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions { DefaultClient = subClient };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                Assert.Null(manager.AdditionalImagesRoot);

                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);
                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe the attachment",
                        ["image_paths"] = new[] { attachmentPath }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.TryGetProperty("id", out _));
                Assert.Contains("escapes the work directory", doc.RootElement.GetProperty("error").GetString()!);
                Assert.Empty(manager.GetStatus());
                Assert.Equal(0, subClient.CallCount);
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_Relative_Prefers_PrimaryRoot_Copy()
        {
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                File.WriteAllBytes(Path.Combine(primary, "shared.png"), TinyPng);
                var additionalPngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B };
                File.WriteAllBytes(Path.Combine(additional, "shared.png"), additionalPngBytes);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = additional
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { "shared.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _), json);

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());
                Assert.NotEqual(additionalPngBytes, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_Relative_Only_Under_AdditionalRoot_Loads()
        {
            var (_, primary, additional, cleanup) = MakeRoots();
            try
            {
                File.WriteAllBytes(Path.Combine(additional, "only-there.png"), TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = additional
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { "only-there.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _), json);

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        [Fact]
        public async Task ImagePaths_DotDot_Escape_Rejected_With_AdditionalRoot_Configured()
        {
            var (root, primary, additional, cleanup) = MakeRoots();
            try
            {
                var outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "leak.png"), TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = additional
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);

                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { Path.Combine("..", "outside", "leak.png") }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.TryGetProperty("id", out _));
                Assert.Contains("escapes the work directory", doc.RootElement.GetProperty("error").GetString()!);
                Assert.Empty(manager.GetStatus());
                Assert.Equal(0, subClient.CallCount);
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        // ---------- no-config path: single-root behaviour through the tool ----------

        /// <summary>
        /// When AdditionalImagesRoot is null, the start_sub_agent tool must use the ORIGINAL
        /// single-root LoadAsync (3-arg) path, not the two-root path. This test proves it
        /// through the tool: an in-root image loads, and an out-of-root image is rejected
        /// with "escapes the work directory" — exactly as before the feature was added.
        /// It is removal-proof because if the SubAgentTools null guard were removed and
        /// the null flowed into the two-root core, the relative-path FileExists probe
        /// against a null additional root would throw rather than silently load.
        /// </summary>
        [Fact]
        public async Task NoAdditionalRoot_Through_Tool_Uses_SingleRoot_Path()
        {
            var (root, primary, _, cleanup) = MakeRoots();
            try
            {
                // In-root image loads successfully.
                File.WriteAllBytes(Path.Combine(primary, "repo.png"), TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = null // explicitly not configured
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                Assert.Null(manager.AdditionalImagesRoot);

                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);
                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { "repo.png" }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.True(doc.RootElement.TryGetProperty("id", out _), json);

                await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

                Assert.NotEmpty(subClient.ReceivedMessages);
                var dataContents = UserDataContents(subClient.ReceivedMessages[0]);
                Assert.Single(dataContents);
                Assert.Equal("image/png", dataContents[0].MediaType);
                Assert.Equal(TinyPng, dataContents[0].Data.ToArray());
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        /// <summary>
        /// No-config regression through the tool: an absolute path outside the primary root
        /// must still be rejected with "escapes the work directory" when no additional root
        /// is configured — identical to the pre-feature single-root behaviour.
        /// </summary>
        [Fact]
        public async Task NoAdditionalRoot_Through_Tool_Outside_Rejected()
        {
            var (root, primary, _, cleanup) = MakeRoots();
            try
            {
                var outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(outside);
                var leakPath = Path.Combine(outside, "leak.png");
                File.WriteAllBytes(leakPath, TinyPng);

                var subClient = new OptionsCapturingClient("sub done");
                var parent = new AgentOptions
                {
                    WorkDirectory = primary,
                    EnableBash = false,
                    EnableSkills = false,
                    AutoLoadWorkspaceInstructions = false,
                };
                var subOptions = new SubAgentOptions
                {
                    DefaultClient = subClient,
                    AdditionalImagesRoot = null
                };
                parent.SubAgents = subOptions;

                await using var manager = CreateManager(subOptions, parent, subClient);
                Assert.Null(manager.AdditionalImagesRoot);

                var tools = SubAgentTools.BuildTools(manager, subOptions, CancellationToken.None);
                var json = await InvokeAsync(tools, "start_sub_agent",
                    new Dictionary<string, object?>
                    {
                        ["task"] = "describe",
                        ["image_paths"] = new[] { leakPath }
                    });

                using var doc = JsonDocument.Parse(json);
                Assert.False(doc.RootElement.TryGetProperty("id", out _));
                Assert.Contains("escapes the work directory", doc.RootElement.GetProperty("error").GetString()!);
                Assert.Empty(manager.GetStatus());
                Assert.Equal(0, subClient.CallCount);
            }
            finally
            {
                ImageLoader.FileProbe = null;
                cleanup();
            }
        }

        /// <summary>
        /// No-config removal proof at the ImageLoader level: calling the 4-arg LoadAsync with
        /// a null additional root must delegate to the original 3-arg single-root loader. We
        /// prove this by using the FileProbe seam: the 3-arg core never calls FileExists for
        /// relative-path resolution (it resolves and reads directly), while the two-root core
        /// calls FileExists. If the null guard in the 4-arg overload were removed, the null
        /// would flow into the two-root core and FileExists would be invoked — the probe count
        /// would be non-zero.
        /// </summary>
        [Fact]
        public async Task NoAdditionalRoot_FourArg_Delegates_To_SingleRoot_Core()
        {
            var (_, primary, _, cleanup) = MakeRoots();
            try
            {
                File.WriteAllBytes(Path.Combine(primary, "repo.png"), TinyPng);

                var probedPaths = new System.Collections.Generic.List<string>();
                ImageLoader.FileProbe = path =>
                {
                    probedPaths.Add(path);
                    return (TinyPng.Length, () => new MemoryStream(TinyPng));
                };

                try
                {
                    // 4-arg overload with null additional root → must use 3-arg single-root core.
                    var result = await ImageLoader.LoadAsync(
                        primary, new[] { "repo.png" }, null, TestContext.Current.CancellationToken);

                    Assert.True(result.Success, result.Error ?? "Expected success");
                    Assert.Single(result.Attachments);
                    Assert.Equal(TinyPng, result.Attachments[0].Data);

                    // The single-root core calls FileProbe exactly ONCE (to read the resolved file).
                    // The two-root core would call FileProbe for the FileExists existence check
                    // AND for the read — but more importantly, for a relative path the two-root
                    // core calls FileExists on the primary candidate, which invokes the probe.
                    // The single-root core does NOT call FileExists; it resolves and probes once.
                    // If the null guard were removed and null flowed to the two-root core,
                    // ResolveAcrossRoots would call FileExists(primary) → probe invoked once
                    // for existence, then the read probe invoked again → 2 probe calls.
                    // With the single-root core, there is exactly 1 probe call (the read).
                    Assert.Single(probedPaths);
                }
                finally
                {
                    ImageLoader.FileProbe = null;
                }
            }
            finally
            {
                cleanup();
            }
        }
    }
}
