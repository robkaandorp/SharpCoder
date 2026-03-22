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
}
