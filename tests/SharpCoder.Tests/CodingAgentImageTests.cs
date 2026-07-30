using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

/// <summary>
/// Tests for the image-attachment overloads on <see cref="CodingAgent"/>.
/// Uses fake <see cref="IChatClient"/> implementations — no real LLM calls.
/// </summary>
public class CodingAgentImageTests
{
    // ── Fake clients ──

    /// <summary>
    /// Recording non-streaming client: captures every received message list and
    /// returns a fixed text response.
    /// </summary>
    private sealed class RecordingFixedClient : IChatClient
    {
        private readonly string _response;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public RecordingFixedClient(string response = "Done.") => _response = response;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Recording streaming client: captures every received message list and
    /// streams the supplied text chunks.
    /// </summary>
    private sealed class RecordingStreamingClient : IChatClient
    {
        private readonly string[] _chunks;
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public RecordingStreamingClient(params string[] chunks) => _chunks = chunks;

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

    /// <summary>
    /// Streaming client that throws a context-overflow exception on the first
    /// streaming call and returns normal text on the second. The non-streaming
    /// <see cref="GetResponseAsync"/> returns a summary (used by the compactor).
    /// </summary>
    private sealed class OverflowThenSuccessClient : IChatClient
    {
        private int _streamCallCount;
        public List<IList<ChatMessage>> StreamingReceivedMessages { get; } = [];
        public List<IList<ChatMessage>> NonStreamingReceivedMessages { get; } = [];
        public int StreamCallCount => _streamCallCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingReceivedMessages.Add(messages.ToList());
            // Return a non-empty summary so ForceCompactAsync succeeds.
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of prior conversation.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingReceivedMessages.Add(messages.ToList());
            var call = ++_streamCallCount;

            if (call == 1)
            {
                throw new InvalidOperationException("context window exceeds limit");
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Analysis complete.")],
            };
            await Task.Yield();
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ── Helpers ──

    private static AgentOptions MinimalOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
    };

    private static AgentOptions ToolCallOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = false,
        EnableFileOps = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
        ShowToolCallsInStream = true,
    };

    private static List<ImageAttachment> TwoImages() =>
    [
        new() { Data = [1, 2, 3, 4], MediaType = "image/png", Name = "a.png" },
        new() { Data = [5, 6, 7, 8], MediaType = "image/jpeg", Name = "b.jpg" },
    ];

    private static List<ImageAttachment> OneImage() =>
    [
        new() { Data = [10, 20, 30], MediaType = "image/png", Name = "photo.png" },
    ];

    /// <summary>
    /// Finds the last user-role message in a list and asserts it is non-null.
    /// </summary>
    private static ChatMessage LastUserMessage(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
                return messages[i];
        }
        throw new Xunit.Sdk.XunitException("No user message found in the list.");
    }

    private static void AssertUserMessageHasTextAndImages(
        ChatMessage userMsg, string expectedText, int expectedImageCount)
    {
        var text = userMsg.Contents.OfType<TextContent>().Select(c => c.Text).SingleOrDefault();
        Assert.Equal(expectedText, text);

        var dataContents = userMsg.Contents.OfType<DataContent>().ToList();
        Assert.Equal(expectedImageCount, dataContents.Count);
    }

    // ── 1. ExecuteAsync (non-streaming) with images ──

    [Fact]
    public async Task ExecuteAsync_WithImages_LlmReceivesTextAndDataContent()
    {
        var client = new RecordingFixedClient("I see two images.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("img-nonstream");
        var ct = TestContext.Current.CancellationToken;

        await agent.ExecuteAsync(session, "describe this", TwoImages(), ct);

        Assert.Single(client.ReceivedMessages);
        var received = client.ReceivedMessages[0];
        var userMsg = LastUserMessage(received);
        AssertUserMessageHasTextAndImages(userMsg, "describe this", 2);

        var dataContents = userMsg.Contents.OfType<DataContent>().ToList();
        Assert.Equal("image/png", dataContents[0].MediaType);
        Assert.Equal("image/jpeg", dataContents[1].MediaType);
    }

    // ── 2. ExecuteStreamingAsync (plain streaming, no ShowToolCallsInStream) with images ──

    [Fact]
    public async Task ExecuteStreamingAsync_WithImages_LlmReceivesTextAndDataContent()
    {
        var client = new RecordingStreamingClient("Streaming ", "response.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(null, "describe this", TwoImages(), ct)) { }

        Assert.Single(client.ReceivedMessages);
        var received = client.ReceivedMessages[0];
        var userMsg = LastUserMessage(received);
        AssertUserMessageHasTextAndImages(userMsg, "describe this", 2);

        var dataContents = userMsg.Contents.OfType<DataContent>().ToList();
        Assert.Equal("image/png", dataContents[0].MediaType);
        Assert.Equal("image/jpeg", dataContents[1].MediaType);
    }

    // ── 3. ExecuteStreamingAsync with ShowToolCallsInStream=true, with images ──

    [Fact]
    public async Task ExecuteStreamingAsync_ShowToolCalls_WithImages_LlmReceivesTextAndDataContent()
    {
        var client = new RecordingStreamingClient("Final answer.");
        await using var agent = new CodingAgent(client, ToolCallOptions());
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(null, "describe this", TwoImages(), ct)) { }

        // The first streaming call is the initial BuildMessages request.
        Assert.NotEmpty(client.ReceivedMessages);
        var initialMessages = client.ReceivedMessages[0];
        var userMsg = LastUserMessage(initialMessages);
        AssertUserMessageHasTextAndImages(userMsg, "describe this", 2);

        var dataContents = userMsg.Contents.OfType<DataContent>().ToList();
        Assert.Equal("image/png", dataContents[0].MediaType);
        Assert.Equal("image/jpeg", dataContents[1].MediaType);
    }

    // ── 4. Overflow retry preserves task text AND images (ShowToolCallsInStream=true) ──

    [Fact]
    public async Task ExecuteStreamingAsync_ShowToolCalls_OverflowRetry_PreservesTextAndImages()
    {
        var client = new OverflowThenSuccessClient();
        var opts = ToolCallOptions();
        // Low retain so ForceCompactAsync has enough to compact with a small history.
        opts.CompactionRetainRecent = 1;
        opts.EnableAutoCompaction = false; // avoid premature compaction; test the force path

        await using var agent = new CodingAgent(client, opts);
        var session = AgentSession.Create("img-overflow");
        var ct = TestContext.Current.CancellationToken;

        // Seed enough history so ForceCompactAsync can compact (needs > retainRecent+1
        // non-system messages, i.e. > 2).
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "previous question"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "previous answer"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "another question"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "another answer"));

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "analyze this image", OneImage(), ct)) { }

        // First streaming call should have thrown the overflow error; second is the retry.
        Assert.Equal(2, client.StreamCallCount);

        // The retry's messages must still contain the task text AND the image.
        var retryMessages = client.StreamingReceivedMessages[1];
        var retryUserMsg = LastUserMessage(retryMessages);
        AssertUserMessageHasTextAndImages(retryUserMsg, "analyze this image", 1);

        var dataContent = retryUserMsg.Contents.OfType<DataContent>().Single();
        Assert.Equal("image/png", dataContent.MediaType);
    }

    // ── 5. String-only calls produce identical messages (regression) ──

    [Fact]
    public async Task ExecuteAsync_NoImages_UserMessageHasOnlyTextContent()
    {
        var client = new RecordingFixedClient("Hello back.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("no-img-nonstream");
        var ct = TestContext.Current.CancellationToken;

        await agent.ExecuteAsync(session, "hello", images: null, ct);

        Assert.Single(client.ReceivedMessages);
        var userMsg = LastUserMessage(client.ReceivedMessages[0]);
        Assert.Equal("hello", userMsg.Text);
        Assert.Empty(userMsg.Contents.OfType<DataContent>());
        Assert.Single(userMsg.Contents.OfType<TextContent>());
    }

    [Fact]
    public async Task ExecuteStreamingAsync_NoImages_UserMessageHasOnlyTextContent()
    {
        var client = new RecordingStreamingClient("Hello back.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(null, "hello", images: null, ct)) { }

        Assert.Single(client.ReceivedMessages);
        var userMsg = LastUserMessage(client.ReceivedMessages[0]);
        Assert.Equal("hello", userMsg.Text);
        Assert.Empty(userMsg.Contents.OfType<DataContent>());
        Assert.Single(userMsg.Contents.OfType<TextContent>());
    }

    // ── 6. History appended on success includes image content ──

    [Fact]
    public async Task ExecuteAsync_WithImages_SessionHistoryContainsImageDataContent()
    {
        var client = new RecordingFixedClient("I see it.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("img-history-nonstream");
        var ct = TestContext.Current.CancellationToken;

        await agent.ExecuteAsync(session, "look at this", OneImage(), ct);

        // Session history: user(with image) + assistant.
        Assert.Equal(2, session.MessageHistory.Count);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        AssertUserMessageHasTextAndImages(session.MessageHistory[0], "look at this", 1);

        var dataContent = session.MessageHistory[0].Contents.OfType<DataContent>().Single();
        Assert.Equal("image/png", dataContent.MediaType);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_WithImages_SessionHistoryContainsImageDataContent()
    {
        var client = new RecordingStreamingClient("I see it.");
        await using var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("img-history-stream");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "look at this", OneImage(), ct)) { }

        Assert.Equal(2, session.MessageHistory.Count);
        Assert.Equal(ChatRole.User, session.MessageHistory[0].Role);
        AssertUserMessageHasTextAndImages(session.MessageHistory[0], "look at this", 1);

        var dataContent = session.MessageHistory[0].Contents.OfType<DataContent>().Single();
        Assert.Equal("image/png", dataContent.MediaType);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ShowToolCalls_WithImages_SessionHistoryContainsImageDataContent()
    {
        var client = new RecordingStreamingClient("I see it.");
        await using var agent = new CodingAgent(client, ToolCallOptions());
        var session = AgentSession.Create("img-history-stream-tools");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "look at this", OneImage(), ct)) { }

        Assert.NotEmpty(session.MessageHistory);
        var userMsg = session.MessageHistory.First(m => m.Role == ChatRole.User);
        AssertUserMessageHasTextAndImages(userMsg, "look at this", 1);

        var dataContent = userMsg.Contents.OfType<DataContent>().Single();
        Assert.Equal("image/png", dataContent.MediaType);
    }
}