using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Tests for the parent-side tool-authorization boundary: the
/// <see cref="AgentOptions.AuthorizeTool"/> callback plus its captured
/// <see cref="AgentOptions.ToolAuthorizationCorrelationId"/>, applied to every locally invokable
/// function SharpCoder registers in <see cref="ChatOptions.Tools"/>.
/// </summary>
public class ToolAuthorizationTests
{
    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>Chat client that returns a fixed text response and captures the ChatOptions it received.</summary>
    private sealed class OptionsCapturingClient : IChatClient
    {
        private readonly string _response;

        public OptionsCapturingClient(string response = "Done.") => _response = response;

        public ChatOptions? LastOptions { get; private set; }
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

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
    /// Chat client that replays a script of rounds and records every tool result the
    /// <see cref="FunctionInvokingChatClient"/> fed back to it — i.e. exactly what the MODEL sees.
    /// Both the non-streaming and the streaming request path are scripted, so a test can exercise
    /// either dispatch path with the same script.
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private int _index;

        public ScriptedClient(params Round[] rounds) => _rounds = rounds;

        public ChatOptions? LastOptions { get; private set; }
        public List<string> ToolResults { get; } = [];
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = Next(messages, options);
            response.Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = Next(messages, options);
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        /// <summary>Records what the model saw, then produces the next scripted round.</summary>
        private ChatResponse Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            LastOptions = options;
            var snapshot = messages.ToList();
            ReceivedMessages.Add(snapshot);

            foreach (var message in snapshot)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent result)
                        ToolResults.Add(ResultToString(result.Result));
                }
            }

            var round = _rounds[_index++];
            if (round.ToolName is not null)
            {
                return new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? string.Empty))
            {
                FinishReason = ChatFinishReason.Stop
            };
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

    // ========================================================================
    // Fixtures
    // ========================================================================

    /// <summary>A fresh, unique work directory for one test; deleted when the test ends.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpcoder-auth-" + Guid.NewGuid().ToString("N"));
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

    private static List<AIFunction> Functions(ChatOptions? options) =>
        options?.Tools?.OfType<AIFunction>().ToList() ?? [];

    private static AIFunction FunctionNamed(ChatOptions? options, string name) =>
        Functions(options).Single(f => f.Name == name);

    private static List<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.Select(t => t.Name).ToList() ?? [];

    /// <summary>Records every authorization check the host callback received.</summary>
    private sealed class AuthorizationLog
    {
        public ConcurrentQueue<ToolAuthorizationContext> Calls { get; } = new();

        public IReadOnlyList<ToolAuthorizationContext> All => Calls.ToArray();
    }

    // ========================================================================
    // Custom tool: allow and deny
    // ========================================================================

    [Fact]
    public async Task CustomTool_Allow_RunsToolAndCallbackSeesNameAndCorrelation()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create((string id) => { invocations++; return $"made:{id}"; }, "create_goal")];
        opts.ToolAuthorizationCorrelationId = "user-42";
        var log = new AuthorizationLog();
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
            Round.Call("create_goal", new() { ["id"] = "g1" }),
            Round.Say("done"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("create a goal", TestContext.Current.CancellationToken);

        Assert.Equal(1, invocations);
        var context = Assert.Single(log.All);
        Assert.Equal("create_goal", context.ToolName);
        Assert.Equal("user-42", context.CorrelationId);
        Assert.Equal("g1", context.Arguments["id"]);
        Assert.Contains("made:g1", client.ToolResults.Single());
    }

    [Fact]
    public async Task CustomTool_Deny_DoesNotRunToolAndFeedsRefusalToModel()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create((string id) => { invocations++; return $"made:{id}"; }, "create_goal")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("The current user may not create goals."));

        var client = new ScriptedClient(
            Round.Call("create_goal", new() { ["id"] = "g1" }),
            Round.Say("understood"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("create a goal", TestContext.Current.CancellationToken);

        // The denial happened BEFORE the side effect: the tool body never ran.
        Assert.Equal(0, invocations);

        var result = client.ToolResults.Single();
        Assert.Contains("create_goal", result);
        Assert.Contains("The current user may not create goals.", result);
        Assert.DoesNotContain("made:g1", result);
    }

    [Fact]
    public async Task Callback_ReturnsNull_FailsClosedWithFixedRefusal()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult<ToolAuthorizationDecision>(null!);

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        Assert.Contains("was not executed", client.ToolResults.Single());
    }

    [Fact]
    public async Task Denial_WithBlankReason_UsesFixedRefusalWithoutHostText()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("   "));

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        var result = client.ToolResults.Single();
        Assert.Contains("was not executed", result);
        Assert.DoesNotContain("Reason:", result);
    }

    // ========================================================================
    // Built-in tools: bash and file ops are denied before the side effect
    // ========================================================================

    [Fact]
    public async Task BashTool_Deny_NoProcessRuns()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "bash-marker.txt");

        var opts = OptionsFor(dir.Path);
        opts.EnableBash = true;
        var log = new AuthorizationLog();
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Deny("Shell access is not permitted."));
        };

        var command = $"echo created > \"{marker}\"";
        var client = new ScriptedClient(
            Round.Call("execute_bash_command", new() { ["command"] = command }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("run it", TestContext.Current.CancellationToken);

        // The authorizer saw the concrete command, and the process never started.
        var context = Assert.Single(log.All);
        Assert.Equal("execute_bash_command", context.ToolName);
        Assert.Equal(command, context.Arguments["command"]);
        Assert.False(File.Exists(marker), "the denied bash command must not have run");
        Assert.Contains("Shell access is not permitted.", client.ToolResults.Single());
    }

    [Fact]
    public async Task BashTool_Allow_RunsTheSameCommand()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "bash-marker.txt");

        var opts = OptionsFor(dir.Path);
        opts.EnableBash = true;
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var command = $"echo created > \"{marker}\"";
        var client = new ScriptedClient(
            Round.Call("execute_bash_command", new() { ["command"] = command }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("run it", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(marker), "the allowed bash command must have run");
    }

    [Fact]
    public async Task WriteFileTool_Deny_PathAwareDecision_LeavesNoFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "secret.txt");

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        opts.EnableFileWrites = true;
        var log = new AuthorizationLog();
        // Path-aware authorization: deny only writes to this file.
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            var allowed = !(context.ToolName == "write_file"
                            && context.Arguments.TryGetValue("filePath", out var path)
                            && path is string s
                            && s.EndsWith("secret.txt", StringComparison.Ordinal));
            return Task.FromResult(allowed
                ? ToolAuthorizationDecision.Allow
                : ToolAuthorizationDecision.Deny("Writes to secret.txt are blocked."));
        };

        var client = new ScriptedClient(
            Round.Call("write_file", new() { ["filePath"] = target, ["content"] = "top secret" }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("write it", TestContext.Current.CancellationToken);

        var context = Assert.Single(log.All);
        Assert.Equal("write_file", context.ToolName);
        Assert.Equal(target, context.Arguments["filePath"]);
        Assert.False(File.Exists(target), "the denied write must not have created the file");
        Assert.Contains("Writes to secret.txt are blocked.", client.ToolResults.Single());
    }

    [Fact]
    public async Task WriteFileTool_Allow_CreatesTheFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "allowed.txt");

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        opts.EnableFileWrites = true;
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(
            Round.Call("write_file", new() { ["filePath"] = target, ["content"] = "hello" }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("write it", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(target));
        Assert.Equal("hello", File.ReadAllText(target));
    }

    [Fact]
    public async Task ReadFileTool_Deny_DoesNotReadTheFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "data.txt");
        await File.WriteAllTextAsync(target, "contents", TestContext.Current.CancellationToken);

        var opts = OptionsFor(dir.Path);
        opts.EnableFileOps = true;
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("No reads."));

        var client = new ScriptedClient(
            Round.Call("read_file", new() { ["filePath"] = target }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("read it", TestContext.Current.CancellationToken);

        var result = client.ToolResults.Single();
        Assert.Contains("No reads.", result);
        Assert.DoesNotContain("contents", result);
    }

    // ========================================================================
    // Every registered locally invokable function is guarded
    // ========================================================================

    [Fact]
    public async Task BuildChatOptions_GuardsEveryLocallyInvokableTool()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.EnableBash = true;
        opts.EnableFileOps = true;
        opts.EnableFileWrites = true;
        opts.EnableSkills = true;
        opts.CustomTools = [AIFunctionFactory.Create(() => "custom", "my_custom_tool")];
        opts.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("sub") };
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var expected = new[]
        {
            "my_custom_tool", "execute_bash_command", "read_file", "glob", "grep",
            "write_file", "edit_file", "load_skill", "list_skills",
            "start_sub_agent", "await_sub_agents", "get_sub_agent_status", "list_sub_agent_models"
        };

        var names = ToolNames(client.LastOptions);
        foreach (var name in expected)
        {
            Assert.Contains(name, names);
            Assert.True(ToolAuthorizationGuard.IsGuarded(FunctionNamed(client.LastOptions, name)),
                $"tool '{name}' must be behind the authorization boundary");
        }

        // The guard wraps the function SharpCoder would have registered, unchanged.
        var guarded = FunctionNamed(client.LastOptions, "my_custom_tool");
        var inner = ToolAuthorizationGuard.Unwrap(guarded);
        Assert.NotNull(inner);
        Assert.Equal("my_custom_tool", inner!.Name);
    }

    [Fact]
    public async Task SubAgentTools_AreGuarded_AndDeniedStartSpawnsNoChild()
    {
        var subClient = new OptionsCapturingClient("sub done");
        var opts = OptionsFor(Path.GetTempPath());
        opts.SubAgents = new SubAgentOptions { DefaultClient = subClient };
        var log = new AuthorizationLog();
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(context.ToolName == "start_sub_agent"
                ? ToolAuthorizationDecision.Deny("This user may not start sub-agents.")
                : ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(
            Round.Call("start_sub_agent", new() { ["task"] = "do research" }),
            Round.Say("ok"));

        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        Assert.Equal("start_sub_agent", Assert.Single(log.All).ToolName);
        Assert.Empty(agent.ActiveSubAgentManager!.GetStatus());
        Assert.Equal(0, subClient.CallCount);
        Assert.Contains("This user may not start sub-agents.", client.ToolResults.Single());
    }

    // ========================================================================
    // Direct invocation of a guarded function from ChatOptions
    // ========================================================================

    [Fact]
    public async Task GuardedFunction_InvokedDirectlyFromChatOptions_StillAuthorizes()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create((string id) => { invocations++; return $"made:{id}"; }, "create_goal")];
        opts.ToolAuthorizationCorrelationId = "corr-direct";
        var log = new AuthorizationLog();
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "create_goal");
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "direct" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, invocations);
        Assert.Equal("made:direct", ResultToString(result));
        var context = Assert.Single(log.All);
        Assert.Equal("corr-direct", context.CorrelationId);
    }

    // ========================================================================
    // Callback faults: fail closed, sanitized on BOTH dispatch paths
    // ========================================================================

    [Fact]
    public async Task CallbackThrow_OnFunctionInvokingPath_FailsClosedWithSanitizedRefusal()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        opts.AuthorizeTool = (_, _) => throw new InvalidOperationException("SECRET-HOST-DETAIL-42");

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        var result = client.ToolResults.Single();
        Assert.Contains("could not be authorized", result);
        Assert.DoesNotContain("SECRET-HOST-DETAIL-42", result);
        Assert.DoesNotContain(nameof(InvalidOperationException), result);
        Assert.DoesNotContain("System.", result);
    }

    [Fact]
    public async Task CallbackThrow_OnManualStreamingPath_FailsClosedWithSanitizedRefusal()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.ShowToolCallsInStream = true;
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        opts.AuthorizeTool = (_, _) => throw new InvalidOperationException("SECRET-HOST-DETAIL-43");

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "go", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
        }

        Assert.Equal(0, invocations);
        var streamed = text.ToString();
        Assert.Contains("could not be authorized", streamed);
        Assert.DoesNotContain("SECRET-HOST-DETAIL-43", streamed);
        Assert.DoesNotContain(nameof(InvalidOperationException), streamed);
    }

    [Fact]
    public async Task ThrowingLogger_DoesNotChangeTheDenialOutcome()
    {
        var invocations = 0;
        var tool = ToolAuthorizationGuard.Wrap(
            AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it"),
            (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("Blocked by policy.")),
            correlationId: "corr",
            logger: new ThrowingLogger());

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        // The diagnostic write is best effort: a throwing logger cannot turn a refusal into a crash
        // or into an execution.
        Assert.Equal(0, invocations);
        Assert.Contains("Blocked by policy.", ResultToString(result));
    }

    [Fact]
    public async Task ThrowingLogger_DoesNotChangeTheFaultOutcome()
    {
        var invocations = 0;
        var tool = ToolAuthorizationGuard.Wrap(
            AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it"),
            (_, _) => throw new InvalidOperationException("SECRET-HOST-DETAIL-46"),
            correlationId: "corr",
            logger: new ThrowingLogger());

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        var text = ResultToString(result);
        Assert.Contains("could not be authorized", text);
        Assert.DoesNotContain("SECRET-HOST-DETAIL-46", text);
    }

    [Fact]
    public async Task ThrowingLogger_DoesNotChangeTheAllowOutcome()
    {
        var tool = ToolAuthorizationGuard.Wrap(
            AIFunctionFactory.Create((string id) => $"made:{id}", "do_it"),
            (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow),
            correlationId: "corr",
            logger: new ThrowingLogger());

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "x" }),
            TestContext.Current.CancellationToken);

        Assert.Equal("made:x", ResultToString(result));
    }

    /// <summary>A logger whose every write throws, to prove diagnostics can never change an outcome.</summary>
    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            throw new InvalidOperationException("scope");

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("log write failed");
    }

    [Fact]
    public async Task CallbackReturningFaultedTask_FailsClosedWithSanitizedRefusal()
    {
        var invocations = 0;
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { invocations++; return "ran"; }, "do_it")];
        // A FAULTED TASK rather than a synchronous throw: the guard must treat both identically.
        opts.AuthorizeTool = (_, _) =>
            Task.FromException<ToolAuthorizationDecision>(new InvalidOperationException("SECRET-ASYNC-DETAIL-7"));

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(0, invocations);
        var result = client.ToolResults.Single();
        Assert.Contains("could not be authorized", result);
        Assert.DoesNotContain("SECRET-ASYNC-DETAIL-7", result);
        Assert.DoesNotContain(nameof(InvalidOperationException), result);
    }

    [Fact]
    public async Task CallbackThrow_DoesNotFailTheRun()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => throw new InvalidOperationException("SECRET-HOST-DETAIL-44");

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("all finished"));
        var agent = new CodingAgent(client, opts);
        var result = await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // A fail-closed refusal is a refusal, not a run failure: the model gets the fixed refusal and
        // the run completes normally.
        Assert.Equal("Success", result.Status);
        Assert.Contains("all finished", result.Message);
        Assert.DoesNotContain("SECRET-HOST-DETAIL-44", result.Message);
    }

    [Fact]
    public async Task CallbackThrow_OnManualStreamingPath_DoesNotFailTheRun()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.ShowToolCallsInStream = true;
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => throw new InvalidOperationException("SECRET-HOST-DETAIL-45");

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("all finished"));
        var agent = new CodingAgent(client, opts);

        AgentResult? completed = null;
        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "go", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
            if (update.Kind == StreamingUpdateKind.Completed) completed = update.Result;
        }

        // The manual path used to surface a tool exception as "Error: <message>"; the authorization
        // fault is contained exactly like a deny, so the fault's text never reaches the stream.
        Assert.DoesNotContain("SECRET-HOST-DETAIL-45", text.ToString());
        Assert.Contains("could not be authorized", text.ToString());
        Assert.Equal("Success", completed?.Status);
    }

    [Fact]
    public async Task CancelledTokenBeforeInvocation_PropagatesCancellationNotRefusal()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "do_it");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancellation keeps its identity: a cancelled call is never turned into a refusal string.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), cts.Token).AsTask());
    }

    [Fact]
    public async Task CallbackThrowingOperationCanceled_PropagatesCancellationNotRefusal()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (_, _) => throw new OperationCanceledException("cancelled by host");

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "do_it");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Callback_ReceivesTheCallersCancellationToken()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create((string id) => id, "echo_id")];
        CancellationToken observed = default;
        opts.AuthorizeTool = (_, ct) =>
        {
            observed = ct;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "echo_id");
        using var cts = new CancellationTokenSource();
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "x" }), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task AsyncCallback_CompletesBeforeTheToolBodyRuns()
    {
        // The callback parks on a gate the test releases, so the tool body CANNOT have run when the
        // execution is still parked: that is the ordering proof, with no reliance on elapsed time.
        // Every wait is bounded, so a boundary-bypassing regression fails instead of hanging.
        var order = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { order.Enqueue("tool"); return "ran"; }, "do_it")];
        opts.AuthorizeTool = async (_, _) =>
        {
            order.Enqueue("authorize-start");
            callbackEntered.TrySetResult(true);
            await gate.Task;
            order.Enqueue("authorize-end");
            return ToolAuthorizationDecision.Allow;
        };

        var client = new ScriptedClient(Round.Call("do_it", new()), Round.Say("ok"));
        var agent = new CodingAgent(client, opts);

        var execution = agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        try
        {
            var entered = await Task.WhenAny(callbackEntered.Task, BoundedWait());
            Assert.Same(callbackEntered.Task, entered);
            Assert.False(execution.IsCompleted);
            Assert.DoesNotContain("tool", order);
        }
        finally
        {
            gate.TrySetResult(true);
        }

        await execution;
        Assert.Equal(["authorize-start", "authorize-end", "tool"], order.ToArray());
    }

    [Fact]
    public async Task CancelledInsideAsyncCallback_ToolNeverRuns()
    {
        var toolRan = 0;
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The callback parks on this gate until the test has cancelled the token, so the
        // cancellation is guaranteed to be observed INSIDE the callback: without the gate the
        // callback's continuation could resume before the test cancelled, and the test would
        // race the very ordering it claims to prove.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => { Interlocked.Increment(ref toolRan); return "ran"; }, "do_it")];
        using var cts = new CancellationTokenSource();
        opts.AuthorizeTool = async (_, ct) =>
        {
            callbackEntered.TrySetResult(true);
            await release.Task;
            ct.ThrowIfCancellationRequested();
            return ToolAuthorizationDecision.Allow;
        };

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        var tool = FunctionNamed(client.LastOptions, "do_it");

        var invocation = tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), cts.Token).AsTask();

        try
        {
            var entered = await Task.WhenAny(callbackEntered.Task, BoundedWait());
            Assert.Same(callbackEntered.Task, entered);

            // Cancel FIRST, then let the callback continue: the callback now observes the
            // cancellation and must abort before the tool body runs.
            cts.Cancel();
            release.TrySetResult(true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.Equal(0, toolRan);
        }
        finally
        {
            // Never leave the parked callback behind, whatever happened above.
            release.TrySetResult(true);
        }
    }

    /// <summary>
    /// A bounded wait used ONLY to keep a failing regression from hanging the test run: it is never
    /// used to establish production ordering, which every test proves with a rendezvous instead.
    /// </summary>
    private static Task BoundedWait() => Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    // ========================================================================
    // Non-AIFunction custom tools fail closed only when authorization is enabled
    // ========================================================================

    private static AITool DeclarationTool()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        return AIFunctionFactory.CreateDeclaration("provider_tool", "provider-executed", schema, null);
    }

    [Fact]
    public async Task NonAIFunctionCustomTool_WhenAuthorizationEnabled_ThrowsAtBuildChatOptions()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [DeclarationTool()];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var agent = new CodingAgent(new OptionsCapturingClient(), opts);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ExecuteAsync("go", TestContext.Current.CancellationToken));

        Assert.Contains("provider_tool", ex.Message);
        Assert.Contains(nameof(AgentOptions.AuthorizeTool), ex.Message);
    }

    [Fact]
    public async Task NonAIFunctionCustomTool_WithoutAuthorization_KeepsCurrentShape()
    {
        var opts = OptionsFor(Path.GetTempPath());
        var declaration = DeclarationTool();
        opts.CustomTools = [declaration];

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // No callback: the declaration is registered as-is, exactly as before.
        var registered = Assert.Single(client.LastOptions!.Tools!);
        Assert.Same(declaration, registered);
    }

    // ========================================================================
    // No-callback compatibility
    // ========================================================================

    [Fact]
    public async Task NoCallback_RegistersToolsUnwrappedAndCustomToolsByIdentity()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.EnableBash = true;
        opts.EnableFileOps = true;
        opts.EnableFileWrites = true;
        opts.EnableSkills = true;
        var custom = AIFunctionFactory.Create(() => "custom", "my_custom_tool");
        opts.CustomTools = [custom];

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The custom tool is the very instance the host supplied — no wrapper in the chain.
        var registeredCustom = FunctionNamed(client.LastOptions, "my_custom_tool");
        Assert.Same(custom, registeredCustom);

        foreach (var tool in client.LastOptions!.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool), $"tool '{tool.Name}' must not be wrapped without a callback");

        var names = ToolNames(client.LastOptions);
        foreach (var expected in new[]
                 {
                     "my_custom_tool", "execute_bash_command", "read_file", "glob", "grep",
                     "write_file", "edit_file", "load_skill", "list_skills"
                 })
            Assert.Contains(expected, names);
    }

    [Fact]
    public async Task AuthorizationEnabled_PreservesNamesSchemasAndDescriptions()
    {
        async Task<ChatOptions> BuildAsync(bool authorize)
        {
            var opts = OptionsFor(Path.GetTempPath());
            opts.EnableBash = true;
            opts.EnableFileOps = true;
            opts.EnableFileWrites = true;
            opts.EnableSkills = true;
            if (authorize)
                opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

            var client = new OptionsCapturingClient();
            var agent = new CodingAgent(client, opts);
            await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
            return client.LastOptions!;
        }

        var baseline = await BuildAsync(authorize: false);
        var guarded = await BuildAsync(authorize: true);

        var baselineTools = Functions(baseline);
        var guardedTools = Functions(guarded);

        Assert.Equal(baselineTools.Select(t => t.Name), guardedTools.Select(t => t.Name));
        for (var i = 0; i < baselineTools.Count; i++)
        {
            Assert.Equal(baselineTools[i].JsonSchema.ToString(), guardedTools[i].JsonSchema.ToString());
            Assert.Equal(baselineTools[i].Description, guardedTools[i].Description);
            Assert.Equal(baselineTools[i].ReturnJsonSchema?.ToString(), guardedTools[i].ReturnJsonSchema?.ToString());
        }
    }

    [Fact]
    public async Task AuthorizationEnabled_ReservedCustomToolNameStillThrows()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "x", "start_sub_agent", "collides")];
        opts.SubAgents = new SubAgentOptions { DefaultClient = new OptionsCapturingClient("sub") };
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var ex = Assert.Throws<ArgumentException>(() => new CodingAgent(new OptionsCapturingClient(), opts));
        Assert.Contains("start_sub_agent", ex.Message);
        Assert.Contains("reserved name", ex.Message);
    }

    /// <summary>
    /// A custom <see cref="AIFunction"/> that exposes an auxiliary service through
    /// <see cref="AITool.GetService(Type, object?)"/>, to prove the authorization wrapper forwards
    /// every non-self service request to the function it guards.
    /// </summary>
    private sealed class ServiceExposingFunction : AIFunction
    {
        /// <summary>The marker service this function exposes.</summary>
        internal sealed class Marker
        {
            internal static readonly Marker Instance = new();
        }

        public override string Name => "svc_tool";

        public override string Description => "exposes an auxiliary service";

        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is null && serviceType == typeof(Marker)) return Marker.Instance;
            return base.GetService(serviceType, serviceKey);
        }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new("svc-ran");
    }

    [Fact]
    public async Task GuardedTool_ForwardsAuxiliaryServicesButAnswersSelfQueriesWithTheGuard()
    {
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [new ServiceExposingFunction()];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var guarded = FunctionNamed(client.LastOptions, "svc_tool");
        Assert.True(ToolAuthorizationGuard.IsGuarded(guarded));

        // Self queries resolve to the guard, so the unwrapped function is not reachable that way...
        Assert.Same(guarded, guarded.GetService(typeof(AIFunction)));
        // ...but an auxiliary service the guarded function exposes is still reachable.
        Assert.Same(ServiceExposingFunction.Marker.Instance, guarded.GetService(typeof(ServiceExposingFunction.Marker)));
        // A keyed request is never answered as "this tool".
        Assert.Null(guarded.GetService(typeof(AIFunction), "some-key"));
    }

    [Fact]
    public async Task GuardedTool_PreservesMetadataAndHidesTheInnerFunctionFromGetService()
    {
        var custom = AIFunctionFactory.Create((string id) => $"made:{id}", "create_goal", "Creates a goal.");
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [custom];
        opts.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var guarded = FunctionNamed(client.LastOptions, "create_goal");
        Assert.NotSame(custom, guarded);
        Assert.Equal(custom.Name, guarded.Name);
        Assert.Equal(custom.Description, guarded.Description);
        Assert.Equal(custom.JsonSchema.ToString(), guarded.JsonSchema.ToString());
        Assert.Equal(custom.ReturnJsonSchema?.ToString(), guarded.ReturnJsonSchema?.ToString());
        Assert.Equal(custom.UnderlyingMethod, guarded.UnderlyingMethod);
        Assert.Same(custom.JsonSerializerOptions, guarded.JsonSerializerOptions);

        // The service channel answers with the guard, never the unwrapped function.
        Assert.Same(guarded, guarded.GetService(typeof(AIFunction)));
        Assert.Same(guarded, guarded.GetService(typeof(AITool)));
    }

    // ========================================================================
    // Lifetime immutability of the captured pair
    // ========================================================================

    [Fact]
    public async Task ClearingCallbackAfterConstruction_DoesNotChangeExistingAgent()
    {
        var log = new AuthorizationLog();
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create(() => "ran", "do_it")];
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);

        // Disable authorization AFTER construction: only NEW agents are affected.
        opts.AuthorizeTool = null;

        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        var tool = FunctionNamed(client.LastOptions, "do_it");
        Assert.True(ToolAuthorizationGuard.IsGuarded(tool));
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken);
        Assert.Single(log.All);

        // A new agent built from the same options captures no callback.
        var newClient = new OptionsCapturingClient();
        var newAgent = new CodingAgent(newClient, opts);
        await newAgent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        var newTool = FunctionNamed(newClient.LastOptions, "do_it");
        Assert.False(ToolAuthorizationGuard.IsGuarded(newTool));
        await newTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken);
        Assert.Single(log.All);
    }

    [Fact]
    public async Task ReplacingCallbackAfterConstruction_KeepsTheCapturedDelegate()
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

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);

        opts.AuthorizeTool = (_, _) =>
        {
            second++;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        var tool = FunctionNamed(client.LastOptions, "do_it");
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task ChangingCorrelationAfterConstruction_KeepsTheCapturedCorrelation()
    {
        var log = new AuthorizationLog();
        var opts = OptionsFor(Path.GetTempPath());
        opts.CustomTools = [AIFunctionFactory.Create((string id) => id, "echo_id")];
        opts.ToolAuthorizationCorrelationId = "user-A";
        opts.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, opts);

        // Attempt to re-correlate the already-constructed agent.
        opts.ToolAuthorizationCorrelationId = "user-B";

        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        var tool = FunctionNamed(client.LastOptions, "echo_id");
        await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "x" }),
            TestContext.Current.CancellationToken);

        Assert.Equal("user-A", Assert.Single(log.All).CorrelationId);

        // Only a NEW agent sees the new correlation.
        var newClient = new OptionsCapturingClient();
        var newAgent = new CodingAgent(newClient, opts);
        await newAgent.ExecuteAsync("go", TestContext.Current.CancellationToken);
        var newTool = FunctionNamed(newClient.LastOptions, "echo_id");
        await newTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "x" }),
            TestContext.Current.CancellationToken);
        Assert.Equal("user-B", log.All[^1].CorrelationId);
    }

    // ========================================================================
    // Capture hand-off seam (additive; used by the sub-agent runtime)
    // ========================================================================

    [Fact]
    public void Capture_IsFrozenAtConstructTime_AndIsNullWithoutACallback()
    {
        var opts = OptionsFor(Path.GetTempPath());
        Assert.Null(ToolAuthorizationCapture.Capture(opts));

        var first = 0;
        Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>> callback =
            (_, _) => { first++; return Task.FromResult(ToolAuthorizationDecision.Allow); };
        opts.AuthorizeTool = callback;
        opts.ToolAuthorizationCorrelationId = "corr-1";

        var capture = ToolAuthorizationCapture.Capture(opts)!;
        Assert.Same(callback, capture.AuthorizeTool);
        Assert.Equal("corr-1", capture.CorrelationId);

        // Replacing the options afterwards cannot reach into the capture.
        opts.AuthorizeTool = null;
        opts.ToolAuthorizationCorrelationId = "corr-2";
        Assert.Same(callback, capture.AuthorizeTool);
        Assert.Equal("corr-1", capture.CorrelationId);
        Assert.Equal(0, first);
    }

    // ========================================================================
    // Context argument snapshot
    // ========================================================================
    [Fact]
    public void Context_Arguments_AreAReadOnlyDefensiveCopy()
    {
        var source = new Dictionary<string, object?> { ["filePath"] = "a.txt", ["count"] = 2 };
        var snapshot = ToolAuthorizationGuard.SnapshotArguments(source);

        Assert.Equal("a.txt", snapshot["filePath"]);
        Assert.Equal(2, snapshot["count"]);

        // Mutating the model's dictionary afterwards cannot change the snapshot the host inspected.
        source["filePath"] = "b.txt";
        Assert.Equal("a.txt", snapshot["filePath"]);

        // The host cannot mutate the call's arguments through the snapshot.
        var writable = Assert.IsAssignableFrom<ICollection<KeyValuePair<string, object?>>>(snapshot);
        Assert.Throws<NotSupportedException>(() => writable.Add(new KeyValuePair<string, object?>("x", 1)));
    }

    [Fact]
    public void Context_Arguments_ConvertJsonElementsToPlainClrValues()
    {
        var json = JsonDocument.Parse("""{"filePath":"a.txt","n":7,"ok":true,"items":["x","y"]}""").RootElement;
        var snapshot = ToolAuthorizationGuard.SnapshotArguments(new Dictionary<string, object?> { ["args"] = json });

        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot["args"]);
        Assert.Equal("a.txt", nested["filePath"]);
        Assert.Equal(7L, nested["n"]);
        Assert.Equal(true, nested["ok"]);
        var items = Assert.IsAssignableFrom<object?[]>(nested["items"]);
        Assert.Equal(["x", "y"], items);
    }

    [Fact]
    public void Context_NoArguments_YieldsEmptySnapshot()
    {
        var empty = ToolAuthorizationGuard.SnapshotArguments(null);
        Assert.Empty(empty);
        Assert.Same(empty, ToolAuthorizationGuard.SnapshotArguments(new Dictionary<string, object?>()));
    }
}
