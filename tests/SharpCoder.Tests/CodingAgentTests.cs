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
}
