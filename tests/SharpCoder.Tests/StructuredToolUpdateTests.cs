using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SharpCoder.Tests;

/// <summary>
/// Tests for the additive structured tool updates emitted by the manual streaming tool loop when
/// <see cref="AgentOptions.EmitStructuredToolUpdates"/> is enabled.
/// </summary>
public class StructuredToolUpdateTests
{
    private static AgentOptions Options(bool structured) => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        ShowToolCallsInStream = true,
        EmitStructuredToolUpdates = structured,
    };

    private static async Task<List<StreamingUpdate>> CollectAsync(CodingAgent agent, string prompt, CancellationToken ct)
    {
        var updates = new List<StreamingUpdate>();
        await foreach (var update in agent.ExecuteStreamingAsync(null, prompt, ct))
            updates.Add(update);
        return updates;
    }

    [Fact]
    public async Task DisabledByDefault_NoStructuredUpdates()
    {
        var client = new ToolCallingClient("do_work", new Dictionary<string, object?> { ["value"] = "x" }, "Done.");
        var opts = Options(structured: false);
        opts.CustomTools = [AIFunctionFactory.Create((string value) => $"result:{value}", "do_work")];
        var agent = new CodingAgent(client, opts);

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.ToolCallStarted);
        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.RoundProgress);
        // The legacy markdown narration is still emitted.
        Assert.Contains(updates, u => u.Kind == StreamingUpdateKind.TextDelta && u.Text!.Contains("🔧 do_work"));
    }

    [Fact]
    public async Task Enabled_EmitsStartedFinishedAndProgress_InOrder()
    {
        var client = new ToolCallingClient("do_work", new Dictionary<string, object?> { ["value"] = "x" }, "Done.");
        var opts = Options(structured: true);
        opts.CustomTools = [AIFunctionFactory.Create((string value) => $"result:{value}", "do_work")];
        var agent = new CodingAgent(client, opts);

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        var started = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallStarted);
        var finished = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        var progress = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.RoundProgress);

        Assert.Equal("do_work", started.ToolCall!.Name);
        Assert.Equal("call_1", started.ToolCall.CallId);
        Assert.Equal("x", started.ToolCall.Arguments["value"]);
        Assert.Null(started.ToolCall.Result);
        Assert.False(started.ToolCall.IsError);

        Assert.Equal("result:x", finished.ToolCall!.Result);
        Assert.False(finished.ToolCall.IsError);
        Assert.True(finished.ToolCall.Elapsed >= TimeSpan.Zero);

        Assert.Equal(0, progress.Progress!.Round);
        Assert.Equal(1, progress.Progress.ToolCallsSoFar);
        Assert.Equal(opts.MaxSteps, progress.Progress.MaxSteps);

        int IndexOf(StreamingUpdate u) => updates.IndexOf(u);
        Assert.True(IndexOf(started) < IndexOf(finished));
        Assert.True(IndexOf(finished) < IndexOf(progress));
    }

    [Fact]
    public async Task Enabled_FullResultIsNotTruncated()
    {
        var longResult = new string('r', 500) + "\nsecond line";
        var client = new ToolCallingClient("do_work", new Dictionary<string, object?> { ["value"] = "x" }, "Done.");
        var opts = Options(structured: true);
        opts.CustomTools = [AIFunctionFactory.Create((string value) => longResult, "do_work")];
        var agent = new CodingAgent(client, opts);

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        var finished = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        Assert.Equal(longResult, finished.ToolCall!.Result);

        // The legacy text narration is still one truncated line.
        var narrations = updates.Where(u => u.Kind == StreamingUpdateKind.TextDelta && u.Text!.StartsWith("> ")).ToList();
        Assert.Single(narrations);
        Assert.True(narrations[0].Text!.Length < 130);
        Assert.DoesNotContain("second line", narrations[0].Text!);
    }

    [Fact]
    public async Task Enabled_ToolException_IsFlaggedAsError()
    {
        var client = new ToolCallingClient("do_work", new Dictionary<string, object?> { ["value"] = "x" }, "Done.");
        var opts = Options(structured: true);
        static string ThrowingTool(string value) => throw new InvalidOperationException("boom");
        Func<string, string> throwing = ThrowingTool;
        opts.CustomTools = [AIFunctionFactory.Create(throwing, "do_work")];
        var agent = new CodingAgent(client, opts);

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        var finished = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        Assert.True(finished.ToolCall!.IsError);
        Assert.Contains("boom", finished.ToolCall.Result);
    }

    [Fact]
    public async Task Enabled_UnknownTool_IsFlaggedAsError()
    {
        var client = new ToolCallingClient("missing_tool", new Dictionary<string, object?>(), "Done.");
        var agent = new CodingAgent(client, Options(structured: true));

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        var finished = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        Assert.True(finished.ToolCall!.IsError);
        Assert.Contains("Unknown tool", finished.ToolCall.Result);
    }

    [Fact]
    public async Task StructuredFlag_WithoutShowToolCallsInStream_EmitsNothing()
    {
        // FunctionInvokingChatClient owns the tool loop on this path; SharpCoder cannot observe
        // individual invocations, so structured updates must not be fabricated.
        var client = new ToolCallingClient("do_work", new Dictionary<string, object?> { ["value"] = "x" }, "Done.");
        var opts = Options(structured: true);
        opts.ShowToolCallsInStream = false;
        opts.CustomTools = [AIFunctionFactory.Create((string value) => "ok", "do_work")];
        var agent = new CodingAgent(client, opts);

        var updates = await CollectAsync(agent, "go", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.ToolCallStarted);
        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        Assert.DoesNotContain(updates, u => u.Kind == StreamingUpdateKind.RoundProgress);
    }

    /// <summary>Scripted streaming client: one tool-call round, then a final text round.</summary>
    private sealed class ToolCallingClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _toolArgs;
        private readonly string _finalText;
        private int _callCount;

        public ToolCallingClient(string toolName, Dictionary<string, object?> toolArgs, string finalText)
        {
            _toolName = toolName;
            _toolArgs = toolArgs;
            _finalText = finalText;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("Working on it.")]
                };
                await Task.Yield();
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent("call_1", _toolName, _toolArgs)]
                };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(_finalText)]
                };
                await Task.Yield();
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
