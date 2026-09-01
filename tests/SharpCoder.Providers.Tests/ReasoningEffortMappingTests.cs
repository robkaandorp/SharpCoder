using SharpCoder.Providers;

using Microsoft.Extensions.AI;

using OllamaSharp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Tests for <see cref="ReasoningEffortMappingHandler"/>: the JSON-aware provider-boundary
/// translation of the internal <c>"extra_high"</c> reasoning-effort wire value.
/// </summary>
public sealed class ReasoningEffortMappingHandlerTests
{
    private const string Mapped = "xhigh";

    private static async Task<string> SendAsync(
        HttpContent content, string mappedValue = Mapped, string? customPropertyName = null)
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(
            new ReasoningEffortMappingHandler(mappedValue, terminal, customPropertyName));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return terminal.LastBody ?? string.Empty;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ── Targeted replacements ────────────────────────────────────────────────

    /// <summary>Top-level <c>reasoning_effort</c> with the exact source value is replaced.</summary>
    [Fact]
    public async Task TopLevelReasoningEffort_ExtraHigh_IsReplaced()
    {
        var body = await SendAsync(Json("""{"model":"gpt-5","reasoning_effort":"extra_high"}"""));

        Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("gpt-5", JsonNode.Parse(body)!["model"]!.GetValue<string>());
    }

    /// <summary>Nested <c>reasoning.effort</c> (responses API shape) is replaced.</summary>
    [Fact]
    public async Task NestedReasoningEffort_ExtraHigh_IsReplaced()
    {
        var body = await SendAsync(Json("""{"model":"gpt-5","reasoning":{"effort":"extra_high"}}"""));

        Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>A configured custom property (e.g. Ollama's <c>think</c>) is replaced.</summary>
    [Fact]
    public async Task CustomProperty_ExtraHigh_IsReplaced_WhenConfigured()
    {
        var body = await SendAsync(
            Json("""{"model":"gpt-oss:20b","think":"extra_high"}"""),
            mappedValue: "max",
            customPropertyName: "think");

        Assert.Equal("max", JsonNode.Parse(body)!["think"]!.GetValue<string>());
    }

    /// <summary>The custom property supplements — it does not replace — the default targets.</summary>
    [Fact]
    public async Task CustomProperty_DoesNotDisableDefaultTargets()
    {
        var body = await SendAsync(
            Json("""{"reasoning_effort":"extra_high","reasoning":{"effort":"extra_high"},"think":"extra_high"}"""),
            mappedValue: "max",
            customPropertyName: "think");

        var json = JsonNode.Parse(body)!;
        Assert.Equal("max", json["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("max", json["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("max", json["think"]!.GetValue<string>());
    }

    /// <summary>A custom property is ignored when the handler was not configured with it.</summary>
    [Fact]
    public async Task CustomProperty_NotConfigured_IsUnchanged()
    {
        var body = await SendAsync(Json("""{"think":"extra_high"}"""));

        Assert.Equal("extra_high", JsonNode.Parse(body)!["think"]!.GetValue<string>());
    }

    // ── Negative cases: values that must survive untouched ───────────────────

    /// <summary>A reasoning effort other than the source value is never rewritten.</summary>
    [Fact]
    public async Task ReasoningEffort_High_IsUnchanged()
    {
        var body = await SendAsync(Json("""{"reasoning_effort":"high"}"""));

        Assert.Equal("high", JsonNode.Parse(body)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>The literal text in a user message is never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInUserMessage_IsUnchanged()
    {
        const string original = """{"messages":[{"role":"user","content":"please use extra_high effort"}]}""";

        var body = await SendAsync(Json(original));

        Assert.Contains("extra_high", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>Tool-call arguments containing the value are never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInToolArguments_IsUnchanged()
    {
        const string original =
            """{"messages":[{"role":"assistant","tool_calls":[{"function":{"name":"f","arguments":"{\u0022effort\u0022:\u0022extra_high\u0022}"}}]}]}""";

        var body = await SendAsync(Json(original));

        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>An array element equal to the value is never rewritten.</summary>
    [Fact]
    public async Task ExtraHighInArray_IsUnchanged()
    {
        const string original = """{"supported_efforts":["low","high","extra_high"]}""";

        var body = await SendAsync(Json(original));

        Assert.DoesNotContain("xhigh", body, StringComparison.Ordinal);
        Assert.Equal(original, body);
    }

    /// <summary>Bodies with no reasoning property at all pass through byte-identical.</summary>
    [Fact]
    public async Task NoReasoningProperty_IsUnchanged()
    {
        const string original = """{"model":"gpt-5","messages":[]}""";

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    /// <summary>Non-JSON content is forwarded untouched even if it contains the value.</summary>
    [Fact]
    public async Task NonJsonContent_IsUnchanged()
    {
        const string original = """{"reasoning_effort":"extra_high"}""";

        var body = await SendAsync(new StringContent(original, Encoding.UTF8, "text/plain"));

        Assert.Equal(original, body);
    }

    /// <summary>Malformed JSON passes through unchanged and must not throw.</summary>
    [Fact]
    public async Task MalformedJson_IsUnchanged_AndDoesNotThrow()
    {
        const string original = """{"reasoning_effort":"extra_high" ,,, """;

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    /// <summary>A JSON array root (valid JSON, no target properties) passes through unchanged.</summary>
    [Fact]
    public async Task JsonArrayRoot_IsUnchanged()
    {
        const string original = """["extra_high"]""";

        var body = await SendAsync(Json(original));

        Assert.Equal(original, body);
    }

    // ── Content lifetime ─────────────────────────────────────────────────────

    /// <summary>
    /// When a replacement occurs the original content is no longer referenced by the request,
    /// so the handler must dispose it and attach a fresh <see cref="StringContent"/>.
    /// </summary>
    [Fact]
    public async Task OriginalContent_IsDisposed_WhenReplacementOccurs()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"extra_high"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(content.Disposed);
        Assert.NotSame(content, request.Content);
        Assert.Equal("xhigh", JsonNode.Parse(terminal.LastBody!)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>When nothing is replaced the original content stays attached and undisposed.</summary>
    [Fact]
    public async Task OriginalContent_IsNotDisposed_WhenUnchanged()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"high"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(content.Disposed);
        Assert.Same(content, request.Content);
    }

    /// <summary>
    /// The replacement content must not inherit headers from the original — only the
    /// <see cref="StringContent"/> defaults (JSON content type, recomputed length).
    /// </summary>
    [Fact]
    public async Task ReplacementContent_DoesNotCopyOriginalHeaders()
    {
        var terminal = new CapturingTerminalHandler();
        using var client = new HttpClient(new ReasoningEffortMappingHandler(Mapped, terminal));

        var content = new DisposalTrackingContent("""{"reasoning_effort":"extra_high"}""");
        content.Headers.Add("X-Custom-Marker", "should-not-survive");
        content.Headers.ContentEncoding.Add("identity");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(terminal.LastContentHeaders!.Contains("X-Custom-Marker"));
        Assert.Empty(terminal.LastContentHeaders.ContentEncoding);
        Assert.Equal("application/json", terminal.LastContentHeaders.ContentType?.MediaType);
        Assert.Equal(terminal.LastBody!.Length, terminal.LastContentHeaders.ContentLength);
    }

    /// <summary>Content that carries a disposal flag so tests can assert lifetime behaviour.</summary>
    private sealed class DisposalTrackingContent : HttpContent
    {
        private readonly byte[] _bytes;

        public DisposalTrackingContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Tests for <see cref="ReasoningEffortClampingClient"/>: the provider-boundary clamp that
/// lowers <see cref="ReasoningEffort.ExtraHigh"/> for providers that reject it.
/// </summary>
public sealed class ReasoningEffortClampingClientTests
{
    /// <summary><see cref="ReasoningEffort.ExtraHigh"/> is lowered to the configured maximum.</summary>
    [Fact]
    public async Task ExtraHigh_IsClampedToHigh()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary>Values at or below the maximum are forwarded without cloning.</summary>
    [Theory]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.None)]
    public async Task NonExtraHighEfforts_ForwardOriginalInstance(ReasoningEffort effort)
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken);

        Assert.Same(options, inner.LastOptions);
        Assert.Same(options.Reasoning, inner.LastOptions!.Reasoning);
        Assert.Equal(effort, inner.LastOptions.Reasoning!.Effort);
    }

    /// <summary>Null options and options without reasoning are forwarded unchanged.</summary>
    [Fact]
    public void NullOrReasoninglessOptions_AreReturnedUnchanged()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Null(client.ClampReasoning(null));

        var noReasoning = new ChatOptions();
        Assert.Same(noReasoning, client.ClampReasoning(noReasoning));

        var emptyReasoning = new ChatOptions { Reasoning = new ReasoningOptions() };
        Assert.Same(emptyReasoning, client.ClampReasoning(emptyReasoning));
    }

    /// <summary>The caller's options and reasoning options are never mutated by a clamp.</summary>
    [Fact]
    public void ClampReasoning_DoesNotMutateCallerOptions()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full };
        var options = new ChatOptions { Reasoning = reasoning, Temperature = 0.25f, ModelId = "gpt-5" };

        var clamped = client.ClampReasoning(options);

        Assert.NotSame(options, clamped);
        Assert.NotSame(reasoning, clamped!.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, reasoning.Effort);
        Assert.Same(reasoning, options.Reasoning);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
    }

    /// <summary>All other options members survive the clamp clone.</summary>
    [Fact]
    public void ClampReasoning_PreservesOtherMembers()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary },
            Temperature = 0.5f,
            MaxOutputTokens = 1234,
            ModelId = "gpt-5",
            TopP = 0.9f,
            Seed = 42,
            StopSequences = ["stop"],
        };

        var clamped = client.ClampReasoning(options)!;

        Assert.Equal(0.5f, clamped.Temperature);
        Assert.Equal(1234, clamped.MaxOutputTokens);
        Assert.Equal("gpt-5", clamped.ModelId);
        Assert.Equal(0.9f, clamped.TopP);
        Assert.Equal(42, clamped.Seed);
        Assert.Equal(["stop"], clamped.StopSequences);
        Assert.Equal(ReasoningOutput.Summary, clamped.Reasoning!.Output);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning.Effort);
    }

    /// <summary>Streaming calls clamp too.</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ClampsEffort()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options, TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary><see cref="ReasoningEffortClampingClient.Metadata"/> comes from the inner client.</summary>
    [Fact]
    public void Metadata_IsForwardedToInner()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Same(inner.Metadata, client.Metadata);
    }

    /// <summary><c>GetService</c> delegates verbatim to the inner client.</summary>
    [Fact]
    public void GetService_IsForwardedToInner()
    {
        var inner = new RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        Assert.Same(inner, client.GetService(typeof(RecordingChatClient)));
        Assert.Same(inner.Metadata, client.GetService(typeof(ChatClientMetadata)));
        Assert.Null(client.GetService(typeof(string), "key"));
        Assert.Equal((typeof(string), (object?)"key"), inner.LastServiceRequest);
    }

    /// <summary>Disposing the wrapper disposes the inner client it owns.</summary>
    [Fact]
    public void Dispose_ForwardsToInner()
    {
        var inner = new RecordingChatClient();
        var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        client.Dispose();

        Assert.True(inner.Disposed);
    }

    /// <summary>A minimal <see cref="IChatClient"/> that records what it was handed.</summary>
    internal sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public bool Disposed { get; private set; }
        public (Type, object?)? LastServiceRequest { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("recording", new Uri("https://example.invalid"), "test-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            LastServiceRequest = (serviceType, serviceKey);
            if (serviceKey is not null) return null;
            if (serviceType == typeof(ChatClientMetadata)) return Metadata;
            if (serviceType == typeof(RecordingChatClient)) return this;
            return null;
        }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>
/// Wiring tests that exercise the FULL production handler chains (including the resilience
/// handler) through <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler)"/> and the
/// provider wrappers, without any network access.
/// </summary>
public sealed class ReasoningEffortWiringTests
{
    /// <summary>
    /// Copilot chat/completions branch: <c>"reasoning_effort":"extra_high"</c> reaches the
    /// transport as <c>"xhigh"</c> after passing through resilience + choice-merging + mapping.
    /// </summary>
    [Fact]
    public async Task CopilotChatCompletions_ExtraHigh_ReachesTransportAsXhigh()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"extra_high"}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("hi", sent["messages"]![0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// Copilot responses branch: nested <c>"reasoning":{"effort":"extra_high"}</c> reaches the
    /// transport as <c>"xhigh"</c> after passing through resilience + responses + mapping.
    /// </summary>
    [Fact]
    public async Task CopilotResponses_ExtraHigh_ReachesTransportAsXhigh()
    {
        var terminal = new CapturingTerminalHandler("""{"output":[]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: true, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>The Copilot chain never rewrites a genuine <c>high</c> request.</summary>
    [Fact]
    public async Task CopilotChatCompletions_High_IsUnchanged()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[],"reasoning_effort":"high"}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("high", JsonNode.Parse(terminal.LastBody!)!["reasoning_effort"]!.GetValue<string>());
    }

    /// <summary>
    /// <see cref="ChatClientFactory.BuildHandlerChain"/> must link handlers without discarding
    /// links the caller already established — the bug the old <c>CreateResilientHandler</c> had.
    /// </summary>
    [Fact]
    public async Task BuildHandlerChain_PreservesEveryHandlerInOrder()
    {
        var order = new List<string>();
        var terminal = new CapturingTerminalHandler();
        var outer = new MarkerHandler("outer", order);
        var middle = new MarkerHandler("middle", order);

        var chain = ChatClientFactory.BuildHandlerChain(outer, middle, terminal);
        Assert.Same(outer, chain);

        using var client = new HttpClient(chain);
        using var response = await client.GetAsync("https://example.invalid/ping", TestContext.Current.CancellationToken);

        Assert.Equal(["outer", "middle"], order);
    }

    /// <summary>
    /// <see cref="ChatClientFactory.CreateResilientHandler"/> must place the supplied chain
    /// beneath the resilience handler without overwriting its inner links.
    /// </summary>
    [Fact]
    public async Task CreateResilientHandler_DoesNotDisconnectSuppliedChain()
    {
        var order = new List<string>();
        var terminal = new CapturingTerminalHandler();
        var supplied = ChatClientFactory.BuildHandlerChain(new MarkerHandler("supplied", order), terminal);

        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(supplied));
        using var response = await client.GetAsync("https://example.invalid/ping", TestContext.Current.CancellationToken);

        Assert.Equal(["supplied"], order);
        Assert.Equal(1, terminal.CallCount);
    }

    /// <summary>Non-delegating handlers may only appear last in a chain.</summary>
    [Fact]
    public void BuildHandlerChain_NonDelegatingHandlerBeforeEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ChatClientFactory.BuildHandlerChain(new CapturingTerminalHandler(), new CapturingTerminalHandler()));
    }

    /// <summary>An empty chain is a programming error.</summary>
    [Fact]
    public void BuildHandlerChain_NoHandlers_Throws()
        => Assert.Throws<ArgumentException>(() => ChatClientFactory.BuildHandlerChain());

    /// <summary>
    /// GitHub provider: the clamping wrapper lowers <see cref="ReasoningEffort.ExtraHigh"/> to
    /// <see cref="ReasoningEffort.High"/> before the inner client sees it.
    /// </summary>
    [Fact]
    public async Task GitHubClampingWrapper_ExtraHigh_InnerReceivesHigh()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using IChatClient client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, inner.LastOptions!.Reasoning!.Effort);
    }

    /// <summary>
    /// REAL PATH: <c>ChatOptions.ExtraHigh → OllamaApiClient → handler chain → terminal</c>.
    /// This drives the production Ollama stack end to end (no synthetic JSON injected) and proves
    /// the terminal actually receives <c>"think":"max"</c>. This is the test that a body-level-only
    /// implementation cannot pass, because OllamaSharp collapses ExtraHigh to "high" during
    /// serialization, before any HTTP body exists.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH, streaming: the streaming entry point must map ExtraHigh the same way, since
    /// OllamaSharp maps options separately for streaming and non-streaming requests.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_Streaming_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken))
        {
            // drain
        }

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.True(sent["stream"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH regression guard: a genuine <see cref="ReasoningEffort.High"/> request must still
    /// send <c>"think":"high"</c>. This is exactly the distinction a body-level rewrite of "high"
    /// would have destroyed.
    /// </summary>
    [Theory]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.Low, "low")]
    public async Task OllamaRealPath_LowerEfforts_AreUnchanged(ReasoningEffort effort, string expectedThink)
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } },
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedThink, JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>REAL PATH: <see cref="ReasoningEffort.None"/> still disables thinking entirely.</summary>
    [Fact]
    public async Task OllamaRealPath_NoneEffort_SendsThinkFalse()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } },
            TestContext.Current.CancellationToken);

        Assert.False(JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH: the ExtraHigh interception must not disturb any other request member.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_PreservesOtherRequestOptions()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            Temperature = 0.25f,
            MaxOutputTokens = 77,
        };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["keep_alive"] = "5m" };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.Equal("gpt-oss:20b", sent["model"]!.GetValue<string>());
        Assert.Equal("5m", sent["keep_alive"]!.GetValue<string>());
        Assert.Equal(0.25f, sent["options"]!["temperature"]!.GetValue<float>(), 3);
        Assert.Equal(77, sent["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("hello", sent["messages"]![0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: the caller's <see cref="ChatOptions"/> must not be mutated by the interception —
    /// callers frequently reuse a single options instance across many requests.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExtraHigh_DoesNotMutateCallerOptions()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Null(options.AdditionalProperties);
        Assert.Equal(ReasoningEffort.ExtraHigh, options.Reasoning!.Effort);
    }

    /// <summary>
    /// REAL PATH: an explicit caller-supplied <c>think</c> value always wins over the ExtraHigh
    /// interception — the wrapper must never override deliberate caller intent.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitThinkValue_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = "low" };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: an explicit <c>Think</c> supplied through the caller's
    /// <see cref="ChatOptions.RawRepresentationFactory"/> must win over the ExtraHigh injection.
    /// OllamaSharp applies <c>AdditionalProperties["think"]</c> AFTER adopting the raw request, so
    /// injecting blindly would silently overwrite the caller's explicit raw value.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitRawRepresentationThink_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: a boolean <c>Think</c> from the raw representation (e.g. explicitly disabling
    /// thinking) must also survive the ExtraHigh injection.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ExplicitRawRepresentationThinkFalse_IsNotOverridden()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue(false),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.False(JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<bool>());
    }

    /// <summary>
    /// REAL PATH: a raw representation that does NOT set <c>Think</c> must still receive the
    /// ExtraHigh injection, and its other members must survive.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_RawRepresentationWithoutThink_StillMapsExtraHigh()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { KeepAlive = "9m" },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["think"]!.GetValue<string>());
        Assert.Equal("9m", sent["keep_alive"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL PATH: a raw-representation factory that returns <see langword="null"/> or a non-Ollama
    /// object means "no explicit think", so the ExtraHigh injection still applies.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_RawRepresentationFactoryReturningUnusableValue_StillMapsExtraHigh()
    {
        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        foreach (var factory in new Func<IChatClient, object?>[] { _ => null, _ => "not-a-chat-request" })
        {
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                RawRepresentationFactory = factory!,
            };

            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

            Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// REAL PATH: disposing a factory-created Ollama client must dispose the whole injected
    /// transport chain. OllamaSharp 5.4.30 only disposes an HTTP client it created itself, so the
    /// wrapper has to own the injected one — otherwise the handler chain and its sockets leak.
    /// Proven by asserting the terminal handler (the innermost link) was disposed.
    /// </summary>
    [Fact]
    public void OllamaRealPath_DisposingClient_DisposesInjectedTransportChain()
    {
        var terminal = new OllamaTerminalHandler();
        var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        Assert.False(terminal.Disposed);

        client.Dispose();

        // Disposing HttpClient disposes its handler chain down to the terminal handler.
        Assert.True(terminal.Disposed);
    }

    /// <summary>
    /// The body-level mapping handler remains wired on the Ollama chain as defence in depth: a
    /// hand-built body carrying the canonical <c>extra_high</c> wire value is still normalized to
    /// <c>max</c>, while a genuine <c>"high"</c> is never rewritten.
    /// </summary>
    [Fact]
    public async Task OllamaMapping_ThinkProperty_MapsExtraHighToMax()
    {
        var terminal = new CapturingTerminalHandler("""{"done":true}""");
        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)));

        using var extraHigh = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent("""{"model":"gpt-oss:20b","think":"extra_high"}""", Encoding.UTF8, "application/json"),
        };
        using var extraHighResponse = await client.SendAsync(extraHigh, TestContext.Current.CancellationToken);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());

        using var high = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent("""{"model":"gpt-oss:20b","think":"high"}""", Encoding.UTF8, "application/json"),
        };
        using var highResponse = await client.SendAsync(high, TestContext.Current.CancellationToken);
        Assert.Equal("high", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// Terminal handler that speaks just enough of the Ollama <c>/api/chat</c> protocol
    /// (newline-delimited JSON with a terminating <c>done</c> record) for OllamaSharp to complete a
    /// request, while capturing the outgoing body.
    /// </summary>
    internal sealed class OllamaTerminalHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        /// <summary>Set when this handler is disposed, proving the chain's disposal reached it.</summary>
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            const string done = """{"model":"gpt-oss:20b","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(done, Encoding.UTF8, "application/x-ndjson"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Delegating handler that records that it ran, then forwards.</summary>
    private sealed class MarkerHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly List<string> _order;

        public MarkerHandler(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _order.Add(_name);
            return base.SendAsync(request, ct);
        }
    }
}

/// <summary>
/// Terminal (non-delegating) handler that records the request body and content headers it
/// receives and returns a canned JSON response, so handler chains can be tested offline.
/// </summary>
internal sealed class CapturingTerminalHandler : HttpMessageHandler
{
    private readonly string _responseBody;

    public CapturingTerminalHandler(string responseBody = "{}") => _responseBody = responseBody;

    public string? LastBody { get; private set; }
    public System.Net.Http.Headers.HttpContentHeaders? LastContentHeaders { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(ct);
            LastContentHeaders = request.Content.Headers;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// Additional integration tests for the reasoning-effort mapping: defensive guard branches,
/// full-chain round-trips, and provider-boundary semantics.
/// </summary>
public sealed class ReasoningEffortIntegrationTests
{
    /// <summary>
    /// When a derived <see cref="ChatOptions"/> clones itself sharing the same
    /// <see cref="ReasoningOptions"/> instance, the clamping client must detect the shared
    /// reference and allocate a fresh <see cref="ReasoningOptions"/> rather than mutating
    /// the caller's reasoning instance.
    /// </summary>
    [Fact]
    public void ClampReasoning_SharedReasoningOnClone_CreatesFreshReasoningOptions()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        var sharedReasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full };
        var options = new SharedReasoningChatOptions(sharedReasoning, 0.5f, "gpt-5");

        var clamped = client.ClampReasoning(options)!;

        Assert.NotSame(sharedReasoning, clamped.Reasoning);
        Assert.NotSame(options.Reasoning, clamped.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, sharedReasoning.Effort);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.Full, clamped.Reasoning.Output);
        Assert.Equal(0.5f, clamped.Temperature);
        Assert.Equal("gpt-5", clamped.ModelId);
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> returns a
    /// clone with a null <see cref="ReasoningOptions"/> — the defensive guard must allocate
    /// a fresh one rather than NRE on <c>clone.Reasoning.Effort</c>.
    /// </summary>
    [Fact]
    public void ClampReasoning_NullReasoningOnClone_CreatesFreshReasoningOptions()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new ReasoningEffortClampingClient(inner, ReasoningEffort.High);

        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Summary };
        var options = new NullReasoningCloneChatOptions(reasoning, "test-model");

        var clamped = client.ClampReasoning(options)!;

        Assert.NotNull(clamped.Reasoning);
        Assert.Equal(ReasoningEffort.High, clamped.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.Summary, clamped.Reasoning.Output);
        Assert.NotSame(reasoning, clamped.Reasoning);
        Assert.Equal(ReasoningEffort.ExtraHigh, reasoning.Effort);
        Assert.Equal("test-model", clamped.ModelId);
    }

    /// <summary>
    /// Full Copilot chain round-trip with both target properties present in the same body:
    /// both <c>reasoning_effort</c> and <c>reasoning.effort</c> must be mapped to
    /// <c>xhigh</c> independently.
    /// </summary>
    [Fact]
    public async Task CopilotChain_BothTargetsInSameBody_AreBothMapped()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","reasoning_effort":"extra_high","reasoning":{"effort":"extra_high"},"messages":[]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>
    /// Full Copilot chain: a request with no reasoning properties at all must pass through
    /// with body structure unchanged (no spurious injection of reasoning keys).
    /// </summary>
    [Fact]
    public async Task CopilotChain_NoReasoningInBody_BodyStructureUnchanged()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        var original = """{"model":"gpt-5","messages":[{"role":"user","content":"hello"}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(original, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = (JsonObject)JsonNode.Parse(terminal.LastBody!)!;
        Assert.False(sent.ContainsKey("reasoning_effort"));
        Assert.False(sent.ContainsKey("reasoning"));
        Assert.Equal("gpt-5", sent["model"]!.GetValue<string>());
    }

    /// <summary>
    /// Full Copilot chain: "extra_high" appearing inside a tool-call argument (escaped JSON
    /// string) must survive the mapping handler untouched — only top-level reasoning
    /// properties are targeted.
    /// </summary>
    [Fact]
    public async Task CopilotChain_ExtraHighInToolArguments_SurvivesMapping()
    {
        var terminal = new CapturingTerminalHandler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal);

        var original = """{"model":"gpt-5","reasoning_effort":"extra_high","messages":[{"role":"assistant","tool_calls":[{"function":{"name":"set_effort","arguments":"{\\u0022level\\u0022:\\u0022extra_high\\u0022}"}}]}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(original, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning_effort"]!.GetValue<string>());
        // The tool-call argument must still contain "extra_high", not "xhigh"
        var args = sent["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.Contains("extra_high", args, StringComparison.Ordinal);
        Assert.DoesNotContain("xhigh", args, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Ollama mapping handler, wired through the resilience layer, maps a canonical
    /// <c>"extra_high"</c> in the <c>think</c> property to <c>"max"</c> while also mapping
    /// <c>reasoning_effort</c> (the default target) when both are present.
    /// </summary>
    [Fact]
    public async Task OllamaMapping_BothThinkAndReasoningEffort_AreMappedToMax()
    {
        var terminal = new CapturingTerminalHandler("""{"done":true}""");
        using var client = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)));

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat")
        {
            Content = new StringContent(
                """{"model":"gpt-oss:20b","reasoning_effort":"extra_high","think":"extra_high"}""",
                Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("max", sent["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("max", sent["think"]!.GetValue<string>());
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> shares the
    /// same <see cref="ReasoningOptions"/> instance, exercising the defensive guard.
    /// </summary>
    private sealed class SharedReasoningChatOptions : ChatOptions
    {
        private readonly ReasoningOptions _sharedReasoning;

        public SharedReasoningChatOptions(ReasoningOptions reasoning, float temperature, string modelId)
        {
            _sharedReasoning = reasoning;
            Reasoning = reasoning;
            Temperature = temperature;
            ModelId = modelId;
        }

        public override ChatOptions Clone()
        {
            // Clone shares the same Reasoning instance (simulating a buggy/derived Clone).
            var clone = new SharedReasoningChatOptions(_sharedReasoning, Temperature ?? 0f, ModelId ?? string.Empty);
            return clone;
        }
    }

    /// <summary>
    /// A <see cref="ChatOptions"/> subclass whose <see cref="ChatOptions.Clone"/> returns a
    /// clone with a null <see cref="ReasoningOptions"/>, exercising the null branch of the guard.
    /// </summary>
    private sealed class NullReasoningCloneChatOptions : ChatOptions
    {
        public NullReasoningCloneChatOptions(ReasoningOptions reasoning, string modelId)
        {
            Reasoning = reasoning;
            ModelId = modelId;
        }

        public override ChatOptions Clone()
        {
            // Clone has null Reasoning — simulating a Clone that drops the reasoning instance.
            var clone = new NullReasoningCloneChatOptions(null!, ModelId!);
            return clone;
        }
    }
}

/// <summary>
/// Retry-safety tests for <see cref="ChatClientFactory.CopilotResponsesHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The production chain places the handler <em>beneath</em> the <c>ResilienceHandler</c>, so a
/// transient failure re-enters <c>CopilotResponsesHandler.SendAsync</c>. A probe confirmed that
/// Polly reuses the <b>same</b> <see cref="HttpRequestMessage"/> instance across attempts and that
/// both <c>request.Options</c> and a replaced <c>Content</c> persist into the retry. That makes the
/// transformation re-entrant, so it must be idempotent and must not commit conversation state until
/// an authoritative response arrives.
/// </para>
/// <para>
/// Every test here forces real transient failures through
/// <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler, out DelegatingHandler, TimeSpan?)"/>,
/// which exercises the genuine resilience pipeline (same retry count and backoff type as
/// production, only the base delay is shortened).
/// </para>
/// </remarks>
public sealed class CopilotResponsesHandlerRetryTests
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(1);

    private static HttpClient CreateClient(
        HttpMessageHandler terminal, out ChatClientFactory.CopilotResponsesHandler handler)
    {
        var client = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: true,
            ChatClientFactory.CopilotExtraHighMapping,
            terminal,
            out var copilotHandler,
            FastRetry);

        handler = Assert.IsType<ChatClientFactory.CopilotResponsesHandler>(copilotHandler);
        return client;
    }

    private static HttpRequestMessage ResponsesRequest(string body) =>
        new(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    // ── Internal helpers for cross-class integration tests ───────────────────

    /// <summary>Creates a test client with the fast retry pipeline, exposing the Copilot handler.</summary>
    internal static HttpClient GetClientForExternalUse(
        HttpMessageHandler terminal, out ChatClientFactory.CopilotResponsesHandler handler)
        => CreateClient(terminal, out handler);

    /// <summary>Builds a responses-API request with the given JSON body.</summary>
    internal static HttpRequestMessage ResponsesRequestForExternalUse(string body) =>
        ResponsesRequest(body);

    /// <summary>
    /// A first request that is retried must record the ORIGINAL input as the base context exactly
    /// once, under the response's own id. Before the fix, the retry re-read the (already-transformed)
    /// body and staged the expanded conversation as the base, corrupting every later conversation
    /// reconstruction.
    /// </summary>
    [Fact]
    public async Task FirstRequest_RetriedAfterTransientFailure_BaseInputNotCorrupted()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, terminal.Attempts);

        // The entry committed under the response id must hold exactly the caller's original message.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());

        // Exactly one conversation was recorded despite the three attempts.
        Assert.Equal(1, handler.StoreCountForTest);
    }

    /// <summary>
    /// A follow-up request that is retried must append its input to the turn history exactly once.
    /// Before the fix, state was committed during the transformation, so the input was appended on
    /// the first attempt even when that attempt failed.
    /// </summary>
    [Fact]
    public async Task FollowUpRequest_RetriedAfterTransientFailure_TurnHistoryNotDuplicated()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        // Establish the base context with a clean first request.
        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        // Now a follow-up that fails twice before succeeding.
        terminal.ResetFailures(2);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Exactly one new turn-history entry despite three attempts.
        Assert.True(handler.TryGetConversationStateForTest("resp_2", out _, out var historyAfterFollowUp));
        Assert.Equal(historyAfterFirst.Count + 1, historyAfterFollowUp.Count);
        Assert.Equal("tool-result", historyAfterFollowUp[^1]["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The request body must be transformed exactly once, no matter how many attempts occur.
    /// A non-idempotent transformation would re-expand the input array on every retry.
    /// </summary>
    [Fact]
    public async Task RetriedFollowUp_BodyIsTransformedExactlyOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out _);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(2);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        // Every attempt must have sent an identical body — proof the transformation did not re-run.
        Assert.Equal(3, terminal.Bodies.Count);
        Assert.Single(terminal.Bodies.Distinct());

        var sent = JsonNode.Parse(terminal.Bodies[^1])!;
        Assert.Null(sent["previous_response_id"]);

        // base input (1) + current input (1) — not duplicated per attempt.
        var input = sent["input"]!.AsArray();
        Assert.Equal(2, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[1]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// When every attempt fails, no conversation state may be committed at all — a later successful
    /// request must not inherit phantom history from the failed one.
    /// </summary>
    [Fact]
    public async Task AllAttemptsFail_NoConversationStateIsCommitted()
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.False(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
        Assert.Equal(0, handler.StoreCountForTest);
    }

    /// <summary>
    /// End-to-end proof that the retry fix preserves correct conversation reconstruction: after a
    /// retried first request, a subsequent follow-up must still rebuild the conversation from the
    /// original base input rather than from a corrupted, self-expanded copy.
    /// </summary>
    [Fact]
    public async Task RetriedFirstRequest_LaterFollowUp_ReconstructsConversationCorrectly()
    {
        var terminal = new FailThenSucceedHandler(
            failures: 2, """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out _);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(0);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        var input = JsonNode.Parse(terminal.Bodies[^1])!["input"]!.AsArray();

        // base input (1) + prior response output (1) + current input (1) = 3, in order, no dupes.
        Assert.Equal(3, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The reasoning-effort mapping still applies on the retried attempts — the idempotence marker
    /// must not short-circuit the rest of the chain.
    /// </summary>
    [Fact]
    public async Task RetriedRequest_ExtraHighStillMappedOnEveryAttempt()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out _);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(3, terminal.Bodies.Count);
        Assert.All(terminal.Bodies, body =>
            Assert.Equal("xhigh", JsonNode.Parse(body)!["reasoning"]!["effort"]!.GetValue<string>()));
    }

    /// <summary>
    /// The exact corruption described in review: a retried FOLLOW-UP request. The first attempt
    /// strips <c>previous_response_id</c>; on retry the body no longer contains it, so an
    /// unguarded handler falls into the "first request" branch and stages the fully expanded
    /// conversation as the base input. This asserts the entry committed under the follow-up's own
    /// response id still holds only the original message as its base.
    /// </summary>
    [Fact]
    public async Task RetriedFollowUp_BaseInputIsNotOverwrittenWithExpandedConversation()
    {
        var terminal = new FailThenSucceedHandler(
            failures: 0, """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        terminal.ResetFailures(2);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        // The base input must still be ONLY the original user message — not the expanded conversation.
        Assert.True(handler.TryGetConversationStateForTest("resp_2", out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// A FOLLOW-UP whose every attempt fails must not append anything to the turn history. This is
    /// the test that proves the deferred-commit half of the fix: committing the staged input during
    /// the transformation (rather than after an authoritative response) would leave the failed
    /// request's tool result permanently in the history, poisoning the next successful request.
    /// </summary>
    [Fact]
    public async Task FollowUpWithAllAttemptsFailing_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        terminal.ResetFailures(int.MaxValue);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"doomed-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // Nothing from the failed follow-up may have been recorded — neither a new entry nor an
        // addition to the parent's.
        Assert.False(handler.TryGetConversationStateForTest("resp_2", out _, out _));
        Assert.Equal(1, handler.StoreCountForTest);

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFailure));
        Assert.Equal(historyAfterFirst.Count, historyAfterFailure.Count);
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("doomed-result", StringComparison.Ordinal));
    }

    /// <summary>
    /// A FIRST request whose every attempt fails with a <c>text/event-stream</c> content type must
    /// not commit conversation state. An error reply can carry the streaming content type (the API
    /// echoes the requested stream mode), and the streaming short-circuit previously ran before the
    /// success check, so the failed attempt committed the base input anyway.
    /// </summary>
    [Fact]
    public async Task FailedSseResponse_DoesNotCommitBaseInput()
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"id":"resp_1","output":[]}""")
        {
            FailuresUseSseContentType = true,
        };
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.False(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey,
            out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
        Assert.Equal(0, handler.StoreCountForTest);
    }

    /// <summary>
    /// A FOLLOW-UP whose every attempt fails with an SSE content type must not append its staged
    /// input to the turn history.
    /// </summary>
    [Fact]
    public async Task FailedSseFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        terminal.ResetFailures(int.MaxValue);
        terminal.FailuresUseSseContentType = true;

        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"sse-doomed"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // Neither the streaming slot nor the parent entry may have grown.
        Assert.False(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey, out _, out _));

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFailure));
        Assert.Equal(historyAfterFirst.Count, historyAfterFailure.Count);
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("sse-doomed", StringComparison.Ordinal));
    }

    /// <summary>
    /// An SSE attempt that fails transiently and then SUCCEEDS as SSE must commit exactly once —
    /// the fix must not swing too far and drop state for genuinely successful streaming responses.
    /// A streaming response's id is not observable, so its state lands in the shared legacy slot.
    /// </summary>
    [Fact]
    public async Task FailedThenSuccessfulSseResponse_CommitsStateExactlyOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"id":"resp_1","output":[]}""")
        {
            FailuresUseSseContentType = true,
            SuccessUsesSseContentType = true,
        };
        using var client = CreateClient(terminal, out var handler);

        using (var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The successful SSE body must still pass through untouched for the SDK's parser.
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("event: done",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }

        Assert.Equal(3, terminal.Attempts);

        Assert.True(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey, out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());

        // Exactly one entry — the slot — despite the three attempts.
        Assert.Equal(1, handler.StoreCountForTest);
    }

    /// <summary>
    /// A successful SSE FOLLOW-UP must commit its staged input exactly once, without duplication
    /// across the failed attempts that preceded it.
    /// </summary>
    [Fact]
    public async Task SuccessfulSseFollowUpAfterFailures_CommitsTurnHistoryOnce()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        terminal.ResetFailures(2);
        terminal.FailuresUseSseContentType = true;
        terminal.SuccessUsesSseContentType = true;

        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"sse-result"}]}"""))
        using (var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The SSE follow-up's state lands in the shared legacy slot: the parent's base and history
        // with its own input appended exactly once, despite the failed attempts before it.
        Assert.True(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey,
            out _, out var slotHistory));
        Assert.Equal(historyAfterFirst.Count + 1, slotHistory.Count);
        Assert.Equal("sse-result", slotHistory[^1]["output"]!.GetValue<string>());
    }

    /// <summary>
    /// A failed SSE response must NOT be read to completion. A live error event stream can stay
    /// open indefinitely, so consuming it would block until the timeout and prevent the resilience
    /// handler above from ever observing the failure status and retrying. The unbounded stream here
    /// makes that hang detectable — with a body-consuming implementation this test times out.
    /// </summary>
    /// <remarks>
    /// HTTP media types are case-insensitive and may carry parameters, so every spelling a server
    /// might legitimately send has to take the no-consumption path. A case-sensitive comparison
    /// against the canonical lowercase form leaves every other casing falling through to the
    /// body-reading branch.
    /// </remarks>
    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM")]
    [InlineData("text/event-stream; charset=utf-8")]
    [InlineData("Text/Event-Stream; charset=utf-8")]
    public async Task FailedSseResponse_IsNotReadToCompletion_SoRetriesProceed(string failureContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: 2, """{"id":"resp_1","output":[]}""")
        {
            FailuresUseSseContentType = true,
            UseUnboundedFailureStream = true,
            FailureContentType = failureContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}""");

        // A generous but finite budget: the point is that this completes at all.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await client.SendAsync(request, timeout.Token);

        // Both failed attempts were observed and retried despite their never-ending bodies.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, terminal.Attempts);

        // The successful attempt still committed exactly once. Its response is NOT
        // text/event-stream, so — the response's signal governing the commit — it takes the normal
        // id-keyed path rather than the streaming legacy slot.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal(1, handler.StoreCountForTest);
    }

    /// <summary>
    /// A failed SSE response of any casing must also leave conversation state untouched — the
    /// no-consumption path and the no-commit path must be the same path.
    /// </summary>
    [Theory]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM; charset=utf-8")]
    public async Task FailedSseResponse_MixedCase_DoesNotCommitState(string failureContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: int.MaxValue, """{"output":[]}""")
        {
            FailuresUseSseContentType = true,
            FailureContentType = failureContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey,
            out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
        Assert.Equal(0, handler.StoreCountForTest);
    }

    /// <summary>
    /// A SUCCESSFUL SSE response of any casing must still be recognized as streaming: it must be
    /// handed back unconsumed for the SDK's parser and must commit exactly once.
    /// </summary>
    [Theory]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM; charset=utf-8")]
    public async Task SuccessfulSseResponse_MixedCase_CommitsAndPassesThrough(string successContentType)
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[]}""")
        {
            SuccessUsesSseContentType = true,
            SuccessContentType = successContentType,
        };
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Passed through untouched — not rewritten to application/json.
        Assert.Equal("text/event-stream",
            response.Content.Headers.ContentType?.MediaType, ignoreCase: true);
        Assert.Contains("event: done",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        Assert.True(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey, out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
    }

    /// <summary>
    /// A 2xx whose body is malformed JSON means the operation did not actually complete, so no
    /// conversation state may be committed. Committing before parsing would poison every later
    /// reconstruction with state from a request whose result the caller never received.
    /// </summary>
    [Fact]
    public async Task MalformedSuccessBody_DoesNotCommitConversationState()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"output":[ ,,, truncated""");
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
    }

    /// <summary>
    /// A malformed 2xx on a FOLLOW-UP must not append its staged input to the turn history either.
    /// </summary>
    [Fact]
    public async Task MalformedSuccessBodyOnFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        terminal.SuccessBody = """{"output":[ ,,, truncated""";

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"malformed-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        // No new entry, and the parent entry is untouched.
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFailure));
        Assert.Equal(historyAfterFirst.Count, historyAfterFailure.Count);
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("malformed-doomed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 2xx body that parses syntactically but is structurally invalid — here a null element in
    /// the <c>output</c> array — must not commit conversation state. Syntactic parsing alone is not
    /// enough to call the exchange complete: processing the output can still fail, and committing
    /// beforehand would leave the caller observing an error while later requests inherit poisoned
    /// state.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"resp_1","output":[null]}""")]
    [InlineData("""{"id":"resp_1","output":[{"type":"message","content":"ok"},null]}""")]
    [InlineData("""{"id":"resp_1","output":[null,{"type":"message","content":"ok"}]}""")]
    public async Task StructurallyInvalidSuccessBody_DoesNotCommitConversationState(string successBody)
    {
        var terminal = new FailThenSucceedHandler(failures: 0, successBody);
        using var client = CreateClient(terminal, out var handler);

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
    }

    /// <summary>
    /// A structurally invalid 2xx on a FOLLOW-UP must not append its staged input to the turn
    /// history, and must not partially append response output either. The previous implementation
    /// added output items one at a time, so a null element midway through left earlier items
    /// already committed.
    /// </summary>
    [Fact]
    public async Task StructurallyInvalidSuccessBodyOnFollowUp_DoesNotPartiallyCommit()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        // A well-formed leading item followed by a null: the old loop committed the first item
        // before throwing on the second.
        terminal.SuccessBody = """{"id":"resp_2","output":[{"type":"message","content":"leaked"},null]}""";

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"structural-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        // No entry at all for the failed exchange, and the parent's is unchanged.
        Assert.False(handler.TryGetConversationStateForTest("resp_2", out _, out _));
        Assert.Equal(1, handler.StoreCountForTest);

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFailure));
        Assert.Equal(historyAfterFirst.Count, historyAfterFailure.Count);
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("structural-doomed", StringComparison.Ordinal));
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("leaked", StringComparison.Ordinal));
    }

    /// <summary>
    /// A structural failure must leave state clean enough that a later well-formed request still
    /// reconstructs the conversation correctly — proving nothing was silently poisoned.
    /// </summary>
    [Fact]
    public async Task AfterStructuralFailure_LaterRequestReconstructsCorrectly()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_0","output":[null]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var doomed = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"doomed"}]}"""))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(doomed, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest("resp_0", out _, out _));

        // A fresh, well-formed first request establishes the base context cleanly.
        terminal.SuccessBody = """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""";
        using (var good = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"real-first"}]}"""))
        using (await client.SendAsync(good, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("real-first", baseInput[0]!["content"]!.GetValue<string>());

        // And the follow-up reconstructs base + response output + current input, with no residue
        // from the structurally invalid exchange.
        terminal.ResetFailures(0);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(followUp, TestContext.Current.CancellationToken)) { }

        var input = JsonNode.Parse(terminal.Bodies[^1])!["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.Equal("real-first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());
    }

    /// <summary>
    /// The conversation-state commit must be the FINAL step, after response-content replacement.
    /// If replacement fails, the exchange has not been fully processed, so nothing may be committed
    /// — the caller sees the error with state exactly as it was.
    /// </summary>
    /// <remarks>
    /// This test fails if the commit is moved back before response-content replacement: the commit
    /// would then run before the forced replacement failure and leave state behind.
    /// </remarks>
    [Fact]
    public async Task ResponseContentReplacementThrows_DoesNotCommitConversationState()
    {
        var terminal = new FailThenSucceedHandler(
            failures: 0, """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        handler.ResponseContentFactory = _ => throw new InvalidOperationException("content replacement failed");

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("content replacement failed", ex.Message);

        // Neither the staged request state nor the accumulated response output may be present.
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
    }

    /// <summary>
    /// The same guarantee on a FOLLOW-UP: a failed response-content replacement must not append the
    /// staged input or the response output to the turn history.
    /// </summary>
    [Fact]
    public async Task ResponseContentReplacementThrowsOnFollowUp_DoesNotCommitTurnHistory()
    {
        var terminal = new FailThenSucceedHandler(failures: 0, """{"id":"resp_1","output":[]}""");
        using var client = CreateClient(terminal, out var handler);

        using (var first = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFirst));

        terminal.SuccessBody =
            """{"id":"resp_2","output":[{"type":"message","content":"replacement-leaked"}]}""";
        handler.ResponseContentFactory = _ => throw new InvalidOperationException("content replacement failed");

        using var followUp = ResponsesRequest(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"replacement-doomed"}]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(followUp, TestContext.Current.CancellationToken));

        Assert.False(handler.TryGetConversationStateForTest("resp_2", out _, out _));
        Assert.Equal(1, handler.StoreCountForTest);

        Assert.True(handler.TryGetConversationStateForTest("resp_1", out _, out var historyAfterFailure));
        Assert.Equal(historyAfterFirst.Count, historyAfterFailure.Count);
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("replacement-doomed", StringComparison.Ordinal));
        Assert.DoesNotContain(historyAfterFailure,
            n => n.ToJsonString().Contains("replacement-leaked", StringComparison.Ordinal));
    }

    /// <summary>
    /// Directly observes the ordering: at the moment response-content replacement runs, the durable
    /// conversation state must still be untouched. This pins the sequence rather than only the
    /// failure behaviour, so moving the commit earlier is caught even when nothing throws.
    /// </summary>
    [Fact]
    public async Task CommitHappensAfterResponseContentReplacement()
    {
        var terminal = new FailThenSucceedHandler(
            failures: 0, """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""");
        using var client = CreateClient(terminal, out var handler);

        int? storeCountAtReplacement = null;
        int? baseInputCountAtReplacement = null;
        int? turnHistoryCountAtReplacement = null;

        var realFactory = handler.ResponseContentFactory;
        handler.ResponseContentFactory = body =>
        {
            storeCountAtReplacement = handler.StoreCountForTest;
            handler.TryGetConversationStateForTest("resp_1", out var atReplacement, out var historyAtReplacement);
            baseInputCountAtReplacement = atReplacement?.Count ?? -1;
            turnHistoryCountAtReplacement = historyAtReplacement.Count;
            return realFactory(body);
        };

        using var request = ResponsesRequest(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Replacement ran, and at that point nothing had been committed yet.
        Assert.Equal(0, storeCountAtReplacement);
        Assert.Equal(-1, baseInputCountAtReplacement);
        Assert.Equal(0, turnHistoryCountAtReplacement);

        // After the exchange completes, the commit has happened exactly once.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());
        Assert.Single(turnHistory);
        Assert.Equal("answer", turnHistory[0]["content"]!.GetValue<string>());
        Assert.Equal(1, handler.StoreCountForTest);

        // The body still reached the caller intact.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Terminal handler that returns a configurable number of retryable failures before succeeding,
    /// recording the body of every attempt.
    /// </summary>
    internal sealed class FailThenSucceedHandler : HttpMessageHandler
    {
        private int _remainingFailures;

        public FailThenSucceedHandler(int failures, string successBody)
        {
            _remainingFailures = failures;
            SuccessBody = successBody;
        }

        /// <summary>The body returned once the failure budget is exhausted.</summary>
        public string SuccessBody { get; set; }

        public List<string> Bodies { get; } = new();
        public int Attempts => Bodies.Count;

        /// <summary>
        /// When set, failure responses are returned with a <c>text/event-stream</c> content type,
        /// reproducing an API that echoes the requested stream mode on an error reply.
        /// </summary>
        public bool FailuresUseSseContentType { get; set; }

        /// <summary>When set, the success response is returned as an SSE stream.</summary>
        public bool SuccessUsesSseContentType { get; set; }

        /// <summary>
        /// When set alongside <see cref="FailuresUseSseContentType"/>, failure bodies are backed by a
        /// stream that never completes — modelling a live error event stream the server holds open.
        /// </summary>
        public bool UseUnboundedFailureStream { get; set; }

        /// <summary>
        /// The exact <c>Content-Type</c> header used for SSE failure bodies. Defaults to the
        /// canonical lowercase form; tests override it to prove casing and parameters are handled.
        /// </summary>
        public string FailureContentType { get; set; } = "text/event-stream";

        /// <summary>The exact <c>Content-Type</c> header used for SSE success bodies.</summary>
        public string SuccessContentType { get; set; } = "text/event-stream";

        /// <summary>Resets the failure budget and the recorded bodies for the next logical request.</summary>
        public void ResetFailures(int failures)
        {
            _remainingFailures = failures;
            Bodies.Clear();
        }

        /// <summary>
        /// Builds content whose <c>Content-Type</c> is set verbatim from <paramref name="contentType"/>,
        /// preserving casing and parameters exactly as a server would send them.
        /// <see cref="StringContent"/>'s constructor normalizes the media type, so the header is
        /// assigned directly instead.
        /// </summary>
        private static HttpContent SseStringContent(string body, string contentType)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return content;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            if (_remainingFailures > 0)
            {
                if (_remainingFailures != int.MaxValue) _remainingFailures--;

                HttpContent failureContent = FailuresUseSseContentType
                    ? (UseUnboundedFailureStream
                        ? NeverEndingStreamContent.CreateSse(FailureContentType)
                        : SseStringContent("event: error\ndata: {}\n\n", FailureContentType))
                    : new StringContent("""{"error":"transient"}""", Encoding.UTF8, "application/json");

                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = failureContent,
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = SuccessUsesSseContentType
                    ? SseStringContent("event: done\ndata: {}\n\n", SuccessContentType)
                    : new StringContent(SuccessBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// SSE content backed by a stream that never completes, modelling a real error event stream the
    /// server holds open. Reading it to completion blocks forever, so any handler that does so is
    /// caught by a test timeout instead of silently working against finite <c>StringContent</c>.
    /// </summary>
    internal sealed class NeverEndingStreamContent : HttpContent
    {
        private readonly BlockingForeverStream _stream = new();

        public static NeverEndingStreamContent CreateSse(string contentType = "text/event-stream")
        {
            var content = new NeverEndingStreamContent();
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return content;
        }

        /// <summary>Set once anything attempts to read the body to completion.</summary>
        public bool ReadAttempted => _stream.ReadAttempted;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            // Unknown length, exactly like a live event stream.
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override void Dispose(bool disposing)
        {
            _stream.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>A readable stream whose reads never return, until disposed.</summary>
        private sealed class BlockingForeverStream : Stream
        {
            private readonly SemaphoreSlim _blocked = new(0, 1);

            public bool ReadAttempted { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadAttempted = true;
                // Blocks until cancelled or disposed — never yields data, never reports EOF.
                await _blocked.WaitAsync(cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadAttempted = true;
                _blocked.Wait();
                return 0;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _blocked.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

/// <summary>
/// Unit tests for <see cref="OllamaExtraHighReasoningClient"/>'s options handling, complementing the
/// real-path wiring tests that drive it through <see cref="OllamaSharp.OllamaApiClient"/>.
/// </summary>
public sealed class OllamaExtraHighReasoningClientTests
{
    /// <summary>
    /// Resolves the <c>Think</c> value that the composed <see cref="ChatOptions.RawRepresentationFactory"/>
    /// will hand to OllamaSharp, invoking it exactly as OllamaSharp does.
    /// </summary>
    private static string? ResolveThink(ChatOptions options, IChatClient suppliedClient)
        => (options.RawRepresentationFactory?.Invoke(suppliedClient)
            as OllamaSharp.Models.Chat.ChatRequest)?.Think?.ToString();

    /// <summary>ExtraHigh installs a factory that supplies <c>Think = max</c>.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExtraHigh_AddsThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.NotSame(options, applied);
        Assert.Equal("max", ResolveThink(applied, inner));

        // The caller's options are untouched: no factory installed, no additional properties added.
        Assert.Null(options.RawRepresentationFactory);
        Assert.Null(options.AdditionalProperties);
    }

    /// <summary>Every other effort (and null options) is forwarded untouched, without cloning.</summary>
    [Theory]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.None)]
    public void ApplyExtraHighThink_OtherEfforts_ReturnOriginalInstance(ReasoningEffort effort)
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } };

        Assert.Same(options, client.ApplyExtraHighThink(options));
    }

    /// <summary>Null and reasoning-less options are returned unchanged.</summary>
    [Fact]
    public void ApplyExtraHighThink_NullOrReasoninglessOptions_AreUnchanged()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        Assert.Null(client.ApplyExtraHighThink(null));

        var noReasoning = new ChatOptions();
        Assert.Same(noReasoning, client.ApplyExtraHighThink(noReasoning));

        var emptyReasoning = new ChatOptions { Reasoning = new ReasoningOptions() };
        Assert.Same(emptyReasoning, client.ApplyExtraHighThink(emptyReasoning));
    }

    /// <summary>An explicit caller-supplied <c>think</c> value always wins.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExplicitThink_IsPreserved()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false };

        Assert.Same(options, client.ApplyExtraHighThink(options));
    }

    /// <summary>Existing additional properties survive alongside the injected <c>think</c>.</summary>
    [Fact]
    public void ApplyExtraHighThink_PreservesOtherAdditionalProperties()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["keep_alive"] = "5m" };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.Equal("max", ResolveThink(applied, inner));
        Assert.Equal("5m", applied.AdditionalProperties!["keep_alive"]);

        // The caller's own dictionary is never touched.
        Assert.False(options.AdditionalProperties.ContainsKey("think"));
        Assert.Null(options.RawRepresentationFactory);
    }

    /// <summary><c>Metadata</c>, <c>GetService</c> and <c>Dispose</c> forward to the inner client.</summary>
    [Fact]
    public void MetadataGetServiceAndDispose_ForwardToInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var client = new OllamaExtraHighReasoningClient(inner);

        Assert.Same(inner.Metadata, client.Metadata);
        Assert.Same(inner.Metadata, client.GetService(typeof(ChatClientMetadata)));

        client.Dispose();
        Assert.True(inner.Disposed);
    }

    /// <summary>
    /// The wrapper disposes an owned transport in addition to the inner client. OllamaSharp only
    /// disposes an HTTP client it created itself, so without this the factory-created transport
    /// (and its handler chain and sockets) would leak.
    /// </summary>
    [Fact]
    public void Dispose_DisposesOwnedTransport()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var transport = new DisposalTrackingDisposable();
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        client.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(transport.Disposed);
    }

    /// <summary>
    /// A failure while disposing the inner client must not prevent the owned transport from being
    /// disposed, and the original exception must still surface.
    /// </summary>
    [Fact]
    public void Dispose_InnerThrows_StillDisposesTransportAndPropagates()
    {
        var inner = new ThrowingDisposalChatClient();
        var transport = new DisposalTrackingDisposable();
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<InvalidOperationException>(client.Dispose);

        Assert.Equal("inner disposal failed", ex.Message);
        Assert.True(transport.Disposed);
    }

    /// <summary>
    /// A transport-only failure must still surface when the inner client disposes cleanly.
    /// </summary>
    [Fact]
    public void Dispose_TransportThrows_PropagatesAndStillDisposesInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var transport = new ThrowingDisposable("transport disposal failed");
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<InvalidOperationException>(client.Dispose);

        Assert.Equal("transport disposal failed", ex.Message);
        Assert.True(inner.Disposed);
    }

    /// <summary>
    /// When BOTH cleanups fail, the original (inner) exception must not be lost. A plain
    /// <c>try/finally</c> would let the transport's exception replace and hide it; the failures are
    /// aggregated instead, with the primary error first.
    /// </summary>
    [Fact]
    public void Dispose_BothThrow_PreservesOriginalException()
    {
        var inner = new ThrowingDisposalChatClient();
        var transport = new ThrowingDisposable("transport disposal failed");
        var client = new OllamaExtraHighReasoningClient(inner, transport);

        var ex = Assert.Throws<AggregateException>(client.Dispose);

        Assert.Equal(2, ex.InnerExceptions.Count);

        // The inner client's failure is the primary one and comes first.
        Assert.Equal("inner disposal failed", ex.InnerExceptions[0].Message);
        Assert.Equal("transport disposal failed", ex.InnerExceptions[1].Message);
    }

    /// <summary>A disposable whose disposal always throws.</summary>
    private sealed class ThrowingDisposable : IDisposable
    {
        private readonly string _message;

        public ThrowingDisposable(string message) => _message = message;

        public void Dispose() => throw new InvalidOperationException(_message);
    }

    /// <summary>When no transport is owned, disposal is limited to the inner client.</summary>
    [Fact]
    public void Dispose_WithoutOwnedTransport_DisposesOnlyInner()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        var client = new OllamaExtraHighReasoningClient(inner);

        client.Dispose();

        Assert.True(inner.Disposed);
    }

    /// <summary>An explicit raw-representation <c>Think</c> is never overridden.</summary>
    [Fact]
    public void ApplyExtraHighThink_ExplicitRawThink_IsPreserved()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.Equal("low", ResolveThink(applied, inner));
    }

    /// <summary>A raw representation without <c>Think</c> gets the injection.</summary>
    [Fact]
    public void ApplyExtraHighThink_RawRepresentationWithoutThink_StillInjects()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { KeepAlive = "9m" },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        Assert.NotSame(options, applied);

        var request = Assert.IsType<OllamaSharp.Models.Chat.ChatRequest>(
            applied.RawRepresentationFactory!(inner));
        Assert.Equal("max", request.Think?.ToString());
        Assert.Equal("9m", request.KeepAlive);
    }

    /// <summary>
    /// The composed factory must invoke the caller's factory exactly once per request. The previous
    /// implementation called it once for inspection and let OllamaSharp call it again, so a stateful
    /// or non-deterministic factory could expose one <c>Think</c> during inspection and another
    /// during serialization.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_InvokesCallerFactoryExactlyOncePerRequest()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        var invocations = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ =>
            {
                invocations++;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        // Building the options must not have run caller code at all.
        Assert.Equal(0, invocations);

        // One serialization pass == exactly one caller-factory invocation.
        applied.RawRepresentationFactory!(inner);
        Assert.Equal(1, invocations);
    }

    /// <summary>
    /// The composed factory forwards the client OllamaSharp supplies, not this decorator, so a
    /// caller factory that inspects the client sees exactly what it would without the wrapper.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_ForwardsSuppliedClientToCallerFactory()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        IChatClient? observed = null;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = c =>
            {
                observed = c;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;
        applied.RawRepresentationFactory!(inner);

        Assert.Same(inner, observed);
        Assert.NotSame(client, observed);
    }

    /// <summary>
    /// A caller factory whose result varies between invocations must still have its ACTUAL
    /// serialized value respected. Under the old inspect-then-rebuild approach the first call
    /// (inspection) decided the outcome while the second call (serialization) produced the request,
    /// so the decision could be based on a value that was never sent.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_NonDeterministicFactory_RespectsTheServializedValue()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        var call = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            // First invocation reports no Think, every later one reports an explicit "low".
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = call++ == 0
                    ? (OllamaSharp.Models.Chat.ThinkValue?)null
                    : new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        var applied = client.ApplyExtraHighThink(options)!;

        // The single serialization invocation is the first call, which has no explicit Think,
        // so "max" is injected onto the very object that gets sent.
        Assert.Equal("max", ResolveThink(applied, inner));
        Assert.Equal(1, call);

        // A subsequent request reports an explicit "low", which must be preserved verbatim.
        Assert.Equal("low", ResolveThink(applied, inner));
        Assert.Equal(2, call);
    }

    /// <summary>
    /// A factory returning an object the mapper cannot use is replaced with a request carrying
    /// <c>Think = max</c>. OllamaSharp discards non-<c>ChatRequest</c> results outright, so passing
    /// one through would forfeit the mapping for no benefit.
    /// </summary>
    [Fact]
    public void ApplyExtraHighThink_UnusableFactoryResult_StillSuppliesThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using var client = new OllamaExtraHighReasoningClient(inner);

        foreach (var factory in new Func<IChatClient, object?>[] { _ => null, _ => "not-a-chat-request" })
        {
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                RawRepresentationFactory = factory!,
            };

            Assert.Equal("max", ResolveThink(client.ApplyExtraHighThink(options)!, inner));
        }
    }

    /// <summary>Tracks disposal so ownership transfer can be asserted.</summary>
    private sealed class DisposalTrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>An inner client whose disposal throws, to prove cleanup still completes.</summary>
    private sealed class ThrowingDisposalChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => throw new InvalidOperationException("inner disposal failed");
    }

    /// <summary>The streaming entry point applies the same interception.</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_AppliesThinkMax()
    {
        var inner = new ReasoningEffortClampingClientTests.RecordingChatClient();
        using IChatClient client = new OllamaExtraHighReasoningClient(inner);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal("max", ResolveThink(inner.LastOptions!, inner));
    }
}

/// <summary>
/// Additional integration tests for the retry-2 iteration: Ollama cloud-style construction
/// and multi-turn conversation reconstruction after retry.
/// </summary>
public sealed class ReasoningEffortRetry2IntegrationTests
{
    /// <summary>
    /// The Ollama real path must work with a cloud-style base address (https://ollama.com) as
    /// well as the local address. The <c>think</c> mapping is address-independent, but this
    /// confirms the <c>OllamaApiClient</c> → <c>OllamaExtraHighReasoningClient</c> stack
    /// doesn't depend on the local <c>/api/chat</c> endpoint shape.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_CloudBaseAddress_ExtraHigh_TerminalReceivesThinkMax()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        // Build the same stack as CreateOllamaClientForTest but with a cloud-style base address.
        var httpClient = new HttpClient(ChatClientFactory.CreateResilientHandler(
            ChatClientFactory.BuildHandlerChain(
                new ReasoningEffortMappingHandler(
                    ChatClientFactory.OllamaExtraHighMapping,
                    customPropertyName: ChatClientFactory.OllamaReasoningPropertyName),
                terminal)))
        {
            BaseAddress = new Uri("https://ollama.com"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ollamaClient = new OllamaApiClient(httpClient);
        ollamaClient.SelectedModel = "gpt-oss:120b";
        using var client = new OllamaExtraHighReasoningClient(ollamaClient);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            TestContext.Current.CancellationToken);

        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// Multi-turn conversation reconstruction after a retry on the first request: after the
    /// initial retried request succeeds and accumulates response output, a second follow-up
    /// request must reconstruct the conversation as base + response output + new input (exactly
    /// 3 items), proving no corruption from the retry.
    /// </summary>
    [Fact]
    public async Task MultiTurnConversation_RetryOnFirstRequest_SecondTurnReconstructsCorrectly()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 2, """{"id":"resp_1","output":[{"type":"message","content":"answer1"}]}""");

        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(
            terminal, out var handler);

        // First request: fails twice, then succeeds.
        using (var first = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(first, TestContext.Current.CancellationToken)) { }

        // Verify base input was committed correctly under the response id despite the retry.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());

        // The response output "answer1" should have been committed to turn history.
        Assert.Contains(turnHistory,
            n => n.ToJsonString().Contains("answer1", StringComparison.Ordinal));

        // Second request: no failures, must reconstruct conversation correctly.
        terminal.ResetFailures(0);
        terminal.SuccessBody = """{"id":"resp_2","output":[]}""";
        using (var second = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"function_call_output","output":"tool-result"}]}"""))
        using (await client.SendAsync(second, TestContext.Current.CancellationToken)) { }

        var sent = JsonNode.Parse(terminal.Bodies[^1])!;
        var input = sent["input"]!.AsArray();

        // base (1) + response output (1) + current input (1) = 3, no duplicates from retry.
        Assert.Equal(3, input.Count);
        Assert.Equal("first", input[0]!["content"]!.GetValue<string>());
        Assert.Equal("answer1", input[1]!["content"]!.GetValue<string>());
        Assert.Equal("tool-result", input[2]!["output"]!.GetValue<string>());

        // previous_response_id must be stripped on the second request too.
        Assert.Null(sent["previous_response_id"]);

        client.Dispose();
    }
}

/// <summary>
/// Additional integration tests for the retry-3 iteration: failed-SSE removal-proof,
/// real-path transport disposal through the full production stack, and RawRepresentationFactory
/// streaming-path coverage.
/// </summary>
public sealed class ReasoningEffortRetry3IntegrationTests
{
    /// <summary>
    /// REMOVAL-PROOF: a failed SSE response with a follow-up that is also SSE must not corrupt
    /// the base input. This test proves the fix by sending a first request whose every attempt
    /// returns 500 with text/event-stream, then a second clean request and verifying the base
    /// input is from the SECOND request only (not the failed first).
    /// </summary>
    [Fact]
    public async Task FailedSseFirstRequest_SecondCleanRequest_BaseInputFromSecondOnly()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: int.MaxValue, """{"id":"resp_1","output":[]}""")
        {
            FailuresUseSseContentType = true,
        };

        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out var handler);

        // First request: all attempts fail with SSE content type.
        using (var first = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"doomed-first"}]}"""))
        using (var response = await client.SendAsync(first, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // No state should have been committed from the failed first request — not even the slot.
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest(
            ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey,
            out var failedBaseInput, out var failedTurnHistory));
        Assert.Null(failedBaseInput);
        Assert.Empty(failedTurnHistory);

        // Second request: succeeds cleanly (non-SSE).
        terminal.ResetFailures(0);
        terminal.FailuresUseSseContentType = false;
        using (var second = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"clean-second"}]}"""))
        using (await client.SendAsync(second, TestContext.Current.CancellationToken)) { }

        // The only recorded entry is the second request's, keyed by its response id.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out _));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("clean-second", baseInput[0]!["content"]!.GetValue<string>());
        Assert.Equal(1, handler.StoreCountForTest);

        client.Dispose();
    }

    /// <summary>
    /// REAL-PATH disposal: disposing a factory-created Ollama client (via
    /// <see cref="ChatClientFactory.CreateOllamaClientForTest"/>) must dispose the entire injected
    /// transport chain including the terminal handler. This proves no socket/transport leak.
    /// The test verifies the terminal handler's Disposed flag is set after client disposal.
    /// </summary>
    [Fact]
    public void OllamaRealPath_DisposingFactoryClient_DisposesEntireTransportChain()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        Assert.False(terminal.Disposed);

        client.Dispose();

        // The entire chain (OllamaExtraHighReasoningClient → OllamaApiClient → HttpClient →
        // ResilienceHandler → ReasoningEffortMappingHandler → terminal) must be disposed.
        Assert.True(terminal.Disposed);
    }

    /// <summary>
    /// REAL-PATH streaming with RawRepresentationFactory: the streaming entry point must also
    /// respect an explicit raw Think value, not just the non-streaming path.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_Streaming_RawRepresentationThink_IsNotOverridden()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken))
        {
            // drain
        }

        Assert.Equal("low", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }
}

/// <summary>
/// Additional integration tests for the retry-4 iteration: well-formed non-SSE success commit,
/// real-path single factory invocation, and real-path non-deterministic factory.
/// </summary>
public sealed class ReasoningEffortRetry4IntegrationTests
{
    /// <summary>
    /// A well-formed non-SSE 2xx response must commit both base input and response output to turn
    /// history exactly once. This complements the malformed-success tests by proving the happy path
    /// still commits — the fix must not swing too far and drop state for valid responses.
    /// </summary>
    [Fact]
    public async Task WellFormedNonSseSuccess_CommitsBaseInputAndTurnHistoryExactlyOnce()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 0, """{"id":"resp_1","output":[{"type":"message","content":"answer"}]}""");
        var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out var handler);

        using (var request = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"first"}]}"""))
        using (await client.SendAsync(request, TestContext.Current.CancellationToken)) { }

        // Base input committed from the request, under the response's own id.
        Assert.True(handler.TryGetConversationStateForTest("resp_1", out var baseInput, out var turnHistory));
        Assert.NotNull(baseInput);
        Assert.Single(baseInput);
        Assert.Equal("first", baseInput[0]!["content"]!.GetValue<string>());

        // Response output committed to turn history (the "answer" message).
        Assert.Contains(turnHistory,
            n => n.ToJsonString().Contains("answer", StringComparison.Ordinal));
        Assert.Single(turnHistory);
        Assert.Equal(1, handler.StoreCountForTest);

        client.Dispose();
    }

    /// <summary>
    /// REAL-PATH: the composed RawRepresentationFactory must be invoked exactly once through the
    /// full OllamaApiClient stack. The old implementation invoked it once for inspection and
    /// OllamaSharp invoked it again for serialization, so a stateful factory could observe two calls.
    /// This test drives the real path and asserts the caller's factory is invoked exactly once.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_ComposedFactory_InvokedExactlyOnce()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var invocations = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ =>
            {
                invocations++;
                return new OllamaSharp.Models.Chat.ChatRequest();
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        Assert.Equal(1, invocations);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }

    /// <summary>
    /// REAL-PATH: a non-deterministic factory whose first invocation returns no Think and whose
    /// second returns an explicit "low" must have the first call's result serialized (with max
    /// injected), not the second. Under the old inspect-then-rebuild approach the inspection call
    /// (first) would decide "no Think → inject max" while the serialization call (second) would
    /// produce a request with Think="low", and the additional-property would overwrite it to "max".
    /// With composition, only one call happens, so the result is consistent.
    /// </summary>
    [Fact]
    public async Task OllamaRealPath_NonDeterministicFactory_SingleInvocationProducesMax()
    {
        var terminal = new ReasoningEffortWiringTests.OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        var call = 0;
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest
            {
                Think = call++ == 0
                    ? (OllamaSharp.Models.Chat.ThinkValue?)null
                    : new OllamaSharp.Models.Chat.ThinkValue("low"),
            },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], options, TestContext.Current.CancellationToken);

        // Only one invocation happened, and it returned no Think, so max was injected.
        Assert.Equal(1, call);
        Assert.Equal("max", JsonNode.Parse(terminal.LastBody!)!["think"]!.GetValue<string>());
    }
}

/// <summary>
/// Spec-driven tests for <see cref="ChatClientFactory.CopilotResponsesHandler"/>'s
/// per-conversation state store: the FRESH/CONTINUATION/non-array staging rules, the id-keyed
/// commit, deep-clone isolation, the bounded FIFO, the uniform id validation, and the streaming
/// legacy slot.
/// </summary>
/// <remarks>
/// <para>
/// Everything is asserted through the internal seams (<c>TryGetConversationStateForTest</c>,
/// <c>StoreCountForTest</c>, <c>InsertionOrderForTest</c>) plus the bodies the terminal handler
/// actually received, so each test pins a specific entry's exact contents, order and multiplicity
/// rather than merely "something was recorded".
/// </para>
/// <para>
/// Concurrency is forced with an async barrier, never with <c>Task.Delay</c>: the gated terminal
/// holds every participant inside the handler until all of them have arrived, so the interleaving
/// is deterministic and a state-bleeding implementation cannot pass by luck.
/// </para>
/// </remarks>
public sealed class CopilotResponsesHandlerConversationStoreTests
{
    private const string SlotKey = ChatClientFactory.CopilotResponsesHandler.StreamingLegacySlotKey;

    // ── Shared plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// Reduces a conversation item to the single string that identifies it in these tests, so
    /// assertions can compare exact sequences (which items, in what order, how many times).
    /// </summary>
    private static string MarkerOf(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["content"] is JsonValue content && content.TryGetValue<string>(out var contentText))
                return contentText;
            if (obj["output"] is JsonValue output && output.TryGetValue<string>(out var outputText))
                return outputText;
        }

        return node?.ToJsonString() ?? "<null>";
    }

    private static string[] ArrayMarkers(JsonArray? array)
        => array is null ? Array.Empty<string>() : array.Select(MarkerOf).ToArray();

    private static string[] ListMarkers(IReadOnlyList<JsonNode> items)
        => items.Select(n => MarkerOf(n)).ToArray();

    /// <summary>The <c>BaseInput</c> of the entry under <paramref name="id"/>, as markers.</summary>
    private static string[] BaseMarkers(ChatClientFactory.CopilotResponsesHandler handler, string id)
    {
        Assert.True(handler.TryGetConversationStateForTest(id, out var baseInput, out _),
            $"expected an entry under '{id}'");
        return ArrayMarkers(baseInput);
    }

    /// <summary>The <c>TurnHistory</c> of the entry under <paramref name="id"/>, as markers.</summary>
    private static string[] HistoryMarkers(ChatClientFactory.CopilotResponsesHandler handler, string id)
    {
        Assert.True(handler.TryGetConversationStateForTest(id, out _, out var history),
            $"expected an entry under '{id}'");
        return ListMarkers(history);
    }

    /// <summary>How often <paramref name="marker"/> occurs across the WHOLE entry (base + history).</summary>
    private static int OccurrencesIn(ChatClientFactory.CopilotResponsesHandler handler, string id, string marker)
        => BaseMarkers(handler, id).Concat(HistoryMarkers(handler, id)).Count(m => m == marker);

    /// <summary>The <c>input</c> array of a request body, as markers.</summary>
    private static string[] SentInputMarkers(string body)
        => ArrayMarkers(JsonNode.Parse(body)!["input"] as JsonArray);

    /// <summary>Sends one responses-API request and requires it to succeed.</summary>
    private static async Task SendAsync(HttpClient client, string body)
    {
        using var request = CopilotResponsesHandlerRetryTests.ResponsesRequestForExternalUse(body);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string UserInput(string marker)
        => "[{\"type\":\"message\",\"role\":\"user\",\"content\":\"" + marker + "\"}]";

    private static string ToolInput(string marker)
        => "[{\"type\":\"function_call_output\",\"output\":\"" + marker + "\"}]";

    /// <summary>Builds a responses-API request body with the given raw <c>input</c> JSON.</summary>
    private static string Request(string rawInput, string? previousResponseId = null, bool stream = false)
    {
        var previous = previousResponseId is null
            ? string.Empty
            : "\"previous_response_id\":" + previousResponseId + ",";
        var streaming = stream ? "\"stream\":true," : string.Empty;
        return "{\"model\":\"gpt-5\"," + streaming + previous + "\"input\":" + rawInput + "}";
    }

    /// <summary>A well-formed JSON response body with a valid id and the given output markers.</summary>
    private static string JsonReply(string id, params string[] outputMarkers)
    {
        var outputs = string.Join(",",
            outputMarkers.Select(m => "{\"type\":\"message\",\"content\":\"" + m + "\"}"));
        return "{\"id\":\"" + id + "\",\"output\":[" + outputs + "]}";
    }

    /// <summary>
    /// Terminal handler driven by an explicit script of replies. An unscripted request throws
    /// rather than falling back to a default, so a test that sends more (or fewer) requests than it
    /// intended fails loudly instead of silently asserting against the wrong exchange.
    /// </summary>
    private sealed class ScriptedTerminal : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly Queue<KeyValuePair<string, string>> _replies = new();

        public List<string> Bodies { get; } = new();

        public string LastBody
        {
            get { lock (_lock) { return Bodies[^1]; } }
        }

        public ScriptedTerminal Json(string body)
        {
            lock (_lock) { _replies.Enqueue(new KeyValuePair<string, string>(body, "application/json")); }
            return this;
        }

        public ScriptedTerminal Sse(string body = "event: done\ndata: {}\n\n")
        {
            lock (_lock) { _replies.Enqueue(new KeyValuePair<string, string>(body, "text/event-stream")); }
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct);

            KeyValuePair<string, string> reply;
            lock (_lock)
            {
                Bodies.Add(requestBody);
                if (_replies.Count == 0)
                    throw new InvalidOperationException(
                        $"ScriptedTerminal received an unscripted request #{Bodies.Count}: {requestBody}");
                reply = _replies.Dequeue();
            }

            var content = new StringContent(reply.Key, Encoding.UTF8);
            content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(reply.Value);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            };
        }
    }

    private static HttpClient CreateClient(
        ScriptedTerminal terminal, out ChatClientFactory.CopilotResponsesHandler handler)
        => CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out handler);

    // ── (a) New conversation ─────────────────────────────────────────────────

    /// <summary>
    /// A first request with array input plus a valid response id creates a FRESH entry: the request
    /// input becomes the <c>BaseInput</c> exactly once, the staged history is EMPTY, and only the
    /// response's <c>output</c> items land in the turn history.
    /// </summary>
    /// <remarks>
    /// The "the input is not ALSO in the history" assertion is what distinguishes FRESH staging
    /// from CONTINUATION staging: staging the input into the history as well would make the history
    /// two items long and would duplicate the input inside the entry.
    /// </remarks>
    [Fact]
    public async Task FirstRequest_WithValidResponseId_StagesInputAsBaseOnlyAndAppendsOutput()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_a", "answer"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        // Exactly one conversation, keyed by the response's own id.
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { "resp_a" }, handler.InsertionOrderForTest);

        // The request input is the base — once — and the staged history was empty, so the history
        // holds only the response output.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
        Assert.Equal(1, OccurrencesIn(handler, "resp_a", "first"));
    }

    /// <summary>
    /// Every response <c>output</c> item is appended, in order, after the (empty) staged history.
    /// </summary>
    [Fact]
    public async Task FirstRequest_AppendsEveryOutputItemInOrder()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_a", "out1", "out2", "out3"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "out1", "out2", "out3" }, HistoryMarkers(handler, "resp_a"));
    }

    // ── (b) The FRESH / CONTINUATION distinction ─────────────────────────────

    /// <summary>
    /// A follow-up naming its parent's response id composes the new entry as the parent's base +
    /// the parent's history + the CURRENT input appended once + the new response's output. The base
    /// must not grow, and the current input must occur exactly once in the whole entry.
    /// </summary>
    [Fact]
    public async Task FollowUp_AppendsCurrentInputToHistoryExactlyOnce_AndLeavesBaseUnchanged()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_b", "answer2"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request(ToolInput("tool-result"), previousResponseId: "\"resp_a\""));

        // The sent body carries the reconstruction, with the id stripped and nothing duplicated.
        Assert.Null(JsonNode.Parse(terminal.LastBody)!["previous_response_id"]);
        Assert.Equal(new[] { "first", "answer", "tool-result" }, SentInputMarkers(terminal.LastBody));

        // The base did NOT grow: the current input goes to the history, never to the base.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b"));
        Assert.Equal(new[] { "answer", "tool-result", "answer2" }, HistoryMarkers(handler, "resp_b"));

        // Stored exactly once across the entire entry — not once in the base and once in history.
        Assert.Equal(1, OccurrencesIn(handler, "resp_b", "tool-result"));
        Assert.Equal(1, OccurrencesIn(handler, "resp_b", "first"));

        // Both conversations coexist; the parent keeps its own key.
        Assert.Equal(2, handler.StoreCountForTest);
        Assert.Equal(new[] { "resp_a", "resp_b" }, handler.InsertionOrderForTest);
    }

    /// <summary>
    /// A three-turn chain accumulates every turn exactly once, in request→response order, with the
    /// base still holding only the originating input.
    /// </summary>
    [Fact]
    public async Task MultiTurnChain_AccumulatesEachTurnExactlyOnce()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_1", "out1"))
            .Json(JsonReply("resp_2", "out2"))
            .Json(JsonReply("resp_3", "out3"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request(ToolInput("tool1"), previousResponseId: "\"resp_1\""));
        await SendAsync(client, Request(ToolInput("tool2"), previousResponseId: "\"resp_2\""));

        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_3"));
        Assert.Equal(new[] { "out1", "tool1", "out2", "tool2", "out3" }, HistoryMarkers(handler, "resp_3"));

        foreach (var marker in new[] { "first", "out1", "tool1", "out2", "tool2", "out3" })
            Assert.Equal(1, OccurrencesIn(handler, "resp_3", marker));
    }

    // ── (c) The non-array-input normalization ────────────────────────────────

    /// <summary>
    /// A request whose <c>input</c> is not a JSON array stages NOTHING, so a valid response id
    /// commits an EMPTY <c>BaseInput</c> with only the response output as the history.
    /// </summary>
    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("{\"role\":\"user\"}")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task NonArrayInput_StagesNothing_AndCommitsEmptyBaseWithOutputOnly(string rawInput)
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_c", "c-out"));
        using var client = CreateClient(terminal, out var handler);

        var requestBody = Request(rawInput);
        await SendAsync(client, requestBody);

        // The request still went through, unchanged: no id to strip and no parent to inline.
        Assert.Equal(JsonNode.Parse(requestBody)!.ToJsonString(),
            JsonNode.Parse(terminal.LastBody)!.ToJsonString());

        // An entry still exists (the exchange was authoritative), with an EMPTY base.
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Empty(BaseMarkers(handler, "resp_c"));
        Assert.Equal(new[] { "c-out" }, HistoryMarkers(handler, "resp_c"));
    }

    /// <summary>
    /// The non-array rule is a BLANKET rule: even when a parent state resolved — and was inlined
    /// into the request body — nothing is staged, so the committed entry still has an empty base
    /// and carries no trace of the parent conversation.
    /// </summary>
    [Fact]
    public async Task NonArrayInput_WithResolvingParent_StillStagesNothing_ThoughRequestIsReconstructed()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_c", "c-out"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request("\"a bare string\"", previousResponseId: "\"resp_a\""));

        // TRANSFORMED NORMALLY: the id is stripped and the parent IS reconstructed in the body.
        Assert.Null(JsonNode.Parse(terminal.LastBody)!["previous_response_id"]);
        Assert.Equal(new[] { "first", "answer" }, SentInputMarkers(terminal.LastBody));

        // ...yet NOTHING was staged, so the entry is the empty-base normalization, carrying neither
        // the parent's base nor the parent's history.
        Assert.Empty(BaseMarkers(handler, "resp_c"));
        Assert.Equal(new[] { "c-out" }, HistoryMarkers(handler, "resp_c"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_c", "first"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_c", "answer"));

        // The parent entry is untouched.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
    }

    // ── (d) Isolation, branching and concurrency ─────────────────────────────

    /// <summary>
    /// A HIT returns a deep clone: mutating what the seam handed back — the base array, its
    /// elements, or a history node — must leave the stored entry untouched.
    /// </summary>
    [Fact]
    public async Task SeamHit_ReturnsDeepClone_MutatingItDoesNotAffectTheStore()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_a", "answer"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        Assert.True(handler.TryGetConversationStateForTest("resp_a", out var baseInput, out var history));

        // Mutate every part of the returned graph: element values and the collection itself.
        baseInput![0]!["content"] = "MUTATED-BASE";
        baseInput.Add(JsonNode.Parse("{\"type\":\"message\",\"content\":\"INJECTED\"}"));
        history[0]["content"] = "MUTATED-HISTORY";

        // A second read must show the original, unmutated entry.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));

        // Two independent reads never alias each other either.
        Assert.True(handler.TryGetConversationStateForTest("resp_a", out var firstRead, out _));
        Assert.True(handler.TryGetConversationStateForTest("resp_a", out var secondRead, out _));
        firstRead![0]!["content"] = "MUTATED-AGAIN";
        Assert.Equal("first", MarkerOf(secondRead![0]));
    }

    /// <summary>
    /// Resolving a parent during a follow-up must not mutate the parent's stored entry. Without the
    /// deep clone on read, the follow-up's staging appends the current input straight onto the
    /// parent's own history list, retroactively corrupting the parent conversation.
    /// </summary>
    [Fact]
    public async Task FollowUp_DoesNotMutateTheParentEntry()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_b", "answer2"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request(ToolInput("tool-result"), previousResponseId: "\"resp_a\""));

        // The parent still holds exactly what it held before the follow-up ran.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_a", "tool-result"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_a", "answer2"));
    }

    /// <summary>
    /// Two follow-ups branching from the SAME parent get fully independent entries: neither branch
    /// may observe the other's input or output, and the shared parent stays pristine.
    /// </summary>
    /// <remarks>
    /// Without deep-clone-on-read both branches would stage onto one shared history list, so the
    /// second branch would inherit the first branch's tool result.
    /// </remarks>
    [Fact]
    public async Task BranchingFollowUps_FromSameParent_AreIndependent()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_b1", "out1"))
            .Json(JsonReply("resp_b2", "out2"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request(ToolInput("branch1-input"), previousResponseId: "\"resp_a\""));
        await SendAsync(client, Request(ToolInput("branch2-input"), previousResponseId: "\"resp_a\""));

        // Each branch continues from the parent, and ONLY from the parent.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b1"));
        Assert.Equal(new[] { "answer", "branch1-input", "out1" }, HistoryMarkers(handler, "resp_b1"));

        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b2"));
        Assert.Equal(new[] { "answer", "branch2-input", "out2" }, HistoryMarkers(handler, "resp_b2"));

        // Explicit cross-contamination checks in both directions.
        Assert.Equal(0, OccurrencesIn(handler, "resp_b1", "branch2-input"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_b1", "out2"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_b2", "branch1-input"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_b2", "out1"));

        // The second branch's reconstruction never saw the first branch's turn either.
        Assert.Equal(new[] { "first", "answer", "branch2-input" }, SentInputMarkers(terminal.LastBody));

        // The parent is still pristine.
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
        Assert.Equal(3, handler.StoreCountForTest);
    }

    /// <summary>
    /// A reusable async barrier: every participant's task completes only once ALL participants have
    /// arrived, and the barrier then re-arms for the next round. Purely signal-driven — no sleeping
    /// and no polling anywhere, so the forced interleaving is deterministic.
    /// </summary>
    private sealed class AsyncBarrier
    {
        private readonly int _participants;
        private readonly object _lock = new();
        private int _waiting;
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncBarrier(int participants) => _participants = participants;

        public Task SignalAndWaitAsync()
        {
            TaskCompletionSource release;
            lock (_lock)
            {
                release = _current;
                if (++_waiting == _participants)
                {
                    _waiting = 0;
                    _current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    release.TrySetResult();
                }
            }

            return release.Task;
        }
    }

    /// <summary>
    /// Terminal handler that holds every request inside the handler until all participants of the
    /// current round have arrived, then answers each one from a body-driven responder.
    /// </summary>
    private sealed class GatedTerminal : HttpMessageHandler
    {
        private readonly AsyncBarrier _barrier;
        private readonly Func<string, string> _responder;
        private readonly object _lock = new();

        public GatedTerminal(int participants, Func<string, string> responder)
        {
            _barrier = new AsyncBarrier(participants);
            _responder = responder;
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (_lock) { Bodies.Add(body); }

            // Every participant of this round is now inside the handler simultaneously.
            await _barrier.SignalAndWaitAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responder(body), Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Two conversations driven CONCURRENTLY through one handler instance must not bleed into each
    /// other: each response id gets its own correct entry, across two interleaved rounds.
    /// </summary>
    /// <remarks>
    /// This is the scenario the shared-instance-field implementation could not satisfy at all: with
    /// one <c>_baseInput</c>/<c>_turnHistory</c> pair, the two in-flight conversations overwrite and
    /// append to the same state.
    /// </remarks>
    [Fact]
    public async Task ConcurrentConversations_ThroughOneHandler_DoNotBleedIntoEachOther()
    {
        static string Reply(string body)
        {
            // Follow-up bodies also contain the first turn's markers (they are reconstructed), so
            // the later-turn markers have to be tested first.
            if (body.Contains("A-tool", StringComparison.Ordinal)) return JsonReply("resp_A2", "A-out2");
            if (body.Contains("B-tool", StringComparison.Ordinal)) return JsonReply("resp_B2", "B-out2");
            if (body.Contains("A-first", StringComparison.Ordinal)) return JsonReply("resp_A", "A-out");
            if (body.Contains("B-first", StringComparison.Ordinal)) return JsonReply("resp_B", "B-out");
            throw new InvalidOperationException($"unscripted request: {body}");
        }

        var terminal = new GatedTerminal(participants: 2, Reply);
        using var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(terminal, out var handler);

        // Round 1: both first requests are in flight at the same time.
        await Task.WhenAll(
            SendAsync(client, Request(UserInput("A-first"))),
            SendAsync(client, Request(UserInput("B-first"))));

        Assert.Equal(2, handler.StoreCountForTest);
        Assert.Equal(new[] { "A-first" }, BaseMarkers(handler, "resp_A"));
        Assert.Equal(new[] { "A-out" }, HistoryMarkers(handler, "resp_A"));
        Assert.Equal(new[] { "B-first" }, BaseMarkers(handler, "resp_B"));
        Assert.Equal(new[] { "B-out" }, HistoryMarkers(handler, "resp_B"));

        // Round 2: both follow-ups are in flight at the same time, each naming its own parent.
        await Task.WhenAll(
            SendAsync(client, Request(ToolInput("A-tool"), previousResponseId: "\"resp_A\"")),
            SendAsync(client, Request(ToolInput("B-tool"), previousResponseId: "\"resp_B\"")));

        Assert.Equal(4, handler.StoreCountForTest);

        Assert.Equal(new[] { "A-first" }, BaseMarkers(handler, "resp_A2"));
        Assert.Equal(new[] { "A-out", "A-tool", "A-out2" }, HistoryMarkers(handler, "resp_A2"));

        Assert.Equal(new[] { "B-first" }, BaseMarkers(handler, "resp_B2"));
        Assert.Equal(new[] { "B-out", "B-tool", "B-out2" }, HistoryMarkers(handler, "resp_B2"));

        // Not one item of either conversation reached the other.
        foreach (var foreignMarker in new[] { "B-first", "B-out", "B-tool", "B-out2" })
            Assert.Equal(0, OccurrencesIn(handler, "resp_A2", foreignMarker));

        foreach (var foreignMarker in new[] { "A-first", "A-out", "A-tool", "A-out2" })
            Assert.Equal(0, OccurrencesIn(handler, "resp_B2", foreignMarker));
    }

    // ── (e) Degraded mode ────────────────────────────────────────────────────

    /// <summary>
    /// A <c>previous_response_id</c> that does not resolve — never committed, or an invalid value
    /// (empty / whitespace / null / non-string) — degrades to the FIRST-REQUEST transformation: the
    /// id is stripped, only the current input is inlined (no parent reconstruction), and the
    /// resulting entry is a FRESH one.
    /// </summary>
    [Theory]
    [InlineData("\"resp_never_committed\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    [InlineData("1234")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task UnresolvablePreviousResponseId_DegradesToFirstRequestTransformation(string rawId)
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_d", "d-out"));
        using var client = CreateClient(terminal, out var handler);

        // A real conversation exists in the store — degrading must NOT reach for it.
        await SendAsync(client, Request(UserInput("first")));

        await SendAsync(client, Request(ToolInput("degraded-input"), previousResponseId: rawId));

        // Stripped, and NOT reconstructed: only the current input was inlined.
        Assert.Null(JsonNode.Parse(terminal.LastBody)!["previous_response_id"]);
        Assert.Equal(new[] { "degraded-input" }, SentInputMarkers(terminal.LastBody));
        Assert.DoesNotContain("first", terminal.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("answer", terminal.LastBody, StringComparison.Ordinal);

        // The committed entry is FRESH: the current input as the base, only the output in history.
        Assert.Equal(new[] { "degraded-input" }, BaseMarkers(handler, "resp_d"));
        Assert.Equal(new[] { "d-out" }, HistoryMarkers(handler, "resp_d"));

        // The unrelated conversation was neither read from nor written to.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
    }

    /// <summary>
    /// The degradation is per-request, not sticky: after a degraded follow-up, a later follow-up
    /// naming a still-present id reconstructs normally again.
    /// </summary>
    [Fact]
    public async Task AfterADegradedFollowUp_ALaterValidFollowUpStillReconstructs()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_a", "answer"))
            .Json(JsonReply("resp_d", "d-out"))
            .Json(JsonReply("resp_b", "answer2"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client, Request(ToolInput("degraded-input"), previousResponseId: "\"nope\""));
        await SendAsync(client, Request(ToolInput("tool-result"), previousResponseId: "\"resp_a\""));

        Assert.Equal(new[] { "first", "answer", "tool-result" }, SentInputMarkers(terminal.LastBody));
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b"));
        Assert.Equal(new[] { "answer", "tool-result", "answer2" }, HistoryMarkers(handler, "resp_b"));

        // The degraded exchange left its own independent entry, with no cross-talk.
        Assert.Equal(new[] { "degraded-input" }, BaseMarkers(handler, "resp_d"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_b", "degraded-input"));
    }

    // ── (f) Bounded FIFO and in-place duplicates ─────────────────────────────

    /// <summary>
    /// The store is bounded at <c>MaxEntries</c> (50): once full, each genuinely new key evicts the
    /// OLDEST key, in insertion order, and the count never grows past the bound.
    /// </summary>
    [Fact]
    public async Task Store_IsBoundedAtFiftyEntries_EvictingOldestFirst()
    {
        var terminal = new ScriptedTerminal();
        for (var i = 1; i <= 55; i++)
            terminal.Json(JsonReply($"resp_{i}", $"out-{i}"));

        using var client = CreateClient(terminal, out var handler);

        for (var i = 1; i <= 55; i++)
            await SendAsync(client, Request(UserInput($"in-{i}")));

        // Bounded, and holding exactly the newest 50 keys in insertion order.
        Assert.Equal(50, handler.StoreCountForTest);
        Assert.Equal(Enumerable.Range(6, 50).Select(i => $"resp_{i}").ToArray(), handler.InsertionOrderForTest);

        // The five oldest were evicted...
        for (var i = 1; i <= 5; i++)
            Assert.False(handler.TryGetConversationStateForTest($"resp_{i}", out _, out _));

        // ...and the surviving boundary entries still hold their own correct content.
        Assert.Equal(new[] { "in-6" }, BaseMarkers(handler, "resp_6"));
        Assert.Equal(new[] { "out-6" }, HistoryMarkers(handler, "resp_6"));
        Assert.Equal(new[] { "in-55" }, BaseMarkers(handler, "resp_55"));
        Assert.Equal(new[] { "out-55" }, HistoryMarkers(handler, "resp_55"));
    }

    /// <summary>
    /// Exactly at the bound the store is full but nothing has been evicted yet — proving the
    /// eviction test above is not passing because of an off-by-one that drops entries early.
    /// </summary>
    [Fact]
    public async Task Store_AtExactlyFiftyEntries_HasEvictedNothing()
    {
        var terminal = new ScriptedTerminal();
        for (var i = 1; i <= 50; i++)
            terminal.Json(JsonReply($"resp_{i}", $"out-{i}"));

        using var client = CreateClient(terminal, out var handler);

        for (var i = 1; i <= 50; i++)
            await SendAsync(client, Request(UserInput($"in-{i}")));

        Assert.Equal(50, handler.StoreCountForTest);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $"resp_{i}").ToArray(), handler.InsertionOrderForTest);
        Assert.Equal(new[] { "in-1" }, BaseMarkers(handler, "resp_1"));
    }

    /// <summary>
    /// A follow-up naming an EVICTED parent degrades exactly like a missing one: no reconstruction,
    /// and a FRESH entry.
    /// </summary>
    [Fact]
    public async Task FollowUpNamingAnEvictedParent_Degrades()
    {
        var terminal = new ScriptedTerminal();
        for (var i = 1; i <= 51; i++)
            terminal.Json(JsonReply($"resp_{i}", $"out-{i}"));
        terminal.Json(JsonReply("resp_after", "after-out"));

        using var client = CreateClient(terminal, out var handler);

        for (var i = 1; i <= 51; i++)
            await SendAsync(client, Request(UserInput($"in-{i}")));

        // resp_1 was pushed out by resp_51.
        Assert.False(handler.TryGetConversationStateForTest("resp_1", out _, out _));

        await SendAsync(client, Request(ToolInput("post-eviction"), previousResponseId: "\"resp_1\""));

        Assert.Equal(new[] { "post-eviction" }, SentInputMarkers(terminal.LastBody));
        Assert.Equal(new[] { "post-eviction" }, BaseMarkers(handler, "resp_after"));
        Assert.Equal(new[] { "after-out" }, HistoryMarkers(handler, "resp_after"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_after", "in-1"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_after", "out-1"));
    }

    /// <summary>
    /// Re-committing an id that is already present UPDATES IT IN PLACE: the entry's contents are
    /// replaced, but the insertion order keeps its original sequence and position and the store
    /// does not grow. A second queue entry would both mis-order eviction and leak a key.
    /// </summary>
    [Fact]
    public async Task RecommittingAnExistingId_UpdatesInPlace_WithoutGrowingOrReorderingTheQueue()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_dup", "v1-out"))
            .Json(JsonReply("resp_other1", "o1-out"))
            .Json(JsonReply("resp_other2", "o2-out"))
            .Json(JsonReply("resp_dup", "v2-out"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("v1-in")));
        await SendAsync(client, Request(UserInput("o1-in")));
        await SendAsync(client, Request(UserInput("o2-in")));

        Assert.Equal(new[] { "resp_dup", "resp_other1", "resp_other2" }, handler.InsertionOrderForTest);

        // The duplicate commit.
        await SendAsync(client, Request(UserInput("v2-in")));

        // No growth and no re-queueing: the key keeps its ORIGINAL position at the head.
        Assert.Equal(3, handler.StoreCountForTest);
        Assert.Equal(new[] { "resp_dup", "resp_other1", "resp_other2" }, handler.InsertionOrderForTest);

        // The entry itself was replaced, not merged or appended to.
        Assert.Equal(new[] { "v2-in" }, BaseMarkers(handler, "resp_dup"));
        Assert.Equal(new[] { "v2-out" }, HistoryMarkers(handler, "resp_dup"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_dup", "v1-in"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_dup", "v1-out"));

        // The other entries are untouched.
        Assert.Equal(new[] { "o1-in" }, BaseMarkers(handler, "resp_other1"));
        Assert.Equal(new[] { "o2-in" }, BaseMarkers(handler, "resp_other2"));
    }

    /// <summary>
    /// The streaming slot participates in the store like any other key: re-committing it updates in
    /// place, so repeated streaming turns never grow the queue or displace other conversations.
    /// </summary>
    [Fact]
    public async Task TheStreamingSlot_IsUpdatedInPlaceAcrossTurns()
    {
        var terminal = new ScriptedTerminal().Sse().Sse().Sse();
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("slot-base"), stream: true));
        await SendAsync(client, Request(ToolInput("turn2"), stream: true));
        await SendAsync(client, Request(ToolInput("turn3"), stream: true));

        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Equal(new[] { "turn2", "turn3" }, HistoryMarkers(handler, SlotKey));
    }

    // ── (g) Uniform id validation and the seam miss contract ─────────────────

    /// <summary>
    /// THE UNIFORM ID RULE: a response id is usable only when it is a non-empty, non-whitespace
    /// JSON STRING. Every other shape — null, numeric, boolean, object, array, empty, whitespace —
    /// writes NO entry at all.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("12.5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\t\"")]
    public async Task ResponseWithoutAValidStringId_WritesNoEntry(string rawId)
    {
        var terminal = new ScriptedTerminal().Json(
            "{\"id\":" + rawId + ",\"output\":[{\"type\":\"message\",\"content\":\"x\"}]}");
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        // The exchange succeeded, but nothing durable was recorded under any key.
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);
    }

    /// <summary>A response with no <c>id</c> property at all writes no entry either.</summary>
    [Fact]
    public async Task ResponseWithNoIdProperty_WritesNoEntry()
    {
        var terminal = new ScriptedTerminal().Json("{\"output\":[{\"type\":\"message\",\"content\":\"x\"}]}");
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);
    }

    /// <summary>
    /// A valid string id IS committed — proving the id-matrix test above is not passing merely
    /// because commits never happen — while an invalid-id response leaves nothing to continue from,
    /// so a follow-up naming it degrades.
    /// </summary>
    [Fact]
    public async Task ValidStringId_IsCommitted_WhileAnInvalidIdResponseLeavesNothingToContinueFrom()
    {
        var terminal = new ScriptedTerminal()
            .Json(JsonReply("resp_valid", "answer"))
            .Json("{\"id\":123,\"output\":[{\"type\":\"message\",\"content\":\"ignored\"}]}")
            .Json(JsonReply("resp_next", "next-out"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_valid"));

        // The numeric-id response adds nothing.
        await SendAsync(client, Request(ToolInput("orphan"), previousResponseId: "\"resp_valid\""));
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { "resp_valid" }, handler.InsertionOrderForTest);

        // So a follow-up naming "123" has nothing to continue from and degrades.
        await SendAsync(client, Request(ToolInput("after-orphan"), previousResponseId: "\"123\""));
        Assert.Equal(new[] { "after-orphan" }, SentInputMarkers(terminal.LastBody));
        Assert.Equal(new[] { "after-orphan" }, BaseMarkers(handler, "resp_next"));
    }

    /// <summary>
    /// THE SEAM MISS CONTRACT: a null, empty, whitespace-only or simply absent id returns
    /// <see langword="false"/> with a null base and an EMPTY history, and never throws — even while
    /// the store holds other entries, and without disturbing them.
    /// </summary>
    [Fact]
    public async Task SeamMiss_ReturnsFalseNullBaseAndEmptyHistory_WithoutThrowing()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_ok", "answer"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));

        foreach (var probe in new string?[] { null, "", "   ", "\t", "\r\n", "resp_absent", "RESP_OK" })
        {
            Assert.False(handler.TryGetConversationStateForTest(probe, out var baseInput, out var turnHistory));
            Assert.Null(baseInput);
            Assert.NotNull(turnHistory);
            Assert.Empty(turnHistory);
        }

        // The probes disturbed nothing.
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_ok"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_ok"));
    }

    /// <summary>The miss contract holds on a completely empty store too, without throwing.</summary>
    [Fact]
    public void SeamMiss_OnAnEmptyStore_ReturnsFalseWithoutThrowing()
    {
        var terminal = new ScriptedTerminal();
        using var client = CreateClient(terminal, out var handler);

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);

        Assert.False(handler.TryGetConversationStateForTest(null, out var baseInput, out var turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);

        Assert.False(handler.TryGetConversationStateForTest("anything", out baseInput, out turnHistory));
        Assert.Null(baseInput);
        Assert.Empty(turnHistory);
    }

    // ── (h) The streaming algorithm ──────────────────────────────────────────

    /// <summary>
    /// STREAMING SELECTION, case 3 — neither a resolving id nor a slot entry: FRESH staging. The
    /// slot receives the current input as its base and an EMPTY history.
    /// </summary>
    [Fact]
    public async Task StreamingRequest_WithNoIdAndNoSlot_CommitsFreshStateToTheSlot()
    {
        var terminal = new ScriptedTerminal().Sse();
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("fresh-input"), stream: true));

        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
        Assert.Equal(new[] { "fresh-input" }, BaseMarkers(handler, SlotKey));
        Assert.Empty(HistoryMarkers(handler, SlotKey));
    }

    /// <summary>
    /// STREAMING SELECTION, case 2 — an absent, or present-but-unresolvable,
    /// <c>previous_response_id</c> with the slot present: the SLOT's state is CONTINUED, so the base
    /// stays the slot's original base and the current input is appended to the slot's history.
    /// </summary>
    /// <remarks>
    /// Without the slot fallback this would stage FRESH, making the base the current input and the
    /// history empty — exactly what the assertions here exclude.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("\"resp_unknown\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task StreamingRequest_FallsBackToTheSlot_WhenTheIdDoesNotResolve(string? rawId)
    {
        var terminal = new ScriptedTerminal().Sse().Sse();
        using var client = CreateClient(terminal, out var handler);

        // Seed the slot with a FRESH streaming turn.
        await SendAsync(client, Request(UserInput("slot-base"), stream: true));

        // Second streaming turn: continues from the slot.
        await SendAsync(client, Request(ToolInput("slot-turn2"), previousResponseId: rawId, stream: true));

        // CONTINUATION from the slot: the base is unchanged, the input went to the history.
        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Equal(new[] { "slot-turn2" }, HistoryMarkers(handler, SlotKey));

        // The reconstruction the server saw carried the slot's conversation forward.
        Assert.Equal(new[] { "slot-base", "slot-turn2" }, SentInputMarkers(terminal.LastBody));

        // Still exactly one key: the slot. Streaming never writes an id-keyed entry.
        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
    }

    /// <summary>
    /// A NON-streaming request gets NO slot fallback: with a slot present but no resolving id, its
    /// selection is parent-or-fresh only, so it stages FRESH and never inherits the slot.
    /// </summary>
    [Fact]
    public async Task NonStreamingRequest_NeverFallsBackToTheSlot()
    {
        var terminal = new ScriptedTerminal()
            .Sse()
            .Json(JsonReply("resp_plain", "plain-out"));
        using var client = CreateClient(terminal, out var handler);

        // Seed the slot.
        await SendAsync(client, Request(UserInput("slot-base"), stream: true));

        // A non-streaming request with no previous_response_id at all.
        await SendAsync(client, Request(UserInput("plain-input")));

        // FRESH — not a continuation of the slot.
        Assert.Equal(new[] { "plain-input" }, SentInputMarkers(terminal.LastBody));
        Assert.Equal(new[] { "plain-input" }, BaseMarkers(handler, "resp_plain"));
        Assert.Equal(new[] { "plain-out" }, HistoryMarkers(handler, "resp_plain"));
        Assert.Equal(0, OccurrencesIn(handler, "resp_plain", "slot-base"));

        // The slot itself is untouched by the non-streaming exchange.
        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Empty(HistoryMarkers(handler, SlotKey));
    }

    /// <summary>
    /// STREAMING SELECTION, case 1 — a valid, present, FOUND <c>previous_response_id</c> wins over
    /// the slot: the real parent's state is continued, not the slot's.
    /// </summary>
    [Fact]
    public async Task StreamingRequest_WithResolvingId_ContinuesTheRealParentNotTheSlot()
    {
        var terminal = new ScriptedTerminal()
            .Sse()                                  // seeds the slot with a different conversation
            .Json(JsonReply("resp_a", "answer"))    // the real parent
            .Sse();                                 // the streaming follow-up under test
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("slot-base"), stream: true));
        await SendAsync(client, Request(UserInput("first")));

        await SendAsync(client,
            Request(ToolInput("stream-input"), previousResponseId: "\"resp_a\"", stream: true));

        // The REAL parent's base and history were continued — not the slot's previous entry.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, SlotKey));
        Assert.Equal(new[] { "answer", "stream-input" }, HistoryMarkers(handler, SlotKey));
        Assert.Equal(0, OccurrencesIn(handler, SlotKey, "slot-base"));

        // The request body proves the same selection.
        Assert.Equal(new[] { "first", "answer", "stream-input" }, SentInputMarkers(terminal.LastBody));

        // The parent's own entry is untouched by the streaming turn.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
    }

    /// <summary>
    /// A successful SSE response commits the staged base + staged history under the slot key and
    /// performs NO output extraction: nothing derived from the event-stream body reaches the entry,
    /// and no entry is written under the id the stream itself carries.
    /// </summary>
    [Fact]
    public async Task SuccessfulSse_CommitsToSlotWithoutExtractingAnyOutput()
    {
        // A payload an output-extracting implementation would happily mine.
        const string sseBody =
            "event: response.completed\n" +
            "data: {\"id\":\"resp_sse\",\"output\":[{\"type\":\"message\",\"content\":\"sse-output\"}]}\n\n";

        var terminal = new ScriptedTerminal().Sse(sseBody);
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("stream-first"), stream: true));

        // Committed under the slot key only — never under the id embedded in the stream.
        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
        Assert.False(handler.TryGetConversationStateForTest("resp_sse", out _, out _));

        // Staged base + staged history exactly, with nothing extracted from the SSE body.
        Assert.Equal(new[] { "stream-first" }, BaseMarkers(handler, SlotKey));
        Assert.Empty(HistoryMarkers(handler, SlotKey));
        Assert.Equal(0, OccurrencesIn(handler, SlotKey, "sse-output"));
    }

    /// <summary>
    /// THE NON-ARRAY STREAMING NO-OP: a <c>stream:true</c> request whose input is not an array
    /// stages nothing, so a successful SSE response leaves the slot's PREVIOUS entry exactly as it
    /// was — it must not be overwritten with an empty-base entry.
    /// </summary>
    [Fact]
    public async Task StreamingRequestWithNonArrayInput_LeavesThePreviousSlotEntryUntouched()
    {
        var terminal = new ScriptedTerminal().Sse().Sse().Sse();
        using var client = CreateClient(terminal, out var handler);

        // Build a slot entry with both a base and a history so any overwrite is visible.
        await SendAsync(client, Request(UserInput("slot-base"), stream: true));
        await SendAsync(client, Request(ToolInput("slot-turn2"), stream: true));

        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Equal(new[] { "slot-turn2" }, HistoryMarkers(handler, SlotKey));

        // The no-op request.
        await SendAsync(client, Request("\"not-an-array\"", stream: true));

        // Exactly the same entry, and still exactly one key in the store.
        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Equal(new[] { "slot-turn2" }, HistoryMarkers(handler, SlotKey));
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
    }

    /// <summary>
    /// The same no-op with an EMPTY store: a non-array streaming request creates no slot entry at
    /// all — "nothing staged" must not be normalized into an empty-base slot write.
    /// </summary>
    [Fact]
    public async Task StreamingRequestWithNonArrayInput_OnAnEmptyStore_WritesNoSlotEntry()
    {
        var terminal = new ScriptedTerminal().Sse();
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request("\"not-an-array\"", stream: true));

        Assert.Equal(0, handler.StoreCountForTest);
        Assert.False(handler.TryGetConversationStateForTest(SlotKey, out _, out _));
    }

    /// <summary>
    /// THE <c>stream:true</c> + NON-SSE RESPONSE CASE: the response's Content-Type governs the
    /// commit, so this routes through the NORMAL id-keyed path — output append included — and never
    /// touches the slot.
    /// </summary>
    [Fact]
    public async Task StreamTrueRequestWithJsonResponse_CommitsUnderTheResponseIdNotTheSlot()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_json", "json-out"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("stream-json"), stream: true));

        // Normal id-keyed commit, with the response output appended.
        Assert.Equal(new[] { "resp_json" }, handler.InsertionOrderForTest);
        Assert.Equal(new[] { "stream-json" }, BaseMarkers(handler, "resp_json"));
        Assert.Equal(new[] { "json-out" }, HistoryMarkers(handler, "resp_json"));

        // The slot was not written.
        Assert.False(handler.TryGetConversationStateForTest(SlotKey, out _, out _));
    }

    /// <summary>
    /// The same case with NON-ARRAY input: it still takes the normal path, including the EMPTY-base
    /// normalization and the output append — and still never the slot.
    /// </summary>
    [Fact]
    public async Task StreamTrueRequestWithNonArrayInputAndJsonResponse_UsesEmptyBaseNormalization()
    {
        var terminal = new ScriptedTerminal().Json(JsonReply("resp_json", "json-out"));
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request("\"not-an-array\"", stream: true));

        Assert.Equal(new[] { "resp_json" }, handler.InsertionOrderForTest);
        Assert.Empty(BaseMarkers(handler, "resp_json"));
        Assert.Equal(new[] { "json-out" }, HistoryMarkers(handler, "resp_json"));
        Assert.False(handler.TryGetConversationStateForTest(SlotKey, out _, out _));
    }

    /// <summary>
    /// A <c>stream:true</c> FOLLOW-UP whose response is JSON continues its parent normally and lands
    /// under the response id — the slot stays exactly as it was.
    /// </summary>
    [Fact]
    public async Task StreamTrueFollowUpWithJsonResponse_DoesNotDisturbTheSlot()
    {
        var terminal = new ScriptedTerminal()
            .Sse()                                  // seeds the slot
            .Json(JsonReply("resp_a", "answer"))    // the parent
            .Json(JsonReply("resp_b", "answer2"));  // stream:true request, JSON response
        using var client = CreateClient(terminal, out var handler);

        await SendAsync(client, Request(UserInput("slot-base"), stream: true));
        await SendAsync(client, Request(UserInput("first")));
        await SendAsync(client,
            Request(ToolInput("tool-result"), previousResponseId: "\"resp_a\"", stream: true));

        // Id-keyed, with the full continuation.
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b"));
        Assert.Equal(new[] { "answer", "tool-result", "answer2" }, HistoryMarkers(handler, "resp_b"));

        // The slot never moved.
        Assert.Equal(new[] { "slot-base" }, BaseMarkers(handler, SlotKey));
        Assert.Empty(HistoryMarkers(handler, SlotKey));
    }

    // ── (i) Retry-safe staging, migrated semantics ───────────────────────────

    /// <summary>
    /// A transient failure followed by success commits EXACTLY ONCE under the response id, on both
    /// a first request and a follow-up: no duplicated base, history or output from the extra
    /// attempts, and no staging lost by the attempt that finally succeeded.
    /// </summary>
    [Fact]
    public async Task RetriedRequests_CommitExactlyOncePerResponseId()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 2, JsonReply("resp_a", "answer"));
        using var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(
            terminal, out var handler);

        await SendAsync(client, Request(UserInput("first")));
        Assert.Equal(3, terminal.Attempts);

        // One entry, one base item, one history item — not three of each.
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { "resp_a" }, handler.InsertionOrderForTest);
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_a"));
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));

        // A retried FOLLOW-UP: its input must be appended exactly once despite three attempts.
        terminal.ResetFailures(2);
        terminal.SuccessBody = JsonReply("resp_b", "answer2");
        await SendAsync(client, Request(ToolInput("tool-result"), previousResponseId: "\"resp_a\""));
        Assert.Equal(3, terminal.Attempts);

        Assert.Equal(2, handler.StoreCountForTest);
        Assert.Equal(new[] { "first" }, BaseMarkers(handler, "resp_b"));
        Assert.Equal(new[] { "answer", "tool-result", "answer2" }, HistoryMarkers(handler, "resp_b"));
        Assert.Equal(1, OccurrencesIn(handler, "resp_b", "tool-result"));
        Assert.Equal(1, OccurrencesIn(handler, "resp_b", "answer2"));

        // Every attempt sent an identical body: the transformation ran exactly once.
        Assert.Single(terminal.Bodies.Distinct());

        // And the parent was not mutated by any of the follow-up's attempts.
        Assert.Equal(new[] { "answer" }, HistoryMarkers(handler, "resp_a"));
    }

    /// <summary>
    /// A retried STREAMING exchange commits to the slot exactly once too: the failed attempts leave
    /// no partial state and the successful one does not double-append.
    /// </summary>
    [Fact]
    public async Task RetriedStreamingRequest_CommitsToTheSlotExactlyOnce()
    {
        var terminal = new CopilotResponsesHandlerRetryTests.FailThenSucceedHandler(
            failures: 2, JsonReply("unused", "unused"))
        {
            FailuresUseSseContentType = true,
            SuccessUsesSseContentType = true,
        };
        using var client = CopilotResponsesHandlerRetryTests.GetClientForExternalUse(
            terminal, out var handler);

        await SendAsync(client, Request(UserInput("stream-first"), stream: true));

        Assert.Equal(3, terminal.Attempts);
        Assert.Equal(1, handler.StoreCountForTest);
        Assert.Equal(new[] { SlotKey }, handler.InsertionOrderForTest);
        Assert.Equal(new[] { "stream-first" }, BaseMarkers(handler, SlotKey));
        Assert.Empty(HistoryMarkers(handler, SlotKey));
    }
}
