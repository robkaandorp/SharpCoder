using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace SharpCoder.Tests;

/// <summary>
/// Verifies that CodingAgent sanitizes outbound tool-call names (repairing invalid names
/// produced by models or replayed from persisted sessions) without ever mutating
/// AgentSession.MessageHistory and without changing call-id/result pairing.
/// </summary>
public class ToolCallNameSanitizationTests
{
    /// <summary>
    /// Strict oracle: 1-64 characters, every one from <c>[a-zA-Z0-9_-]</c>.
    /// <para>
    /// Checked character by character rather than with <c>^[a-zA-Z0-9_-]{1,64}$</c>, because in
    /// .NET <c>$</c> also matches immediately before a final <c>'\n'</c> — that permissive anchor
    /// would happily accept <c>"functions_bad_name\n"</c> and hide exactly the regression this
    /// oracle exists to catch.
    /// </para>
    /// </summary>
    private static bool IsStrictlyValidName(string? name)
    {
        if (name is null || name.Length == 0 || name.Length > 64) return false;

        foreach (var c in name)
        {
            var ok = (c >= 'a' && c <= 'z')
                     || (c >= 'A' && c <= 'Z')
                     || (c >= '0' && c <= '9')
                     || c == '_'
                     || c == '-';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>Renders a name so whitespace is visible in assertion messages.</summary>
    private static string ShowName(string? name)
        => name is null
            ? "<null>"
            : name.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>
    /// Recording fake: captures every request on both the streaming and non-streaming
    /// paths and always answers with a fixed text response.
    /// </summary>
    private sealed class RecordingTextClient : IChatClient
    {
        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToList());
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok.")] };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Recording fake: round 1 emits a tool call with a deliberately invalid name,
    /// round 2 emits plain text. Mirrors the existing ToolCallingClient pattern.
    /// </summary>
    private sealed class BadToolCallClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _toolArgs;
        private int _callCount;

        public List<IList<ChatMessage>> ReceivedMessages { get; } = [];

        public BadToolCallClient(string toolName, Dictionary<string, object?> toolArgs)
        {
            _toolName = toolName;
            _toolArgs = toolArgs;
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
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("call_1", _toolName, _toolArgs)],
                };
                await Task.Yield();
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("Done.")],
                };
                await Task.Yield();
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
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

    /// <summary>Builds a session whose history already contains a bad tool-call name.</summary>
    private static AgentSession CreatePersistedSessionWithBadName()
    {
        var session = AgentSession.Create("bad-name-session");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "do the thing"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(
                "call_bad", "functions.bad name",
                new Dictionary<string, object?> { ["x"] = "1" })]));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call_bad", "Unknown tool: functions.bad name")]));
        return session;
    }

    private static FunctionCallContent? FindCall(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();

    private static FunctionResultContent? FindResult(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault();

    private static void AssertNoInvalidNames(IEnumerable<IEnumerable<ChatMessage>> rounds)
    {
        foreach (var round in rounds)
        {
            foreach (var call in round.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            {
                Assert.True(
                    IsStrictlyValidName(call.Name),
                    $"Received function-call name '{ShowName(call.Name)}' is not 1-64 characters of [a-zA-Z0-9_-]");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Persisted-session repair
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sanitize_PersistedBadName_DefaultStreamingPath_RepairsNameOnly()
    {
        var client = new RecordingTextClient();
        var agent = new CodingAgent(client, MinimalOptions());
        var session = CreatePersistedSessionWithBadName();
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "continue", ct)) { }

        Assert.Single(client.ReceivedMessages);
        var sent = client.ReceivedMessages[0];

        var call = FindCall(sent);
        Assert.NotNull(call);
        Assert.Equal("functions_bad_name", call!.Name);
        Assert.Equal("call_bad", call.CallId);

        var result = FindResult(sent);
        Assert.NotNull(result);
        Assert.Equal("call_bad", result!.CallId);
        Assert.Equal("Unknown tool: functions.bad name", result.Result?.ToString());

        AssertNoInvalidNames(client.ReceivedMessages);

        // The sanitizer must never touch the stored history.
        var historyCall = FindCall(session.MessageHistory);
        Assert.NotNull(historyCall);
        Assert.Equal("functions.bad name", historyCall!.Name);
    }

    [Fact]
    public async Task Sanitize_PersistedBadName_NonStreaming_RepairsNameOnly()
    {
        var client = new RecordingTextClient();
        var agent = new CodingAgent(client, MinimalOptions());
        var session = CreatePersistedSessionWithBadName();
        var ct = TestContext.Current.CancellationToken;

        await agent.ExecuteAsync(session, "continue", ct);

        Assert.True(client.ReceivedMessages.Count >= 1);
        var sent = client.ReceivedMessages[0];

        var call = FindCall(sent);
        Assert.NotNull(call);
        Assert.Equal("functions_bad_name", call!.Name);
        Assert.Equal("call_bad", call.CallId);

        var result = FindResult(sent);
        Assert.NotNull(result);
        Assert.Equal("call_bad", result!.CallId);
        Assert.Equal("Unknown tool: functions.bad name", result.Result?.ToString());

        AssertNoInvalidNames(client.ReceivedMessages);

        var historyCall = FindCall(session.MessageHistory);
        Assert.NotNull(historyCall);
        Assert.Equal("functions.bad name", historyCall!.Name);
    }

    [Fact]
    public async Task Sanitize_PersistedBadName_ShowToolCallsInStream_RepairsNameOnly()
    {
        var client = new RecordingTextClient();
        var opts = MinimalOptions();
        opts.ShowToolCallsInStream = true;
        var agent = new CodingAgent(client, opts);
        var session = CreatePersistedSessionWithBadName();
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "continue", ct)) { }

        Assert.Single(client.ReceivedMessages);
        var sent = client.ReceivedMessages[0];

        var call = FindCall(sent);
        Assert.NotNull(call);
        Assert.Equal("functions_bad_name", call!.Name);
        Assert.Equal("call_bad", call.CallId);

        var result = FindResult(sent);
        Assert.NotNull(result);
        Assert.Equal("call_bad", result!.CallId);
        Assert.Equal("Unknown tool: functions.bad name", result.Result?.ToString());

        AssertNoInvalidNames(client.ReceivedMessages);

        var historyCall = FindCall(session.MessageHistory);
        Assert.NotNull(historyCall);
        Assert.Equal("functions.bad name", historyCall!.Name);
    }

    // ---------------------------------------------------------------------
    // Live loop: ShowToolCallsInStream path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sanitize_LiveBadToolCall_ShowToolCallsInStream_Round2CarriesSanitizedAndDispatchSawOriginal()
    {
        var client = new BadToolCallClient(
            "functions.bad", new Dictionary<string, object?> { ["x"] = "1" });
        var opts = MinimalOptions();
        opts.ShowToolCallsInStream = true;
        var agent = new CodingAgent(client, opts);
        var ct = TestContext.Current.CancellationToken;

        var allText = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "Run it", ct))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta)
                allText.Append(update.Text);
        }

        Assert.Equal(2, client.ReceivedMessages.Count);
        AssertNoInvalidNames(client.ReceivedMessages);

        // Round 2 request must carry the sanitized name with the SAME call id.
        var round2 = client.ReceivedMessages[1];
        var round2Call = FindCall(round2);
        Assert.NotNull(round2Call);
        Assert.Equal("functions_bad", round2Call!.Name);
        Assert.Equal("call_1", round2Call.CallId);

        // Tool dispatch saw the ORIGINAL name, so the manual loop answered with it.
        var round2Results = round2.SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Where(r => r.CallId == "call_1")
            .ToList();
        Assert.Single(round2Results);
        Assert.Equal("Unknown tool: functions.bad", round2Results[0].Result?.ToString());
    }

    [Fact]
    public async Task Sanitize_LiveBadToolCall_ShowToolCallsInStream_SessionKeepsOriginalName()
    {
        var client = new BadToolCallClient(
            "functions.bad", new Dictionary<string, object?> { ["x"] = "1" });
        var opts = MinimalOptions();
        opts.ShowToolCallsInStream = true;
        var agent = new CodingAgent(client, opts);
        var session = AgentSession.Create("live-bad-name");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Run it", ct)) { }

        var storedCall = FindCall(session.MessageHistory);
        Assert.NotNull(storedCall);
        Assert.Equal("functions.bad", storedCall!.Name);
        Assert.Equal("call_1", storedCall.CallId);
    }

    // ---------------------------------------------------------------------
    // Live loop: default streaming path (FunctionInvokingChatClient)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sanitize_LiveBadToolCall_DefaultStreaming_Round2CarriesSanitizedAndPairingIntact()
    {
        var client = new BadToolCallClient(
            "functions.bad", new Dictionary<string, object?> { ["x"] = "1" });
        var opts = MinimalOptions();
        // Register a real tool so FunctionInvokingChatClient is active; the invalid
        // name will NOT match it, producing the SDK's own not-found result.
        opts.CustomTools = [AIFunctionFactory.Create((string id) => $"ok {id}", "some_tool")];

        var agent = new CodingAgent(client, opts);
        var session = AgentSession.Create("live-default-bad-name");
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "Run it", ct)) { }

        Assert.Equal(2, client.ReceivedMessages.Count);
        AssertNoInvalidNames(client.ReceivedMessages);

        var round2 = client.ReceivedMessages[1];
        var round2Call = FindCall(round2);
        Assert.NotNull(round2Call);
        Assert.Equal("functions_bad", round2Call!.Name);
        Assert.Equal("call_1", round2Call.CallId);

        // A function result paired with the SAME call id must be present.
        var round2Results = round2.SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Where(r => r.CallId == "call_1")
            .ToList();
        Assert.Single(round2Results);

        // Stored session messages keep the ORIGINAL name.
        var storedCall = FindCall(session.MessageHistory);
        Assert.NotNull(storedCall);
        Assert.Equal("functions.bad", storedCall!.Name);
        Assert.Equal("call_1", storedCall.CallId);

        // And the paired result is still present and untouched.
        var storedResults = session.MessageHistory
            .SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Contains(storedResults, r => r.CallId == "call_1");
    }

    // ---------------------------------------------------------------------
    // Name-mapping unit cases
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null, "invalid_tool_name")]
    [InlineData("", "invalid_tool_name")]
    [InlineData("...", "___")]
    [InlineData("functions.bad name", "functions_bad_name")]
    [InlineData("multi_tool_use.parallel", "multi_tool_use_parallel")]
    [InlineData("already_valid-name_1", "already_valid-name_1")]
    public void SanitizeToolCallName_MapsNamesPerRules(string? original, string expected)
    {
        Assert.Equal(expected, CodingAgent.SanitizeToolCallName(original));
    }

    [Fact]
    public void SanitizeToolCallName_LongerThan64_IsCutTo64()
    {
        var original = new string('a', 40) + "." + new string('b', 40);
        var sanitized = CodingAgent.SanitizeToolCallName(original);
        Assert.Equal(64, sanitized.Length);
        Assert.Equal(new string('a', 40) + "_" + new string('b', 23), sanitized);
    }

    [Theory]
    [InlineData("ok_name")]
    [InlineData("a")]
    [InlineData("A-9_")]
    public void IsValidToolCallName_AcceptsValidNames(string name)
    {
        Assert.True(CodingAgent.IsValidToolCallName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("functions.bad")]
    [InlineData("has space")]
    [InlineData("multi_tool_use.parallel")]
    public void IsValidToolCallName_RejectsInvalidNames(string? name)
    {
        Assert.False(CodingAgent.IsValidToolCallName(name));
    }

    [Fact]
    public void SanitizeOutboundToolCallNames_ValidOnly_PassesSameInstancesThrough()
    {
        var user = new ChatMessage(ChatRole.User, "hi");
        var validCall = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call_ok", "good_name",
                new Dictionary<string, object?> { ["a"] = "b" })]);
        var messages = new List<ChatMessage> { user, validCall };

        var result = CodingAgent.SanitizeOutboundToolCallNames(messages);

        Assert.Equal(2, result.Count);
        Assert.Same(user, result[0]);
        Assert.Same(validCall, result[1]);
    }

    [Fact]
    public void SanitizeOutboundToolCallNames_InvalidCall_RewritesMessageWithoutTouchingInput()
    {
        var call = new FunctionCallContent("call_bad", "functions.bad",
            new Dictionary<string, object?> { ["x"] = "1" });
        var resultContent = new FunctionResultContent("call_bad", "Unknown tool: functions.bad");
        var original = new ChatMessage(ChatRole.Assistant,
            [new TextContent("calling"), call, resultContent])
        {
            AuthorName = "model",
            MessageId = "msg-1",
        };
        var user = new ChatMessage(ChatRole.User, "hi");
        var messages = new List<ChatMessage> { user, original };

        var result = CodingAgent.SanitizeOutboundToolCallNames(messages);

        Assert.Equal(2, result.Count);
        // The valid message passes through as the same instance.
        Assert.Same(user, result[0]);

        var rewritten = result[1];
        Assert.NotSame(original, rewritten);
        Assert.Equal(original.Role, rewritten.Role);
        Assert.Equal(original.AuthorName, rewritten.AuthorName);
        Assert.Equal(original.MessageId, rewritten.MessageId);
        Assert.Null(rewritten.RawRepresentation);

        var contents = rewritten.Contents.ToList();
        Assert.Equal(3, contents.Count);
        // The valid text content passes through as the same instance.
        Assert.Same(original.Contents.ElementAt(0), contents[0]);

        var newCall = Assert.IsType<FunctionCallContent>(contents[1]);
        Assert.Equal("functions_bad", newCall.Name);
        Assert.Equal("call_bad", newCall.CallId);
        Assert.Null(newCall.RawRepresentation);

        // The result content is untouched (same instance, same call id, same result).
        Assert.Same(resultContent, contents[2]);

        // The original message object is untouched.
        var originalCall = Assert.IsType<FunctionCallContent>(original.Contents.ElementAt(1));
        Assert.Equal("functions.bad", originalCall.Name);
    }

    [Fact]
    public void SanitizeOutboundToolCallNames_LongName_IsCutTo64()
    {
        var longName = new string('x', 70);
        var original = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call_long", longName)]);

        var result = CodingAgent.SanitizeOutboundToolCallNames([original]);

        var rewritten = Assert.Single(result);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(rewritten.Contents));
        Assert.Equal(64, call.Name!.Length);
        Assert.Equal(new string('x', 64), call.Name);
        Assert.Equal("call_long", call.CallId);
        Assert.Null(rewritten.RawRepresentation);
    }

    [Fact]
    public void SanitizeToolCallName_NullName_MapsToFallback()
    {
        // FunctionCallContent's constructor rejects a null name, so exercise the
        // mapping helper directly — deserialized/provider-produced null names are
        // still possible in flight and must map to the fallback.
        Assert.Equal("invalid_tool_name", CodingAgent.SanitizeToolCallName(null));
        Assert.False(CodingAgent.IsValidToolCallName(null));
    }

    [Fact]
    public void SanitizeOutboundToolCallNames_EmptyName_MapsToFallback()
    {
        var original = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call_empty", "")]);

        var result = CodingAgent.SanitizeOutboundToolCallNames([original]);

        var rewritten = Assert.Single(result);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(rewritten.Contents));
        Assert.Equal("invalid_tool_name", call.Name);
        Assert.Equal("call_empty", call.CallId);
    }

    // ---------------------------------------------------------------------
    // Trailing-newline boundary (the `$`-anchor hole the validator fix closed)
    // ---------------------------------------------------------------------

    [Fact]
    public void IsValidToolCallName_TrailingNewline_IsInvalid()
    {
        // In .NET, `$` also matches immediately before a final '\n', so the old
        // regex-based validator accepted "good\n". The strict check must not.
        Assert.False(CodingAgent.IsValidToolCallName("good\n"));
        Assert.False(CodingAgent.IsValidToolCallName("functions_bad_name\n"));
    }

    [Fact]
    public void IsValidToolCallName_LeadingNewlineAndEmbeddedWhitespace_AreInvalid()
    {
        // The old `$` anchor only forgave a trailing newline; a strict per-character
        // check must reject newline anywhere, plus other whitespace.
        Assert.False(CodingAgent.IsValidToolCallName("\ngood"));
        Assert.False(CodingAgent.IsValidToolCallName("go\nod"));
        Assert.False(CodingAgent.IsValidToolCallName("has space"));
        Assert.False(CodingAgent.IsValidToolCallName("tab\there"));
    }

    [Fact]
    public void IsValidToolCallName_63ValidCharsPlusNewline_IsInvalid()
    {
        // 63 valid characters + a trailing newline = 64 characters total: the length
        // bound alone would accept it, so the newline must be caught per character.
        var name = new string('a', 63) + "\n";
        Assert.Equal(64, name.Length);
        Assert.False(CodingAgent.IsValidToolCallName(name));
    }

    [Fact]
    public void IsValidToolCallName_64ValidCharsPlusNewline_IsInvalid()
    {
        // 64 valid characters + a trailing newline = 65 characters total: the old
        // regex accepted it (anchor before the newline, 64 "valid" chars matched),
        // but the length bound alone must also reject it. Both defenses apply.
        var name = new string('a', 64) + "\n";
        Assert.Equal(65, name.Length);
        Assert.False(CodingAgent.IsValidToolCallName(name));
    }

    [Fact]
    public void IsValidToolCallName_64ValidChars_IsValidBoundary()
    {
        // Control: exactly 64 valid characters is still valid — proves the rejection
        // above comes from the newline/length, not an off-by-one in the bound.
        var name = new string('a', 64);
        Assert.Equal(64, name.Length);
        Assert.True(CodingAgent.IsValidToolCallName(name));
    }

    [Theory]
    [InlineData("good\n", "good_")]
    [InlineData("functions_bad_name\n", "functions_bad_name_")]
    [InlineData("\n", "_")]
    [InlineData("bad\nname", "bad_name")]
    public void SanitizeToolCallName_TrailingNewline_BecomesUnderscore(string original, string expected)
    {
        var sanitized = CodingAgent.SanitizeToolCallName(original);
        Assert.Equal(expected, sanitized);
        // Every mapped name must itself satisfy the strict validator.
        Assert.True(CodingAgent.IsValidToolCallName(sanitized),
            $"Sanitized name '{ShowName(sanitized)}' must satisfy IsValidToolCallName");
    }

    [Fact]
    public void SanitizeToolCallName_63ValidCharsPlusNewline_CutsTo64Valid()
    {
        // 63 valid chars + newline: after replacement it is 64 characters of valid
        // characters ("a…a_"), so the 64-cap is exactly what keeps it usable.
        var name = new string('a', 63) + "\n";
        var sanitized = CodingAgent.SanitizeToolCallName(name);
        Assert.Equal(64, sanitized.Length);
        Assert.Equal(new string('a', 63) + "_", sanitized);
        Assert.True(CodingAgent.IsValidToolCallName(sanitized));
    }

    [Fact]
    public void SanitizeOutboundToolCallNames_TrailingNewline_RewritesMessageStrictly()
    {
        var call = new FunctionCallContent("call_nl", "good\n",
            new Dictionary<string, object?> { ["x"] = "1" });
        var resultContent = new FunctionResultContent("call_nl", "Unknown tool: good\n");
        var original = new ChatMessage(ChatRole.Assistant,
            [new TextContent("calling"), call, resultContent])
        {
            AuthorName = "model",
            MessageId = "msg-nl",
        };
        var user = new ChatMessage(ChatRole.User, "hi");
        var messages = new List<ChatMessage> { user, original };

        var result = CodingAgent.SanitizeOutboundToolCallNames(messages);

        Assert.Equal(2, result.Count);
        Assert.Same(user, result[0]);

        var rewritten = result[1];
        Assert.NotSame(original, rewritten);
        Assert.Equal(original.Role, rewritten.Role);
        Assert.Equal(original.AuthorName, rewritten.AuthorName);
        Assert.Equal(original.MessageId, rewritten.MessageId);
        Assert.Null(rewritten.RawRepresentation);

        var contents = rewritten.Contents.ToList();
        Assert.Equal(3, contents.Count);
        Assert.Same(original.Contents.ElementAt(0), contents[0]);

        var newCall = Assert.IsType<FunctionCallContent>(contents[1]);
        Assert.Equal("good_", newCall.Name);
        Assert.Equal("call_nl", newCall.CallId);
        Assert.Null(newCall.RawRepresentation);
        // Strict oracle: the rewritten name must satisfy the per-character check.
        Assert.True(IsStrictlyValidName(newCall.Name));

        // The result content is untouched (same instance, same call id).
        Assert.Same(resultContent, contents[2]);

        // The original message object is untouched.
        var originalCall = Assert.IsType<FunctionCallContent>(original.Contents.ElementAt(1));
        Assert.Equal("good\n", originalCall.Name);
    }

    [Fact]
    public async Task Sanitize_PersistedTrailingNewlineName_DefaultStreamingPath_RepairsStrictly()
    {
        var client = new RecordingTextClient();
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("newline-session");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "do the thing"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(
                "call_nl", "functions_bad_name\n",
                new Dictionary<string, object?> { ["x"] = "1" })]));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call_nl", "Unknown tool: functions_bad_name\n")]));
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in agent.ExecuteStreamingAsync(session, "continue", ct)) { }

        Assert.Single(client.ReceivedMessages);
        var sent = client.ReceivedMessages[0];

        var call = FindCall(sent);
        Assert.NotNull(call);
        Assert.Equal("functions_bad_name_", call!.Name);
        Assert.Equal("call_nl", call.CallId);
        Assert.True(IsStrictlyValidName(call.Name));

        var result = FindResult(sent);
        Assert.NotNull(result);
        Assert.Equal("call_nl", result!.CallId);
        Assert.Equal("Unknown tool: functions_bad_name\n", result.Result?.ToString());

        // Strict oracle over every received name: no trailing-newline name may pass.
        AssertNoInvalidNames(client.ReceivedMessages);

        // Stored history keeps the ORIGINAL (newline-bearing) name.
        var historyCall = FindCall(session.MessageHistory);
        Assert.NotNull(historyCall);
        Assert.Equal("functions_bad_name\n", historyCall!.Name);
    }

    [Fact]
    public async Task Sanitize_PersistedTrailingNewlineName_NonStreaming_RepairsStrictly()
    {
        var client = new RecordingTextClient();
        var agent = new CodingAgent(client, MinimalOptions());
        var session = AgentSession.Create("newline-session-nostream");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "do the thing"));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(
                "call_nl", "functions_bad_name\n",
                new Dictionary<string, object?> { ["x"] = "1" })]));
        session.MessageHistory.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call_nl", "Unknown tool: functions_bad_name\n")]));
        var ct = TestContext.Current.CancellationToken;

        await agent.ExecuteAsync(session, "continue", ct);

        Assert.True(client.ReceivedMessages.Count >= 1);
        var sent = client.ReceivedMessages[0];

        var call = FindCall(sent);
        Assert.NotNull(call);
        Assert.Equal("functions_bad_name_", call!.Name);
        Assert.Equal("call_nl", call.CallId);
        Assert.True(IsStrictlyValidName(call.Name));

        var result = FindResult(sent);
        Assert.NotNull(result);
        Assert.Equal("call_nl", result!.CallId);
        Assert.Equal("Unknown tool: functions_bad_name\n", result.Result?.ToString());

        AssertNoInvalidNames(client.ReceivedMessages);

        var historyCall = FindCall(session.MessageHistory);
        Assert.NotNull(historyCall);
        Assert.Equal("functions_bad_name\n", historyCall!.Name);
    }
}