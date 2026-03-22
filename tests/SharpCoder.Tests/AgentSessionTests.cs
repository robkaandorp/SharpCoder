using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

public class AgentSessionTests
{
    [Fact]
    public void Create_WithDefaultId_GeneratesId()
    {
        var session = AgentSession.Create();
        Assert.NotNull(session.SessionId);
        Assert.NotEmpty(session.SessionId);
        Assert.Empty(session.MessageHistory);
        Assert.Equal(0, session.TotalToolCalls);
        Assert.Equal(0, session.InputTokensUsed);
        Assert.Equal(0, session.OutputTokensUsed);
    }

    [Fact]
    public void Create_WithCustomId_UsesIt()
    {
        var session = AgentSession.Create("my-session-42");
        Assert.Equal("my-session-42", session.SessionId);
    }

    [Fact]
    public void EstimatedContextTokens_EmptySession_ReturnsZero()
    {
        var session = AgentSession.Create();
        Assert.Equal(0, session.EstimatedContextTokens);
    }

    [Fact]
    public void EstimatedContextTokens_WithTextMessages_EstimatesCorrectly()
    {
        var session = AgentSession.Create();
        // 400 chars → ~100 tokens
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, new string('x', 400)));
        Assert.Equal(100, session.EstimatedContextTokens);
    }

    [Fact]
    public void EstimatedContextTokens_WithToolCalls_IncludesArguments()
    {
        var session = AgentSession.Create();
        var fc = new FunctionCallContent("call1", "read_file",
            new Dictionary<string, object?> { ["path"] = "src/foo.cs" });
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, [fc]));

        // Should estimate based on function name + argument key/value lengths
        Assert.True(session.EstimatedContextTokens > 0);
    }

    [Fact]
    public void ClearHistory_RemovesAllMessages()
    {
        var session = AgentSession.Create();
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "hello"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "world"));
        session.TotalToolCalls = 5;

        session.ClearHistory();

        Assert.Empty(session.MessageHistory);
        Assert.Equal(5, session.TotalToolCalls); // counters preserved
    }

    [Fact]
    public async Task SaveAndLoad_TextMessages_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AgentSession.Create("test-roundtrip");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "Hello"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Hi there!"));
        session.TotalToolCalls = 3;
        session.InputTokensUsed = 150;
        session.OutputTokensUsed = 50;

        var path = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid()}.json");
        try
        {
            await session.SaveAsync(path, ct);
            Assert.True(File.Exists(path));

            var loaded = await AgentSession.LoadAsync(path, ct);
            Assert.Equal("test-roundtrip", loaded.SessionId);
            Assert.Equal(2, loaded.MessageHistory.Count);
            Assert.Equal(ChatRole.User, loaded.MessageHistory[0].Role);
            Assert.Equal("Hello", loaded.MessageHistory[0].Text);
            Assert.Equal(ChatRole.Assistant, loaded.MessageHistory[1].Role);
            Assert.Equal("Hi there!", loaded.MessageHistory[1].Text);
            Assert.Equal(3, loaded.TotalToolCalls);
            Assert.Equal(150, loaded.InputTokensUsed);
            Assert.Equal(50, loaded.OutputTokensUsed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAndLoad_WithToolCalls_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AgentSession.Create("tool-roundtrip");

        // User message
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "Read file foo.cs"));

        // Assistant with tool call
        var fc = new FunctionCallContent("call-1", "read_file",
            new Dictionary<string, object?> { ["path"] = "foo.cs" });
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, [fc]));

        // Tool result
        var fr = new FunctionResultContent("call-1", "file contents here");
        session.MessageHistory.Add(new ChatMessage(ChatRole.Tool, [fr]));

        // Final assistant reply
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "I've read the file."));

        var path = Path.Combine(Path.GetTempPath(), $"session-tools-{Guid.NewGuid()}.json");
        try
        {
            await session.SaveAsync(path, ct);
            var loaded = await AgentSession.LoadAsync(path, ct);

            Assert.Equal(4, loaded.MessageHistory.Count);

            // Verify tool call roundtrip
            var assistantMsg = loaded.MessageHistory[1];
            Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
            var toolCall = assistantMsg.Contents.OfType<FunctionCallContent>().Single();
            Assert.Equal("read_file", toolCall.Name);
            Assert.Equal("call-1", toolCall.CallId);

            // Verify tool result roundtrip
            var toolMsg = loaded.MessageHistory[2];
            Assert.Equal(ChatRole.Tool, toolMsg.Role);
            var toolResult = toolMsg.Contents.OfType<FunctionResultContent>().Single();
            Assert.Equal("call-1", toolResult.CallId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), $"session-dir-{Guid.NewGuid()}");
        var path = Path.Combine(dir, "subdir", "session.json");
        try
        {
            var session = AgentSession.Create();
            await session.SaveAsync(path, ct);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LoadAsync_NonExistentFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => AgentSession.LoadAsync("/nonexistent/path.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_ThrowsJsonException()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"session-corrupt-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, "not valid json!!!", ct);
            await Assert.ThrowsAsync<JsonException>(
                () => AgentSession.LoadAsync(path, ct));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Timestamps_AreSetOnCreation()
    {
        var before = DateTimeOffset.UtcNow;
        var session = AgentSession.Create();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(session.CreatedAt, before, after);
        Assert.InRange(session.LastActivityAt, before, after);
    }
}
