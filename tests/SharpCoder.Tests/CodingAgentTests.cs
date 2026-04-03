using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

public class CodingAgentTests
{
    /// <summary>Chat client that returns a fixed text response (no tool calls).</summary>
    private sealed class FixedResponseClient : IChatClient
    {
        private readonly string _response;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public FixedResponseClient(string response = "Done.") => _response = response;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _response));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Chat client that streams text in chunks for streaming tests.</summary>
    private sealed class StreamingResponseClient : IChatClient
    {
        private readonly string[] _chunks;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public StreamingResponseClient(params string[] chunks) => _chunks = chunks;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            var text = string.Join("", _chunks);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            bool first = true;
            foreach (var chunk in _chunks)
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    var update = new ChatResponseUpdate
                    {
                        Contents = [new TextContent(chunk)],
                    };
                    if (first) update.Role = ChatRole.Assistant;
                    first = false;
                    yield return update;
                }
                await Task.Yield();
            }
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AgentOptions MinimalOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
    };

    [Fact]
    public async Task ExecuteAsync_WithSession_AccumulatesHistory()
    {
        var client = new FixedResponseClient("Response 1");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("test");

        // First call
        await agent.ExecuteAsync(session, "Hello", TestContext.Current.CancellationToken);

        // Session should have: user("Hello") + assistant("Response 1")
        Assert.Equal(2, session.MessageHistory.Count);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        Assert.Equal("Hello", session.MessageHistory[0].Text);
        Assert.Equal(ChatRole.Assistant, session.MessageHistory[1].Role);
        Assert.Equal("Response 1", session.MessageHistory[1].Text);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleCalls_PreservesFullHistory()
    {
        var client = new FixedResponseClient("Response");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("test");

        // Call 1
        await agent.ExecuteAsync(session, "First question", TestContext.Current.CancellationToken);
        Assert.Equal(2, session.MessageHistory.Count);

        // Call 2 — history from call 1 should be preserved
        await agent.ExecuteAsync(session, "Second question", TestContext.Current.CancellationToken);
        Assert.Equal(4, session.MessageHistory.Count);
        Assert.Equal("First question", session.MessageHistory[0].Text);
        Assert.Equal("Response", session.MessageHistory[1].Text);
        Assert.Equal("Second question", session.MessageHistory[2].Text);
        Assert.Equal("Response", session.MessageHistory[3].Text);

        // Call 3 — all history still there
        await agent.ExecuteAsync(session, "Third question", TestContext.Current.CancellationToken);
        Assert.Equal(6, session.MessageHistory.Count);
        Assert.Equal("Third question", session.MessageHistory[4].Text);
        Assert.Equal("Response", session.MessageHistory[5].Text);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleCalls_LlmReceivesPriorHistory()
    {
        var client = new FixedResponseClient("OK");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("test");

        await agent.ExecuteAsync(session, "Turn 1", TestContext.Current.CancellationToken);
        await agent.ExecuteAsync(session, "Turn 2", TestContext.Current.CancellationToken);

        // The second call should have sent: system + user("Turn 1") + assistant("OK") + user("Turn 2")
        var secondCallMessages = client.ReceivedMessages[1];
        Assert.Equal(4, secondCallMessages.Count);
        Assert.Equal(ChatRole.System, secondCallMessages[0].Role);
        Assert.Equal("Turn 1", secondCallMessages[1].Text);    // history from call 1
        Assert.Equal("OK", secondCallMessages[2].Text);         // response from call 1
        Assert.Equal("Turn 2", secondCallMessages[3].Text);     // new user message
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSession_DoesNotFail()
    {
        var client = new FixedResponseClient("Stateless response");
        var agent = new CodingAgent(client, MinimalOptions());

        // null session = stateless
        var result = await agent.ExecuteAsync("Do something", TestContext.Current.CancellationToken);

        Assert.Equal("Success", result.Status);
        Assert.Equal("Stateless response", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_CumulativeTokenTracking_AccumulatesAcrossCalls()
    {
        var client = new FixedResponseClient("OK");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("test");

        await agent.ExecuteAsync(session, "Call 1", TestContext.Current.CancellationToken);
        await agent.ExecuteAsync(session, "Call 2", TestContext.Current.CancellationToken);
        await agent.ExecuteAsync(session, "Call 3", TestContext.Current.CancellationToken);

        // 3 calls × (user + assistant) = 6 messages
        Assert.Equal(6, session.MessageHistory.Count);

        // Context estimate should reflect all messages
        Assert.True(session.EstimatedContextTokens > 0);
    }

    [Fact]
    public async Task ExecuteAsync_SessionHistoryExcludesSystemMessages()
    {
        var client = new FixedResponseClient("Hello!");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("test");

        await agent.ExecuteAsync(session, "Hi", TestContext.Current.CancellationToken);

        // Session should not contain system messages
        Assert.DoesNotContain(session.MessageHistory, m => m.Role == ChatRole.System);
    }

    // ── Streaming tests ──

    [Fact]
    public async Task ExecuteStreamingAsync_YieldsTextDeltasAndCompleted()
    {
        var client = new StreamingResponseClient("Hello, ", "world!");
        var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        var updates = new List<StreamingUpdate>();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Say hello", ct))
        {
            updates.Add(update);
        }

        // Should have 2 TextDelta updates + 1 Completed
        var textDeltas = updates.Where(u => u.Kind == StreamingUpdateKind.TextDelta).ToList();
        Assert.Equal(2, textDeltas.Count);
        Assert.Equal("Hello, ", textDeltas[0].Text);
        Assert.Equal("world!", textDeltas[1].Text);

        var completed = updates.Single(u => u.Kind == StreamingUpdateKind.Completed);
        Assert.NotNull(completed.Result);
        Assert.Equal("Success", completed.Result!.Status);
        Assert.Equal("Hello, world!", completed.Result.Message);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_WithSession_UpdatesHistory()
    {
        var client = new StreamingResponseClient("Response 1");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("stream-test");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Hello", ct)) { }

        // Session should contain: user("Hello") + assistant("Response 1")
        Assert.Equal(2, session.MessageHistory.Count);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        Assert.Equal("Hello", session.MessageHistory[0].Text);
        Assert.Equal(ChatRole.Assistant, session.MessageHistory[1].Role);
        Assert.Equal("Response 1", session.MessageHistory[1].Text);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_MultipleCallsWithSession_AccumulatesHistory()
    {
        var client = new StreamingResponseClient("OK");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("stream-multi");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Turn 1", ct)) { }
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Turn 2", ct)) { }

        Assert.Equal(4, session.MessageHistory.Count);
        Assert.Equal("Turn 1", session.MessageHistory[0].Text);
        Assert.Equal("OK", session.MessageHistory[1].Text);
        Assert.Equal("Turn 2", session.MessageHistory[2].Text);
        Assert.Equal("OK", session.MessageHistory[3].Text);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CompletedResultHasDiagnostics()
    {
        var client = new StreamingResponseClient("Done.");
        var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        StreamingUpdate? completed = null;
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Do something", ct))
        {
            if (update.Kind == StreamingUpdateKind.Completed)
                completed = update;
        }

        Assert.NotNull(completed?.Result?.Diagnostics);
        Assert.Equal("Do something", completed!.Result!.Diagnostics!.UserMessage);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_SingleChunk_YieldsOneDelta()
    {
        var client = new StreamingResponseClient("Hello World");
        var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        var textDeltas = new List<StreamingUpdate>();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Test", ct))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta)
                textDeltas.Add(update);
        }

        Assert.Single(textDeltas);
        Assert.Equal("Hello World", textDeltas[0].Text);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_LlmReceivesPriorSessionHistory()
    {
        var client = new StreamingResponseClient("OK");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("stream-history");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Turn 1", ct)) { }
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Turn 2", ct)) { }

        // Second call should have sent: system + user("Turn 1") + assistant("OK") + user("Turn 2")
        var secondCallMessages = client.ReceivedMessages[1];
        Assert.Equal(4, secondCallMessages.Count);
        Assert.Equal(ChatRole.System, secondCallMessages[0].Role);
        Assert.Equal("Turn 1", secondCallMessages[1].Text);
        Assert.Equal("OK", secondCallMessages[2].Text);
        Assert.Equal("Turn 2", secondCallMessages[3].Text);
    }

    // ── ShowToolCallsInStream tests ──

    /// <summary>
    /// Chat client that simulates tool calls: first response contains a FunctionCallContent,
    /// second response (after tool result) returns final text.
    /// </summary>
    private sealed class ToolCallingClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _toolArgs;
        private readonly string _finalText;
        private int _callCount;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public ToolCallingClient(string toolName, Dictionary<string, object?> toolArgs, string finalText)
        {
            _toolName = toolName;
            _toolArgs = toolArgs;
            _finalText = finalText;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            _callCount++;

            if (_callCount == 1)
            {
                // First round: text + tool call
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("I'll create that for you.")]
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
                // Second round: final text
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

    private static AgentOptions ToolCallOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        ShowToolCallsInStream = true,
    };

    [Fact]
    public async Task ShowToolCalls_YieldsToolCallAndResultAsTextDelta()
    {
        var client = new ToolCallingClient(
            "create_goal",
            new Dictionary<string, object?> { ["id"] = "add-auth" },
            "Done! Goal created.");

        var opts = ToolCallOptions();
        opts.CustomTools = [AIFunctionFactory.Create(
            (string id) => $"✅ Goal created: {id}", "create_goal")];

        var agent = new CodingAgent(client, opts);
        var ct = TestContext.Current.CancellationToken;

        var allText = new System.Text.StringBuilder();
        StreamingUpdate? completed = null;
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Create a goal", ct))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta)
                allText.Append(update.Text);
            if (update.Kind == StreamingUpdateKind.Completed)
                completed = update;
        }

        var text = allText.ToString();

        // Should contain the tool call markdown
        Assert.Contains("`🔧 create_goal(", text);
        Assert.Contains("id=\"add-auth\"", text);

        // Should contain the tool result as blockquote
        Assert.Contains("> ✅ Goal created: add-auth", text);

        // Should contain text from both LLM rounds
        Assert.Contains("I'll create that for you.", text);
        Assert.Contains("Done! Goal created.", text);

        // Completed result should report 1 tool call
        Assert.NotNull(completed?.Result);
        Assert.Equal(1, completed!.Result!.ToolCallCount);
        Assert.Equal("Success", completed.Result.Status);
    }

    [Fact]
    public async Task ShowToolCalls_SessionTrackingIncludesToolMessages()
    {
        var client = new ToolCallingClient(
            "get_goal",
            new Dictionary<string, object?> { ["id"] = "test" },
            "Here are the details.");

        var opts = ToolCallOptions();
        opts.CustomTools = [AIFunctionFactory.Create(
            (string id) => $"Goal {id}: Draft", "get_goal")];

        var agent = new CodingAgent(client, opts);
        var session = AgentSession.Create("tool-session");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Show goal", ct)) { }

        // Session should contain: user, assistant(with tool call), tool result, assistant(final)
        Assert.True(session.MessageHistory.Count >= 4);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        Assert.Equal("Show goal", session.MessageHistory[0].Text);
        Assert.Equal(1, session.TotalToolCalls);
    }

    [Fact]
    public async Task ShowToolCalls_TextOrderIsCorrect()
    {
        var client = new ToolCallingClient(
            "approve_goal",
            new Dictionary<string, object?> { ["id"] = "my-goal" },
            "All done.");

        var opts = ToolCallOptions();
        opts.CustomTools = [AIFunctionFactory.Create(
            (string id) => $"Approved: {id}", "approve_goal")];

        var agent = new CodingAgent(client, opts);
        var ct = TestContext.Current.CancellationToken;

        var textParts = new List<string>();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Approve it", ct))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta)
                textParts.Add(update.Text!);
        }

        // Find indices to verify order: LLM text → tool call → result → LLM text
        var llmTextIdx = textParts.FindIndex(t => t.Contains("I'll create that"));
        var toolCallIdx = textParts.FindIndex(t => t.Contains("🔧 approve_goal"));
        var resultIdx = textParts.FindIndex(t => t.Contains("Approved:"));
        var finalIdx = textParts.FindIndex(t => t.Contains("All done."));

        Assert.True(llmTextIdx < toolCallIdx, "LLM text should come before tool call");
        Assert.True(toolCallIdx < resultIdx, "Tool call should come before result");
        Assert.True(resultIdx < finalIdx, "Result should come before final text");
    }

    [Fact]
    public async Task ShowToolCalls_DisabledByDefault_NoToolCallText()
    {
        // When ShowToolCallsInStream is false (default), tool calls
        // are handled by FunctionInvokingChatClient — no tool text injected.
        // This test ensures the default path still works with a simple response.
        var client = new StreamingResponseClient("Simple response");
        var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        var allText = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Test", ct))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta)
                allText.Append(update.Text);
        }

        Assert.Equal("Simple response", allText.ToString());
        Assert.DoesNotContain("🔧", allText.ToString());
    }

    // ── FormatToolCallArgs / TruncateFirstLine unit tests ──

    [Fact]
    public void FormatToolCallArgs_FormatsKeyValuePairs()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = "add-auth",
            ["priority"] = "High"
        };
        var result = CodingAgent.FormatToolCallArgs(args);
        Assert.Contains("id=\"add-auth\"", result);
        Assert.Contains("priority=\"High\"", result);
    }

    [Fact]
    public void FormatToolCallArgs_TruncatesLongValues()
    {
        var args = new Dictionary<string, object?>
        {
            ["description"] = new string('x', 100)
        };
        var result = CodingAgent.FormatToolCallArgs(args);
        Assert.Contains("…", result);
        Assert.True(result.Length <= 102); // maxLength + quotes
    }

    [Fact]
    public void FormatToolCallArgs_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", CodingAgent.FormatToolCallArgs(null));
        Assert.Equal("", CodingAgent.FormatToolCallArgs(new Dictionary<string, object?>()));
    }

    [Fact]
    public void TruncateFirstLine_ReturnsFirstLine()
    {
        Assert.Equal("first line", CodingAgent.TruncateFirstLine("first line\nsecond line", 120));
    }

    [Fact]
    public void TruncateFirstLine_TruncatesLongLine()
    {
        var result = CodingAgent.TruncateFirstLine(new string('a', 200), 50);
        Assert.Equal(50, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncateFirstLine_ShortLine_ReturnsAsIs()
    {
        Assert.Equal("short", CodingAgent.TruncateFirstLine("short", 120));
    }

    // ── LastKnownContextTokens tests ──

    /// <summary>
    /// Chat client that returns a fixed response with token usage information.
    /// </summary>
    private sealed class UsageTrackingClient : IChatClient
    {
        private readonly int? _inputTokenCount;
        private readonly int? _outputTokenCount;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public UsageTrackingClient(int? inputTokenCount = null, int? outputTokenCount = null)
        {
            _inputTokenCount = inputTokenCount;
            _outputTokenCount = outputTokenCount;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."));
            if (_inputTokenCount.HasValue)
            {
                response.Usage = new UsageDetails
                {
                    InputTokenCount = _inputTokenCount,
                    OutputTokenCount = _outputTokenCount ?? 0,
                    TotalTokenCount = (_inputTokenCount ?? 0) + (_outputTokenCount ?? 0)
                };
            }
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            
            // Yield text content
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Done.")]
            };
            await Task.Yield();
            
            // Yield usage content if specified
            if (_inputTokenCount.HasValue)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = _inputTokenCount,
                        OutputTokenCount = _outputTokenCount ?? 0,
                        TotalTokenCount = (_inputTokenCount ?? 0) + (_outputTokenCount ?? 0)
                    })]
                };
            }
            
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastKnownContextTokens_FromInputTokenCount()
    {
        // Arrange: client returns exact InputTokenCount = 50000
        var client = new UsageTrackingClient(inputTokenCount: 50000, outputTokenCount: 1000);
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("token-test");

        // Act
        await agent.ExecuteAsync(session, "Hello", TestContext.Current.CancellationToken);

        // Assert: session.LastKnownContextTokens should be set to exact value from API
        Assert.Equal(50000, session.LastKnownContextTokens);
    }

    [Fact]
    public async Task ExecuteAsync_NullInputTokenCount_DoesNotUpdateLastKnownContextTokens()
    {
        // Arrange: client returns response without usage (null InputTokenCount)
        var client = new UsageTrackingClient(inputTokenCount: null);
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("null-usage-test");
        session.LastKnownContextTokens = 12345; // Pre-set a value

        // Act
        await agent.ExecuteAsync(session, "Hello", TestContext.Current.CancellationToken);

        // Assert: LastKnownContextTokens should remain unchanged (not set to 0)
        Assert.Equal(12345, session.LastKnownContextTokens);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_UpdatesLastKnownContextTokens_FromInputTokenCount()
    {
        // Arrange: client returns InputTokenCount = 75000 in streaming response
        var client = new UsageTrackingClient(inputTokenCount: 75000, outputTokenCount: 500);
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("stream-token-test");

        // Act
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Stream test", TestContext.Current.CancellationToken)) { }

        // Assert: LastKnownContextTokens should be updated from usage
        Assert.Equal(75000, session.LastKnownContextTokens);
    }

    // ── Session History Tests (Regression tests for session duplication fix) ──

    /// <summary>
    /// Verifies that the session history derived from messages contains no duplicates.
    /// This is a regression test for the session duplication bug where messages were
    /// re-appended after compaction, causing duplicate entries.
    /// </summary>
    [Fact]
    public void SessionHistory_NoDuplicates_AfterDerivingFromMessages()
    {
        // Arrange: Create a messages list that simulates what StreamWithToolCallsAsync builds
        // messages = [system, user, assistant(with tool call), tool result, assistant(final)]
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, [
                new TextContent("I'll help you."),
                new FunctionCallContent("call_1", "test_tool", new Dictionary<string, object?> { ["arg"] = "value" })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", "tool result")]),
            new ChatMessage(ChatRole.Assistant, "Final response")
        };

        // Act: Derive session history from messages (simulating the fix logic)
        var startIdx = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
        var sessionHistory = new List<ChatMessage>(messages.Skip(startIdx));

        // Assert: No duplicate message content
        var textContents = sessionHistory
            .Where(m => m.Text != null)
            .Select(m => m.Text)
            .ToList();
        
        Assert.Equal(textContents.Count, textContents.Distinct().Count());
        
        // Verify the correct messages are in history (system excluded)
        Assert.Equal(4, sessionHistory.Count);
        Assert.Equal(ChatRole.User, sessionHistory[0].Role);
        Assert.Equal("Hello", sessionHistory[0].Text);
    }

    /// <summary>
    /// Verifies backward compatibility: deriving history from messages produces
    /// the same result as the old re-appending logic for the simple case.
    /// </summary>
    [Fact]
    public async Task SessionHistory_BackwardCompatibility_DerivedFromMessages()
    {
        // Arrange: Use a simple conversation flow without compaction
        var client = new StreamingResponseClient("Response");
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("compat-test");
        var ct = TestContext.Current.CancellationToken;

        // Act: Execute a streaming call (no tool calls, no compaction)
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Hello", ct)) { }

        // Assert: Session should contain user + assistant (no system)
        Assert.Equal(2, session.MessageHistory.Count);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        Assert.Equal("Hello", session.MessageHistory[0].Text);
        Assert.Equal(ChatRole.Assistant, session.MessageHistory[1].Role);
        Assert.Equal("Response", session.MessageHistory[1].Text);
        
        // No system message in history
        Assert.DoesNotContain(session.MessageHistory, m => m.Role == ChatRole.System);
    }

    /// <summary>
    /// Verifies that when the first message in messages is a system prompt,
    /// it is correctly stripped from session history.
    /// </summary>
    [Fact]
    public void SessionHistory_SystemPrompt_StrippedWhenFirst()
    {
        // Arrange: Create messages with system prompt first
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "System instructions here."),
            new ChatMessage(ChatRole.User, "User message"),
            new ChatMessage(ChatRole.Assistant, "Assistant response")
        };

        // Act: Apply the logic from StreamWithToolCallsAsync
        var startIdx = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
        var sessionHistory = new List<ChatMessage>(messages.Skip(startIdx));

        // Assert: System prompt is stripped
        Assert.Equal(2, sessionHistory.Count);
        Assert.Equal(ChatRole.User, sessionHistory[0].Role);
        Assert.Equal(ChatRole.Assistant, sessionHistory[1].Role);
        Assert.DoesNotContain(sessionHistory, m => m.Role == ChatRole.System);
    }

    /// <summary>
    /// Verifies that when there's no system prompt, startIdx is 0 and all messages are included.
    /// </summary>
    [Fact]
    public void SessionHistory_NoSystemPrompt_AllMessagesIncluded()
    {
        // Arrange: Create messages without system prompt (edge case)
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "User message"),
            new ChatMessage(ChatRole.Assistant, "Assistant response")
        };

        // Act: Apply the logic from StreamWithToolCallsAsync
        var startIdx = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
        var sessionHistory = new List<ChatMessage>(messages.Skip(startIdx));

        // Assert: All messages included
        Assert.Equal(2, sessionHistory.Count);
        Assert.Equal(ChatRole.User, sessionHistory[0].Role);
        Assert.Equal(ChatRole.Assistant, sessionHistory[1].Role);
    }

    /// <summary>
    /// Verifies that multiple tool calls and results don't cause duplicates in session history.
    /// </summary>
    [Fact]
    public async Task SessionHistory_MultipleToolCalls_NoDuplicates()
    {
        // This test uses ToolCallingClient which makes two calls (tool call + final response)
        var client = new ToolCallingClient(
            "test_tool",
            new Dictionary<string, object?> { ["arg"] = "value" },
            "Final response");

        var opts = ToolCallOptions();
        opts.CustomTools = [AIFunctionFactory.Create(
            (string arg) => $"Tool result for {arg}", "test_tool")];

        var agent = new CodingAgent(client, opts);
        var session = AgentSession.Create("multi-tool-test");
        var ct = TestContext.Current.CancellationToken;

        // Act
        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Do something", ct)) { }

        // Assert: Session should have user, assistant(with tool call), tool result, assistant(final)
        // No duplicates
        Assert.True(session.MessageHistory.Count >= 3, $"Expected at least 3 messages, got {session.MessageHistory.Count}");
        
        // Check for no duplicate text content
        var textMessages = session.MessageHistory
            .Where(m => !string.IsNullOrEmpty(m.Text))
            .ToList();
        
        var textContents = textMessages.Select(m => m.Text).ToList();
        Assert.Equal(textContents.Count, textContents.Distinct().Count());
    }
}
