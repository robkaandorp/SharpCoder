using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

public class ContextCompactorTests
{
    /// <summary>Minimal chat client mock that returns a fixed summary.</summary>
    private sealed class MockSummarizingClient : IChatClient
    {
        private readonly string _summary;
        public int CallCount { get; private set; }

        public MockSummarizingClient(string summary = "Summary of prior conversation.")
        {
            _summary = summary;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _summary));
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

    /// <summary>Client that throws when called (to verify compaction is NOT triggered).</summary>
    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not be called");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Client that fails on summarization to test error resilience.</summary>
    private sealed class FailingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("API error");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task CompactIfNeeded_BelowThreshold_DoesNotCompact()
    {
        var client = new ThrowingClient();
        var compactor = new ContextCompactor(client);
        var session = AgentSession.Create();
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "Hello"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Hi"));

        var options = new AgentOptions { MaxContextTokens = 100_000 };
        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(2, session.MessageHistory.Count);
    }

    [Fact]
    public async Task CompactIfNeeded_DisabledAutoCompaction_DoesNotCompact()
    {
        var client = new ThrowingClient();
        var compactor = new ContextCompactor(client);
        var session = CreateLargeSession(100);

        var options = new AgentOptions
        {
            MaxContextTokens = 100,
            EnableAutoCompaction = false
        };

        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task CompactIfNeeded_AboveThreshold_CompactsOldMessages()
    {
        var mockClient = new MockSummarizingClient("Compressed conversation summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = CreateLargeSession(20);

        var options = new AgentOptions
        {
            MaxContextTokens = 100, // Very low threshold to trigger compaction
            CompactionThreshold = 0.5,
            CompactionRetainRecent = 5
        };

        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(1, mockClient.CallCount);
        // Should have: 1 summary message + 5 recent = 6 total
        Assert.Equal(6, session.MessageHistory.Count);
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);
        Assert.Contains("Compressed conversation summary", session.MessageHistory[0].Text!);
    }

    [Fact]
    public async Task CompactIfNeeded_PreservesRecentMessages()
    {
        var mockClient = new MockSummarizingClient();
        var compactor = new ContextCompactor(mockClient);

        var session = AgentSession.Create();
        for (int i = 0; i < 20; i++)
        {
            // Use large enough messages to trigger compaction at low thresholds
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message-{i}: " + new string('x', 200)));
        }

        var options = new AgentOptions
        {
            MaxContextTokens = 100,
            CompactionThreshold = 0.1,
            CompactionRetainRecent = 3
        };

        await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        // Last 3 messages should be preserved verbatim
        Assert.Contains("Message-17", session.MessageHistory[^3].Text!);
        Assert.Contains("Message-18", session.MessageHistory[^2].Text!);
        Assert.Contains("Message-19", session.MessageHistory[^1].Text!);
    }

    [Fact]
    public async Task CompactIfNeeded_TooFewMessages_DoesNotCompact()
    {
        var client = new ThrowingClient();
        var compactor = new ContextCompactor(client);

        var session = AgentSession.Create();
        // Add few but large messages
        for (int i = 0; i < 3; i++)
            session.MessageHistory.Add(new ChatMessage(ChatRole.User, new string('x', 10000)));

        var options = new AgentOptions
        {
            MaxContextTokens = 100,
            CompactionThreshold = 0.01,
            CompactionRetainRecent = 5 // Retain more than we have
        };

        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);
        Assert.False(result); // Not enough messages to compact
    }

    [Fact]
    public async Task CompactIfNeeded_SummarizationFails_ReturnsFalseAndPreservesHistory()
    {
        var client = new FailingClient();
        var compactor = new ContextCompactor(client);
        var session = CreateLargeSession(20);
        var originalCount = session.MessageHistory.Count;

        var options = new AgentOptions
        {
            MaxContextTokens = 100,
            CompactionThreshold = 0.1,
            CompactionRetainRecent = 5
        };

        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(originalCount, session.MessageHistory.Count); // History unchanged
    }

    [Fact]
    public void IsContextOverflowError_MatchingMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("Request failed: model_max_prompt_tokens_exceeded");
        Assert.True(ContextCompactor.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_InnerExceptionMatches_ReturnsTrue()
    {
        var inner = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var outer = new Exception("Outer error", inner);
        Assert.True(ContextCompactor.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_NoMatch_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Some other error");
        Assert.False(ContextCompactor.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_NullException_ReturnsFalse()
    {
        Assert.False(ContextCompactor.IsContextOverflowError(null));
    }

    [Fact]
    public void IsContextOverflowError_NestedInnerException_ThreeLevelsDeep_ReturnsTrue()
    {
        // 3 levels deep: outer -> middle -> inner (where inner has the error)
        var inner = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var middle = new Exception("Middle exception", inner);
        var outer = new Exception("Outer exception", middle);
        Assert.True(ContextCompactor.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_ErrorInData_DoesNotMatch()
    {
        // Error string in ex.Data should NOT match - only message and InnerException chain
        var ex = new InvalidOperationException("Some other error");
        ex.Data["error"] = "model_max_prompt_tokens_exceeded";
        Assert.False(ContextCompactor.IsContextOverflowError(ex));
    }

    [Fact]
    public async Task ForceCompactAsync_BelowThreshold_StillCompacts()
    {
        // ForceCompactAsync should compact even if token count is below threshold
        var mockClient = new MockSummarizingClient("Forced summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        // Add enough messages to compact (more than CompactionRetainRecent + 1)
        for (int i = 0; i < 15; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}"));

        // Options with a very high threshold — normal compaction would NOT trigger
        var options = new AgentOptions
        {
            MaxContextTokens = 10_000_000,
            CompactionThreshold = 0.99,
            CompactionRetainRecent = 5,
        };

        var result = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(1, mockClient.CallCount);
        // 1 summary + 5 recent = 6
        Assert.Equal(6, session.MessageHistory.Count);
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);
        Assert.Contains("Forced summary.", session.MessageHistory[0].Text!);
    }

    [Fact]
    public async Task ForceCompactAsync_InvokesOnCompactedCallback()
    {
        var mockClient = new MockSummarizingClient("Summary text.");
        var compactor = new ContextCompactor(mockClient);
        var session = CreateLargeSession(20);

        CompactionResult? callbackResult = null;
        var options = new AgentOptions
        {
            CompactionRetainRecent = 5,
            OnCompacted = r => callbackResult = r,
        };

        var result = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.NotNull(callbackResult);
        Assert.True(callbackResult!.TokensBefore > 0);
    }

    [Fact]
    public async Task ForceCompactAsync_RespectsCompactionRetainRecent()
    {
        // Verify that the last N messages remain verbatim after ForceCompactAsync
        var mockClient = new MockSummarizingClient("Summary of old messages.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        // Add exactly 15 messages with distinct content
        for (int i = 0; i < 15; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"UniqueMessage-{i}"));

        var options = new AgentOptions
        {
            CompactionRetainRecent = 4,  // Keep last 4 messages verbatim
        };

        var result = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        // 1 summary + 4 recent = 5 total
        Assert.Equal(5, session.MessageHistory.Count);

        // First message is the summary
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);

        // Last 4 messages should be verbatim (indices 11, 12, 13, 14 from original)
        // After compaction they are at indices 1, 2, 3, 4
        Assert.Equal("UniqueMessage-11", session.MessageHistory[1].Text);
        Assert.Equal("UniqueMessage-12", session.MessageHistory[2].Text);
        Assert.Equal("UniqueMessage-13", session.MessageHistory[3].Text);
        Assert.Equal("UniqueMessage-14", session.MessageHistory[4].Text);
    }

    [Fact]
    public async Task CompactIfNeededAsync_LiveMessages_OverThreshold_CompactsAndUpdatesSession()
    {
        var mockClient = new MockSummarizingClient("Live compaction summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        // Build a live messages list with a system prompt + many large messages
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You are a helpful assistant.")
        };
        for (int i = 0; i < 20; i++)
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                i % 2 == 0 ? Microsoft.Extensions.AI.ChatRole.User : Microsoft.Extensions.AI.ChatRole.Assistant,
                $"Message {i}: " + new string('x', 200)));

        // Sync session history to non-system messages (mimicking in-loop state)
        session.MessageHistory = new List<Microsoft.Extensions.AI.ChatMessage>(messages.Skip(1));

        var options = new AgentOptions
        {
            MaxContextTokens = 100,       // Very low to trigger compaction
            CompactionThreshold = 0.1,
            CompactionRetainRecent = 5,
        };

        var result = await compactor.CompactIfNeededAsync(session, messages, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        // Live messages list: system prompt + summary + 5 recent = 7
        Assert.Equal(7, messages.Count);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, messages[0].Role);
        Assert.Contains("[CONTEXT SUMMARY", messages[1].Text!);
        Assert.Contains("Live compaction summary.", messages[1].Text!);

        // session.MessageHistory should also be updated (without system prompt)
        Assert.Equal(6, session.MessageHistory.Count);
        Assert.Contains("[CONTEXT SUMMARY", session.MessageHistory[0].Text!);
    }

    [Fact]
    public async Task CompactIfNeededAsync_LiveMessages_BelowThreshold_DoesNotCompact()
    {
        var client = new ThrowingClient();
        var compactor = new ContextCompactor(client);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "Hello"),
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "Hi"),
        };

        var options = new AgentOptions { MaxContextTokens = 100_000 };

        var result = await compactor.CompactIfNeededAsync(null, messages, options, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task CompactIfNeededAsync_LiveMessages_OverThreshold_InvokesOnCompactedCallback()
    {
        // Verify that mid-loop compaction invokes the OnCompacted callback
        var mockClient = new MockSummarizingClient("Mid-loop summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        // Build a live messages list with a system prompt + many large messages
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You are a helpful assistant.")
        };
        for (int i = 0; i < 20; i++)
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                i % 2 == 0 ? Microsoft.Extensions.AI.ChatRole.User : Microsoft.Extensions.AI.ChatRole.Assistant,
                $"Message {i}: " + new string('x', 200)));

        // Sync session history to non-system messages (mimicking in-loop state)
        session.MessageHistory = new List<Microsoft.Extensions.AI.ChatMessage>(messages.Skip(1));

        CompactionResult? callbackResult = null;
        var options = new AgentOptions
        {
            MaxContextTokens = 100,       // Very low to trigger compaction
            CompactionThreshold = 0.1,
            CompactionRetainRecent = 5,
            OnCompacted = r => callbackResult = r,
        };

        var result = await compactor.CompactIfNeededAsync(session, messages, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.NotNull(callbackResult);
        Assert.True(callbackResult!.TokensBefore > 0);
        Assert.True(callbackResult.MessagesBefore > callbackResult.MessagesAfter);
        // Verify compaction occurred: before count should be greater than after count
        Assert.Equal(20, callbackResult.MessagesBefore); // 20 non-system messages before
        Assert.Equal(6, callbackResult.MessagesAfter);   // 1 summary + 5 recent after
    }

    private static AgentSession CreateLargeSession(int messageCount)
    {
        var session = AgentSession.Create();
        for (int i = 0; i < messageCount; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}: " + new string('a', 200)));
        }
        return session;
    }

    [Fact]
    public void AdjustSplitPoint_NoToolResults_ReturnsOriginalSplitPoint()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi"),
            new(ChatRole.User, "How?"),
            new(ChatRole.Assistant, "Like this"),
        };

        Assert.Equal(2, ContextCompactor.AdjustSplitPoint(messages, 2));
    }

    [Fact]
    public void AdjustSplitPoint_ToolResultAtBoundary_MovesForward()
    {
        // Simulate: assistant made tool call (old), tool result at split point (orphaned)
        var assistantWithCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call1", "grep", new Dictionary<string, object?> { ["pattern"] = "test" })]);
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call1", "result data")]);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Find test"),
            assistantWithCall,               // index 1 (old)
            toolResult,                       // index 2 (would be start of recent — orphaned!)
            new(ChatRole.Assistant, "Found"), // index 3
            new(ChatRole.User, "Great"),      // index 4
        };

        // Original split at 2 would orphan the tool result
        var adjusted = ContextCompactor.AdjustSplitPoint(messages, 2);

        // Should move to 3, past the tool result
        Assert.Equal(3, adjusted);
    }

    [Fact]
    public void AdjustSplitPoint_MultipleToolResultsAtBoundary_MovesForwardPastAll()
    {
        var assistantWithCalls = new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "grep", new Dictionary<string, object?> { ["p"] = "a" }),
                new FunctionCallContent("c2", "read_file", new Dictionary<string, object?> { ["path"] = "f.cs" }),
            ]);
        var toolResult1 = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("c1", "r1")]);
        var toolResult2 = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("c2", "r2")]);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Search"),
            assistantWithCalls,
            toolResult1,                       // index 2 — orphaned
            toolResult2,                       // index 3 — orphaned
            new(ChatRole.Assistant, "Done"),    // index 4
            new(ChatRole.User, "Next"),
        };

        var adjusted = ContextCompactor.AdjustSplitPoint(messages, 2);
        Assert.Equal(4, adjusted);
    }

    [Fact]
    public void AdjustSplitPoint_ToolCallAndResultInRecent_NoAdjustment()
    {
        var assistantWithCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "grep", new Dictionary<string, object?> { ["p"] = "x" })]);
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("c1", "found")]);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Old msg 1"),
            new(ChatRole.Assistant, "Old msg 2"),
            assistantWithCall,                 // index 2 (start of recent — has tool call)
            toolResult,                        // index 3 (tool result paired with index 2)
            new(ChatRole.User, "Continue"),
        };

        // Split at 2 — tool call and result are both in recent, no orphan
        var adjusted = ContextCompactor.AdjustSplitPoint(messages, 2);
        Assert.Equal(2, adjusted);
    }

    [Fact]
    public void AdjustSplitPoint_AtZero_ReturnsZero()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
        };

        Assert.Equal(0, ContextCompactor.AdjustSplitPoint(messages, 0));
    }

    [Fact]
    public void AdjustSplitPoint_AtEnd_ReturnsEnd()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi"),
        };

        Assert.Equal(2, ContextCompactor.AdjustSplitPoint(messages, 2));
    }

    [Fact]
    public async Task CompactIfNeeded_OrphanedToolResults_IncludedInOldMessages()
    {
        var mockClient = new MockSummarizingClient("Summary with tools handled.");
        var compactor = new ContextCompactor(mockClient);

        var session = AgentSession.Create();

        // Build a session with tool calls at the compaction boundary
        for (int i = 0; i < 10; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Msg {i}: " + new string('x', 200)));

        // Add assistant with tool call + tool results right before the last 5 messages
        var assistantWithCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("tc1", "read_file", new Dictionary<string, object?> { ["path"] = "test.cs" })]);
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("tc1", "file contents here")]);

        session.MessageHistory.Add(assistantWithCall);  // index 10
        session.MessageHistory.Add(toolResult);         // index 11

        // 5 more recent messages
        for (int i = 12; i < 17; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Recent {i}: " + new string('y', 200)));

        // 17 messages total. retainRecent=5, so original split at 12.
        // msg[11] is a tool result → split should adjust to 12, which is not a tool result. Good.
        // But if retainRecent=6, original split at 11 — msg[11] is tool result → adjust to 12.
        var options = new AgentOptions
        {
            MaxContextTokens = 100,
            CompactionThreshold = 0.01,
            CompactionRetainRecent = 6
        };

        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        Assert.True(result);
        // No message in the remaining history should be a lone tool result
        // without a preceding assistant tool call
        for (int i = 1; i < session.MessageHistory.Count; i++)
        {
            var msg = session.MessageHistory[i];
            if (msg.Contents.OfType<FunctionResultContent>().Any())
            {
                // The preceding message must have tool calls
                var prev = session.MessageHistory[i - 1];
                Assert.True(prev.Contents.OfType<FunctionCallContent>().Any(),
                    $"Tool result at index {i} has no preceding tool call");
            }
        }
    }

    [Fact]
    public async Task CompactIfNeeded_PrefersExactLastKnownContextTokens_OverEstimate()
    {
        // Arrange: session has exact token count (50000) and heuristic is higher (70000)
        var mockClient = new MockSummarizingClient("Summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();
        
        // Add messages that would give a higher heuristic estimate
        // ~800 chars per message → ~200 tokens per message, 30 messages → ~6000 tokens estimated
        for (int i = 0; i < 30; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}: " + new string('x', 800)));
        
        // Set exact count to 50000 (lower than heuristic ~6000)
        session.LastKnownContextTokens = 50000;
        
        // Options: threshold = 50000 * 0.5 = 25000 tokens
        // With exact count (50000 >= 25000) → compaction triggered
        // With heuristic (~6000 < 25000) → NO compaction
        // This test verifies the exact count is used
        var options = new AgentOptions
        {
            MaxContextTokens = 50000,
            CompactionThreshold = 0.5, // threshold = 25000
            CompactionRetainRecent = 5,
            EnableAutoCompaction = true
        };

        // Act
        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        // Assert: compaction should be triggered because 50000 >= 25000 (exact count used)
        Assert.True(result, "Compaction should be triggered when exact count exceeds threshold");
        Assert.Equal(1, mockClient.CallCount);
    }

    [Fact]
    public async Task CompactIfNeeded_FallsBackToEstimate_WhenExactCountIsZero()
    {
        // Arrange: session has LastKnownContextTokens = 0 (no API response yet), use estimate
        var client = new ThrowingClient(); // Should not be called if below threshold
        var compactor = new ContextCompactor(client);
        var session = AgentSession.Create();
        
        // Add small messages → low estimate
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "Hello"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Hi"));
        
        // LastKnownContextTokens = 0 by default
        Assert.Equal(0, session.LastKnownContextTokens);
        
        // High threshold - compaction should NOT trigger
        var options = new AgentOptions
        {
            MaxContextTokens = 100_000,
            CompactionThreshold = 0.5,
            EnableAutoCompaction = true
        };

        // Act
        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        // Assert: compaction should NOT trigger because estimate is below threshold
        Assert.False(result);
    }

    [Fact]
    public async Task CompactIfNeeded_ResetsLastKnownContextTokens_AfterCompaction()
    {
        // Arrange
        var mockClient = new MockSummarizingClient("Summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        for (int i = 0; i < 20; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                new string('x', 400)));

        // Set a non-zero value to simulate stale token count
        session.LastKnownContextTokens = 99_000;

        var options = new AgentOptions
        {
            MaxContextTokens = 100_000,
            CompactionThreshold = 0.9, // threshold = 90000 — below LastKnownContextTokens
            CompactionRetainRecent = 5,
            EnableAutoCompaction = true
        };

        // Act
        var result = await compactor.CompactIfNeededAsync(session, options, TestContext.Current.CancellationToken);

        // Assert: compaction succeeded and LastKnownContextTokens is reset to 0
        Assert.True(result);
        Assert.Equal(0, session.LastKnownContextTokens);
    }

    [Fact]
    public async Task ForceCompactAsync_ResetsLastKnownContextTokens_AfterCompaction()
    {
        // Arrange
        var mockClient = new MockSummarizingClient("Summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        for (int i = 0; i < 15; i++)
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}"));

        session.LastKnownContextTokens = 55_000;

        var options = new AgentOptions { CompactionRetainRecent = 5 };

        // Act
        var result = await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
        Assert.Equal(0, session.LastKnownContextTokens);
    }

    [Fact]
    public async Task CompactIfNeededAsync_LiveMessages_ResetsLastKnownContextTokens_AfterCompaction()
    {
        // Arrange
        var mockClient = new MockSummarizingClient("Summary.");
        var compactor = new ContextCompactor(mockClient);
        var session = AgentSession.Create();

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "You are an agent.")
        };

        for (int i = 0; i < 20; i++)
            messages.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                new string('x', 400)));

        // Sync session history (no system prompt)
        foreach (var m in messages.Skip(1))
            session.MessageHistory.Add(m);

        session.LastKnownContextTokens = 77_000;

        var options = new AgentOptions
        {
            MaxContextTokens = 10_000,
            CompactionThreshold = 0.1, // very low so compaction triggers on estimated chars
            CompactionRetainRecent = 5,
            EnableAutoCompaction = true
        };

        // Act
        var result = await compactor.CompactIfNeededAsync(session, messages, options, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
        Assert.Equal(0, session.LastKnownContextTokens);
    }
}
