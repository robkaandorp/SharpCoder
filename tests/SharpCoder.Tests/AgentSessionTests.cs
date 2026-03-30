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

    [Fact]
    public async Task SaveAndLoad_RoundTripsLastKnownContextTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AgentSession.Create("token-roundtrip");
        session.LastKnownContextTokens = 12345;
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "Hello"));

        var path = Path.Combine(Path.GetTempPath(), $"session-tokens-{Guid.NewGuid()}.json");
        try
        {
            await session.SaveAsync(path, ct);
            Assert.True(File.Exists(path));

            var loaded = await AgentSession.LoadAsync(path, ct);
            Assert.Equal("token-roundtrip", loaded.SessionId);
            Assert.Equal(12345, loaded.LastKnownContextTokens);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LastKnownContextTokens_DefaultsToZero()
    {
        var session = AgentSession.Create();
        Assert.Equal(0, session.LastKnownContextTokens);
    }

    [Fact]
    public void ClearHistory_ResetsLastKnownContextTokens()
    {
        var session = AgentSession.Create();
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "hello"));
        session.LastKnownContextTokens = 42_000;

        session.ClearHistory();

        Assert.Equal(0, session.LastKnownContextTokens);
        Assert.Empty(session.MessageHistory);
    }

    [Fact]
    public void Fork_GeneratesNewSessionId()
    {
        var original = AgentSession.Create("original-id");
        var forked = original.Fork();

        Assert.NotEqual(original.SessionId, forked.SessionId);
        Assert.NotEmpty(forked.SessionId);
    }

    [Fact]
    public void Fork_ResetsTokenCounters()
    {
        var original = AgentSession.Create();
        original.TotalToolCalls = 10;
        original.InputTokensUsed = 5000;
        original.OutputTokensUsed = 1200;

        var forked = original.Fork();

        Assert.Equal(0, forked.TotalToolCalls);
        Assert.Equal(0, forked.InputTokensUsed);
        Assert.Equal(0, forked.OutputTokensUsed);
    }

    [Fact]
    public void Fork_CopiesLastKnownContextTokens()
    {
        var original = AgentSession.Create();
        original.LastKnownContextTokens = 42_000;

        var forked = original.Fork();

        Assert.Equal(42_000, forked.LastKnownContextTokens);
    }

    [Fact]
    public void Fork_SetsTimestampsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var original = AgentSession.Create();
        // Simulate an old session by back-dating timestamps.
        original.CreatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        original.LastActivityAt = DateTimeOffset.UtcNow.AddHours(-1);

        var forked = original.Fork();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(forked.CreatedAt, before, after);
        Assert.InRange(forked.LastActivityAt, before, after);
    }

    [Fact]
    public void Fork_DeepCopiesMessageHistory()
    {
        var original = AgentSession.Create();
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "hello"));
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "world"));

        var forked = original.Fork();

        Assert.Equal(2, forked.MessageHistory.Count);
        Assert.Equal(ChatRole.User, forked.MessageHistory[0].Role);
        Assert.Equal("hello", forked.MessageHistory[0].Text);
        Assert.Equal(ChatRole.Assistant, forked.MessageHistory[1].Role);
        Assert.Equal("world", forked.MessageHistory[1].Text);
    }

    [Fact]
    public void Fork_MessageHistoryMutationDoesNotAffectOriginal()
    {
        var original = AgentSession.Create();
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "original message"));

        var forked = original.Fork();
        // Mutate the fork's history
        forked.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "new reply"));
        forked.MessageHistory[0] = new ChatMessage(ChatRole.User, "mutated message");

        // Original must be untouched
        Assert.Single(original.MessageHistory);
        Assert.Equal("original message", original.MessageHistory[0].Text);
    }

    [Fact]
    public void Fork_MessageHistoryMutationDoesNotAffectFork()
    {
        var original = AgentSession.Create();
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "original"));

        var forked = original.Fork();
        // Mutate the original's history after forking
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "extra"));
        original.MessageHistory[0] = new ChatMessage(ChatRole.User, "changed");

        // Fork must be untouched
        Assert.Single(forked.MessageHistory);
        Assert.Equal("original", forked.MessageHistory[0].Text);
    }

    [Fact]
    public void Fork_EmptyHistory_ReturnsSessionWithEmptyHistory()
    {
        var original = AgentSession.Create();

        var forked = original.Fork();

        Assert.Empty(forked.MessageHistory);
    }

    #region Fork Integration Tests

    /// <summary>
    /// Integration test: Fork creates independent copy — modifying fork doesn't affect original.
    /// Tests deep independence across multiple mutation scenarios.
    /// </summary>
    [Fact]
    public void Fork_Integration_ModifyingForkDoesNotAffectOriginal()
    {
        // Arrange: Create original with multiple message types
        var original = AgentSession.Create("original-session");
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "Hello"));
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Hi there!"));
        original.TotalToolCalls = 5;
        original.InputTokensUsed = 1000;
        original.OutputTokensUsed = 500;
        original.LastKnownContextTokens = 42_000;

        // Act: Fork and modify the fork
        var forked = original.Fork();
        
        // Mutate forked session in various ways
        forked.MessageHistory.Add(new ChatMessage(ChatRole.User, "New message"));
        forked.MessageHistory[0] = new ChatMessage(ChatRole.User, "Modified first message");
        forked.TotalToolCalls = 99;
        forked.InputTokensUsed = 9999;
        forked.OutputTokensUsed = 8888;
        forked.LastKnownContextTokens = 77_777;
        forked.CreatedAt = DateTimeOffset.UtcNow.AddDays(-10);
        forked.LastActivityAt = DateTimeOffset.UtcNow.AddHours(-5);

        // Assert: Original remains unchanged
        Assert.Equal("original-session", original.SessionId);
        Assert.Equal(2, original.MessageHistory.Count); // Still has original 2 messages
        Assert.Equal("Hello", original.MessageHistory[0].Text); // First message unchanged
        Assert.Equal("Hi there!", original.MessageHistory[1].Text); // Second message unchanged
        Assert.Equal(5, original.TotalToolCalls);
        Assert.Equal(1000, original.InputTokensUsed);
        Assert.Equal(500, original.OutputTokensUsed);
        Assert.Equal(42_000, original.LastKnownContextTokens);
        // Original timestamps remain unchanged from fork's mutations
    }

    /// <summary>
    /// Integration test: Fork creates independent copy — modifying original doesn't affect fork.
    /// Tests deep independence across multiple mutation scenarios.
    /// </summary>
    [Fact]
    public void Fork_Integration_ModifyingOriginalDoesNotAffectFork()
    {
        // Arrange: Create original with multiple message types
        var original = AgentSession.Create("original-session");
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "Original user message"));
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Original assistant reply"));
        original.TotalToolCalls = 3;
        original.InputTokensUsed = 2000;
        original.OutputTokensUsed = 800;
        original.LastKnownContextTokens = 15_000;

        // Act: Fork, then modify original
        var forked = original.Fork();
        
        // Capture fork's initial state
        var forkedSessionId = forked.SessionId;
        var forkedMessageCount = forked.MessageHistory.Count;
        var forkedFirstMessage = forked.MessageHistory[0].Text;
        var forkedCreatedAt = forked.CreatedAt;
        var forkedLastActivityAt = forked.LastActivityAt;

        // Mutate original session in various ways
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "Additional message"));
        original.MessageHistory[0] = new ChatMessage(ChatRole.User, "Changed first message");
        original.TotalToolCalls = 100;
        original.InputTokensUsed = 99999;
        original.OutputTokensUsed = 88888;
        original.LastKnownContextTokens = 111_111;
        original.CreatedAt = DateTimeOffset.UtcNow.AddYears(-1);
        original.LastActivityAt = DateTimeOffset.UtcNow.AddMonths(-6);

        // Assert: Forked session remains unchanged
        Assert.Equal(forkedSessionId, forked.SessionId);
        Assert.Equal(forkedMessageCount, forked.MessageHistory.Count);
        Assert.Equal(forkedFirstMessage, forked.MessageHistory[0].Text);
        Assert.Equal("Original assistant reply", forked.MessageHistory[1].Text);
        Assert.Equal(0, forked.TotalToolCalls); // Fork resets to 0
        Assert.Equal(0, forked.InputTokensUsed); // Fork resets to 0
        Assert.Equal(0, forked.OutputTokensUsed); // Fork resets to 0
        Assert.Equal(15_000, forked.LastKnownContextTokens); // Copied from original at fork time
        Assert.Equal(forkedCreatedAt, forked.CreatedAt);
        Assert.Equal(forkedLastActivityAt, forked.LastActivityAt);
    }

    /// <summary>
    /// Integration test: Fork with custom session ID.
    /// Verifies that the optional sessionId parameter works correctly.
    /// </summary>
    [Fact]
    public void Fork_Integration_WithCustomSessionId()
    {
        // Arrange
        var original = AgentSession.Create("original-id");
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "Test message"));

        // Act: Fork with custom session ID
        var customSessionId = "my-custom-fork-id-12345";
        var forked = original.Fork(customSessionId);

        // Assert
        Assert.Equal(customSessionId, forked.SessionId);
        Assert.NotEqual(original.SessionId, forked.SessionId);
        Assert.Single(forked.MessageHistory);
        Assert.Equal("Test message", forked.MessageHistory[0].Text);
    }

    /// <summary>
    /// Integration test: Fork with null session ID (default behavior).
    /// Verifies that null generates a new GUID.
    /// </summary>
    [Fact]
    public void Fork_Integration_WithNullSessionId_GeneratesNewGuid()
    {
        // Arrange
        var original = AgentSession.Create("original-id");

        // Act: Fork with null (default parameter)
        var forked = original.Fork(null);

        // Assert
        Assert.NotEqual(original.SessionId, forked.SessionId);
        Assert.NotEmpty(forked.SessionId);
        // Verify it's a valid GUID format (32 hex chars, no dashes)
        Assert.Matches("^[a-f0-9]{32}$", forked.SessionId);
    }

    /// <summary>
    /// Integration test: Fork of empty session works.
    /// Verifies that forking an empty session produces a valid empty fork.
    /// </summary>
    [Fact]
    public void Fork_Integration_EmptySession_ProducesValidFork()
    {
        // Arrange: Create completely empty session
        var original = AgentSession.Create();

        // Act
        var forked = original.Fork("empty-fork-id");

        // Assert: Fork is valid and empty
        Assert.Equal("empty-fork-id", forked.SessionId);
        Assert.Empty(forked.MessageHistory);
        Assert.Equal(0, forked.TotalToolCalls);
        Assert.Equal(0, forked.InputTokensUsed);
        Assert.Equal(0, forked.OutputTokensUsed);
        Assert.Equal(0, forked.LastKnownContextTokens);
        
        // Verify timestamps are set
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(forked.CreatedAt, now.AddSeconds(-5), now.AddSeconds(5));
        Assert.InRange(forked.LastActivityAt, now.AddSeconds(-5), now.AddSeconds(5));
    }

    /// <summary>
    /// Integration test: Fork preserves message content (user, assistant, tool messages).
    /// Verifies that all message types are correctly deep-copied.
    /// </summary>
    [Fact]
    public void Fork_Integration_PreservesAllMessageTypes()
    {
        // Arrange: Create session with all message types
        var original = AgentSession.Create();
        
        // User message
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "User question about code"));
        
        // Assistant message with tool call
        var toolCall = new FunctionCallContent("call-123", "read_file",
            new Dictionary<string, object?> { ["path"] = "src/Program.cs", ["limit"] = 100 });
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, [toolCall]));
        
        // Tool result message
        var toolResult = new FunctionResultContent("call-123", "File contents here...");
        original.MessageHistory.Add(new ChatMessage(ChatRole.Tool, [toolResult]));
        
        // Another user message
        original.MessageHistory.Add(new ChatMessage(ChatRole.User, "Follow-up question"));
        
        // Assistant text reply
        original.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "Here's my response"));

        // Act
        var forked = original.Fork("fork-with-all-messages");

        // Assert: All messages preserved correctly
        Assert.Equal(5, forked.MessageHistory.Count);
        
        // Verify user message 1
        Assert.Equal(ChatRole.User, forked.MessageHistory[0].Role);
        Assert.Equal("User question about code", forked.MessageHistory[0].Text);
        
        // Verify assistant message with tool call
        Assert.Equal(ChatRole.Assistant, forked.MessageHistory[1].Role);
        var forkedToolCall = forked.MessageHistory[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call-123", forkedToolCall.CallId);
        Assert.Equal("read_file", forkedToolCall.Name);
        // Arguments are preserved (may be JsonElement after deserialization)
        Assert.NotNull(forkedToolCall.Arguments);
        Assert.True(forkedToolCall.Arguments.ContainsKey("path"));
        Assert.True(forkedToolCall.Arguments.ContainsKey("limit"));
        Assert.Contains("src/Program.cs", forkedToolCall.Arguments?["path"]?.ToString() ?? string.Empty);
        
        // Verify tool result message
        Assert.Equal(ChatRole.Tool, forked.MessageHistory[2].Role);
        var forkedToolResult = forked.MessageHistory[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call-123", forkedToolResult.CallId);
        Assert.Equal("File contents here...", forkedToolResult.Result?.ToString());
        
        // Verify user message 2
        Assert.Equal(ChatRole.User, forked.MessageHistory[3].Role);
        Assert.Equal("Follow-up question", forked.MessageHistory[3].Text);
        
        // Verify assistant text reply
        Assert.Equal(ChatRole.Assistant, forked.MessageHistory[4].Role);
        Assert.Equal("Here's my response", forked.MessageHistory[4].Text);
        
        // Verify deep copy: mutations to original don't affect fork
        original.MessageHistory[0] = new ChatMessage(ChatRole.User, "Changed");
        Assert.Equal("User question about code", forked.MessageHistory[0].Text);
    }

    #endregion

    [Fact]
    public async Task SaveAndLoad_LastKnownContextTokens_DefaultsToZeroWhenNotSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = AgentSession.Create("default-tokens");
        // Don't set LastKnownContextTokens - should remain 0
        Assert.Equal(0, session.LastKnownContextTokens);

        var path = Path.Combine(Path.GetTempPath(), $"session-default-tokens-{Guid.NewGuid()}.json");
        try
        {
            await session.SaveAsync(path, ct);
            var loaded = await AgentSession.LoadAsync(path, ct);
            Assert.Equal(0, loaded.LastKnownContextTokens);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
