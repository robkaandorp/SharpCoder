using SharpCoder.Providers;

using Microsoft.Extensions.AI;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Unit tests for <see cref="ChatClientFactory.ParseProviderAndModel"/>: verifies
/// that provider prefixes are extracted correctly for every known provider, that
/// plain model names and edge-case inputs are handled without throwing, and that
/// the returned tuple always contains the exact expected provider and model values.
/// </summary>
public sealed class ChatClientFactoryTests
{
    // ── Known provider prefixes ──────────────────────────────────────────────

    #region copilot/ prefix — extracts "copilot" provider and bare model name

    /// <summary>
    /// When the model string begins with the "copilot/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "copilot" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_CopilotPrefix_ReturnsCopilotProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
    }

    #endregion

    #region ollama-cloud/ prefix — extracts "ollama-cloud" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-cloud/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-cloud" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaCloudPrefix_ReturnsOllamaCloudProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
    }

    #endregion

    #region ollama-local/ prefix — extracts "ollama-local" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-local/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-local" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaLocalPrefix_ReturnsOllamaLocalProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-local/llama3");

        Assert.Equal("ollama-local", provider);
        Assert.Equal("llama3", model);
    }

    #endregion

    #region github/ prefix — extracts "github" provider and bare model name

    /// <summary>
    /// When the model string begins with the "github/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "github" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_GithubPrefix_ReturnsGithubProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("github/openai/gpt-4.1");

        Assert.Equal("github", provider);
        Assert.Equal("openai/gpt-4.1", model);
    }

    #endregion

    // ── No prefix / plain model name ────────────────────────────────────────

    #region No prefix — returns default provider and the full model string as model

    /// <summary>
    /// When the model string contains no recognised provider prefix (no slash,
    /// or an unrecognised prefix), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the default provider from the <c>LLM_PROVIDER</c> environment
    /// variable (defaulting to "copilot") and the original model string as the model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_PlainModelName_ReturnsDefaultProviderAndFullModelName()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel("gpt-4o");

        Assert.Equal(expectedProvider, provider);
        Assert.Equal("gpt-4o", model);
    }

    #endregion

    // ── Edge cases ───────────────────────────────────────────────────────────

    #region Empty string — returns default provider and null model without throwing

    /// <summary>
    /// When the input is an empty string, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") with a <see langword="null"/> model, identical to the behaviour for
    /// a <see langword="null"/> input.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_EmptyString_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(string.Empty);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion

    #region Prefix with no model after slash — returns known provider and empty model string

    /// <summary>
    /// When the input is a known provider prefix followed immediately by a slash but no
    /// model name (e.g. "copilot/"), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must extract the provider correctly and return an empty string as the model, because
    /// <c>Substring(slashIndex + 1)</c> on "copilot/" yields "".
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_KnownPrefixWithNoModel_ReturnsProviderAndEmptyModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/");

        Assert.Equal("copilot", provider);
        Assert.Equal(string.Empty, model);
    }

    #endregion

    #region Double slash — returns known provider and the remainder including leading slash

    /// <summary>
    /// When the input contains a double slash (e.g. "copilot//model"), the first slash is
    /// used to split off the prefix. <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the known provider and the remainder after the first slash as the model,
    /// which will begin with an additional slash character.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_DoubleSlash_ReturnsProviderAndRemainderAfterFirstSlash()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot//model");

        Assert.Equal("copilot", provider);
        Assert.Equal("/model", model);
    }

    #endregion

    // ── Null input ───────────────────────────────────────────────────────────

    #region Null input — returns default provider and null model without throwing

    /// <summary>
    /// When the input is <see langword="null"/>, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") paired with a <see langword="null"/> model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_NullInput_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(null);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion
}

/// <summary>
/// Tests confirming that model strings are parsed into provider + plain model name only.
/// Reasoning effort is never derived from a model-name suffix: any trailing colon segment
/// (including Ollama tags like <c>:120b</c> and legacy levels like <c>:high</c>) stays part
/// of the model name.
/// </summary>
public sealed class ChatClientFactoryReasoningTests
{
    [Fact]
    public void ParseProviderAndModel_LegacyHighSuffix_IsNotStripped()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6:high");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6:high", model);
    }

    [Fact]
    public void ParseProviderAndModel_OllamaModelTag_IsPreserved()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
    }

    [Fact]
    public void ParseProviderAndModel_PlainModel_ReturnsModelUnchanged()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
    }
}

/// <summary>
/// Tests for <see cref="ChatClientFactory.CopilotResponsesHandler"/> streaming behaviour:
/// verifies that SSE (text/event-stream) responses pass through intact without being
/// read or modified, while non-streaming JSON responses are still processed for turn
/// history tracking.
/// </summary>
public sealed class CopilotResponsesHandlerTests
{
    /// <summary>
    /// Verifies that when the Copilot API returns a streaming response (content-type
    /// text/event-stream), <see cref="ChatClientFactory.CopilotResponsesHandler"/>
    /// returns it immediately without reading or modifying the body, so the SSE stream
    /// can be consumed directly by the OpenAI SDK's streaming parser.
    /// </summary>
    [Fact]
    public async Task StreamingResponse_PassesThrough_Intact()
    {
        const string sseBody = "event: done\ndata: {}\n\n";
        var handler = new ChatClientFactory.CopilotResponsesHandler(
            new StreamingFakeResponseHandler(sseBody));
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // The content-type must remain text/event-stream (not overwritten to JSON)
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // The body must NOT have been read or replaced with JSON
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("event: done", body);
    }

    /// <summary>
    /// Verifies that non-streaming (application/json) responses are still fully processed:
    /// the handler reads the body, parses it for turn history accumulation, and
    /// re-wraps it as StringContent.
    /// </summary>
    [Fact]
    public async Task NonStreamingResponse_StillProcessed()
    {
        const string jsonBody = """{"output":[{"type":"message","content":[{"type":"output_text","text":"Hello"}]}]}""";
        var handler = new ChatClientFactory.CopilotResponsesHandler(
            new JsonFakeResponseHandler(jsonBody));
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Non-streaming response should be returned as JSON StringContent
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(body);
    }

    /// <summary>Fake inner handler that returns a streaming SSE response.</summary>
    private sealed class StreamingFakeResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StreamingFakeResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Fake inner handler that returns a JSON response.</summary>
    private sealed class JsonFakeResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public JsonFakeResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Contract tests for the streaming transport tee:
/// <see cref="ChatClientFactory.CopilotResponsesHandler.TeeStreamContent"/> and its nested
/// <see cref="ChatClientFactory.CopilotResponsesHandler.TeeStreamContent.TeeStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// The tee exists so a future SSE parser can observe the event stream WITHOUT changing a single
/// byte the OpenAI SDK's streaming parser sees. Every enumerated guarantee therefore gets its own
/// test, so each one is independently removable and independently falsifiable: header
/// preservation, lazy stream acquisition, the disposal chain, the post-disposal contract, the
/// unsupported write side, both consumption paths observing the <c>OnChunk</c> seam, byte-exact
/// passthrough with the underlying read pattern intact, chunk laziness, the handler-return
/// contract, observer-exception containment, and the null-observer default.
/// </para>
/// <para>
/// The fakes count what they are asked about — stream acquisitions, reads, chunk sizes, disposals
/// — so assertions like "exactly once" and "the wrapped stream is NEVER disposed" are observable
/// facts rather than assumptions. In particular
/// <see cref="RecordingHttpContent"/> deliberately does NOT dispose its own stream, so any
/// non-zero <see cref="ScriptedReadStream.DisposeCount"/> can only have come from the tee.
/// </para>
/// </remarks>
public sealed class TeeStreamContentTests
{
    /// <summary>A 26-byte SSE-shaped body; the exact length is load-bearing for chunk assertions.</summary>
    private const string SseBody = "event: delta\ndata: 12345\n\n";

    private static byte[] BodyBytes => System.Text.Encoding.UTF8.GetBytes(SseBody);

    // ── 1. Header preservation ───────────────────────────────────────────────

    /// <summary>
    /// Every original content header — the SSE media type WITH its charset parameter, single-value
    /// custom headers and multi-value custom headers alike — must be present on the wrapper, so a
    /// wrapped response is indistinguishable from the one the server sent.
    /// </summary>
    [Fact]
    public void Wrapper_CopiesAllOriginalContentHeaders()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        original.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream; charset=utf-8");
        original.Headers.TryAddWithoutValidation("X-Request-Id", "req-abc-123");
        original.Headers.TryAddWithoutValidation("X-Multi", "one");
        original.Headers.TryAddWithoutValidation("X-Multi", "two");
        original.Headers.TryAddWithoutValidation("Content-Language", "en-GB");

        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        Assert.Equal("text/event-stream", tee.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", tee.Headers.ContentType?.CharSet);
        Assert.Equal(new[] { "req-abc-123" }, tee.Headers.GetValues("X-Request-Id"));
        Assert.Equal(new[] { "one", "two" }, tee.Headers.GetValues("X-Multi"));
        Assert.Equal(new[] { "en-GB" }, tee.Headers.GetValues("Content-Language"));

        // Copying headers must not have touched the body.
        Assert.Equal(0, original.StreamAccessCount);
    }

    // ── 2. Lazy acquisition: deferred to the first ACTUAL read ───────────────

    /// <summary>
    /// Obtaining the stream HANDLE touches the original content not at all — neither at
    /// construction nor when <c>ReadAsStreamAsync</c> hands the tee out, and a second handle
    /// request returns the very same instance without acquiring either.
    /// </summary>
    /// <remarks>
    /// Acquiring a stream is not reading it. The whole point of the tee is that a wrapped response
    /// can be handed back through the handler completely unconsumed AND unacquired; an acquisition
    /// at handle time would silently pull the response body's stream before the caller ever asked
    /// for a byte.
    /// <para>
    /// The <c>Assert.Same</c> below pins the observable contract — a caller asking twice gets one
    /// stream — but note that base <see cref="HttpContent"/> caches the content-read stream it
    /// hands out, so the repeat request never re-enters the tee's own factory. The assertion
    /// therefore documents the guarantee rather than isolating who provides it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AsyncHandleAcquisition_TouchesTheOriginalContent_NotAtAll()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);

        var first = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);

        var second = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);
    }

    /// <summary>
    /// The SYNCHRONOUS handle path — <c>ReadAsStream()</c>, i.e. <c>CreateContentReadStream</c> —
    /// is equally acquisition-free, and equally returns the same tee instance on a repeat request.
    /// </summary>
    [Fact]
    public void SyncHandleAcquisition_TouchesTheOriginalContent_NotAtAll()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        Assert.Equal(0, original.StreamAccessCount);

        var first = tee.ReadAsStream(TestContext.Current.CancellationToken);

        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);

        var second = tee.ReadAsStream(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);
    }

    /// <summary>
    /// A content that explodes the instant its stream is touched can be wrapped AND handed out as
    /// a stream without a whisper; only the first ACTUAL read detonates it. This is the sharpest
    /// statement of the deferral: an acquisition anywhere before the first read would surface here
    /// as an exception from the handle call.
    /// </summary>
    [Fact]
    public async Task HostileContent_SurvivesWrappingAndHandOut_AndOnlyTheFirstReadAcquires()
    {
        using var hostile = new ThrowingStreamAccessContent();
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(hostile);

        Assert.Equal(0, hostile.StreamAccessCount);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, hostile.StreamAccessCount);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var n = await stream.ReadAsync(new byte[8].AsMemory(), TestContext.Current.CancellationToken);
            Assert.Fail($"The first read returned {n} instead of surfacing the hostile stream access.");
        });

        Assert.Equal("stream accessed", thrown.Message);
        Assert.Equal(1, hostile.StreamAccessCount);
    }

    /// <summary>
    /// A SYNCHRONOUS read as the first-ever operation on the tee triggers the acquisition by
    /// itself — a sync-first caller must never depend on some async call having run before it.
    /// Exactly one acquisition, and every later read reuses it.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="FirstAsyncRead_TriggersAcquisitionExactlyOnce"/> on purpose: an
    /// acquisition wired only into the async overloads must fail THIS test by name.
    /// </remarks>
    [Theory]
    [InlineData(ReadMode.SyncArray)]
    [InlineData(ReadMode.SyncSpan)]
    public void FirstSyncRead_TriggersAcquisitionExactlyOnce(ReadMode mode)
    {
        var body = BodyBytes;
        using var original = new RecordingHttpContent(body, 5);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);
        Assert.Equal(0, original.StreamAccessCount);

        var buffer = new byte[64];
        var read = mode == ReadMode.SyncArray
            ? stream.Read(buffer, 0, buffer.Length)
            : stream.Read(buffer.AsSpan());

        // The very first read — synchronous, with nothing async having run — did the acquiring.
        Assert.Equal(5, read);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Equal(1, original.InnerStream.ReadCallCount);

        // Every subsequent read reuses the cached stream: still exactly one acquisition.
        var second = mode == ReadMode.SyncArray
            ? stream.Read(buffer, 0, buffer.Length)
            : stream.Read(buffer.AsSpan());

        Assert.Equal(5, second);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Equal(2, original.InnerStream.ReadCallCount);
    }

    /// <summary>
    /// An ASYNCHRONOUS read as the first-ever operation on the tee triggers the acquisition by
    /// itself. Exactly one acquisition, and every later read reuses it.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="FirstSyncRead_TriggersAcquisitionExactlyOnce"/> on purpose: an
    /// acquisition wired only into the synchronous overloads must fail THIS test by name.
    /// </remarks>
    [Theory]
    [InlineData(ReadMode.AsyncArray)]
    [InlineData(ReadMode.AsyncMemory)]
    public async Task FirstAsyncRead_TriggersAcquisitionExactlyOnce(ReadMode mode)
    {
        var body = BodyBytes;
        using var original = new RecordingHttpContent(body, 5);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, original.StreamAccessCount);

        var buffer = new byte[64];
        var read = mode == ReadMode.AsyncArray
            ? await stream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken)
            : await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(5, read);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Equal(1, original.InnerStream.ReadCallCount);

        var second = mode == ReadMode.AsyncArray
            ? await stream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken)
            : await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(5, second);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Equal(2, original.InnerStream.ReadCallCount);
    }

    /// <summary>
    /// The fully synchronous content path end to end: the sync handle API hands out the tee
    /// without acquiring, the first sync read acquires exactly once, and the bytes come through
    /// verbatim and fully observed — no async call anywhere in the sequence.
    /// </summary>
    [Fact]
    public void SyncContentStreamPath_AcquiresOnFirstSyncRead_AndPassesThroughByteExact()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 6);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);
        Assert.Empty(observer.ChunkSizes);

        var sink = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) break;
            sink.Write(buffer, 0, n);
        }

        Assert.Equal(1, original.StreamAccessCount);
        Assert.Equal(body, sink.ToArray());
        Assert.Equal(body, observer.Bytes);
        Assert.Equal(new[] { 6, 6, 6, 6, 2 }, observer.ChunkSizes);
    }

    /// <summary>
    /// PRE-ACQUISITION DELEGATION VALUES, exactly as documented on the production type: querying a
    /// capability or position must never pull the wrapped stream, so before the first read the tee
    /// answers from its own deliberate defaults — <c>CanRead</c> true, <c>CanSeek</c> false,
    /// <c>CanWrite</c> false, <c>Position</c> 0 — and refuses what it cannot answer:
    /// <c>Length</c>, <c>Seek</c> and the <c>Position</c> setter throw
    /// <see cref="NotSupportedException"/> (NOT <see cref="ObjectDisposedException"/> — the stream
    /// is alive, merely unacquired).
    /// </summary>
    /// <remarks>
    /// The inner stream deliberately reports <c>CanSeek</c> true and a length of its own, so a tee
    /// that delegated eagerly (and thereby acquired) would answer <c>true</c>/<c>26</c> here and
    /// fail — as would the stream-access assertion at the end.
    /// </remarks>
    [Fact]
    public void PreAcquisition_DelegationMembers_ReturnDocumentedDefaults_WithoutAcquiring()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        // The wrapped stream is seekable and 26 bytes long — neither of which the tee may report
        // before it has actually acquired anything.
        Assert.True(original.InnerStream.CanSeek);
        Assert.Equal(26, original.InnerStream.Length);

        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(0L, stream.Position);

        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Position = 4);

        // Not one of those queries was allowed to touch the original content.
        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.ReadCallCount);
    }

    // ── 2b. Alive read-side delegation, post-acquisition ─────────────────────

    /// <summary>
    /// Once acquired, the read-side members are pure delegation: <c>CanSeek</c>, <c>Length</c>,
    /// <c>Position</c> (get AND set) and <c>Seek</c> all report the WRAPPED stream's own answers.
    /// </summary>
    /// <remarks>
    /// Every value here is deliberately indistinguishable from nothing else: a length of
    /// 987654321, an initial position of 31337 that advances by exactly the bytes read, and a
    /// <c>Seek</c> that answers 4242 regardless of its arguments. No plausible hardcoded constant
    /// — 0, the offset passed in, the byte count, the length — can coincidentally satisfy them,
    /// so stubbing any one of these delegates fails this test.
    /// </remarks>
    [Fact]
    public void AliveDelegation_PassesThroughDecisiveWrappedStreamValues()
    {
        var probe = new DelegationProbeStream();
        using var original = new StreamProbeContent(probe);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        // One real read acquires the wrapped stream and advances its position by exactly 1.
        Assert.Equal(1, stream.Read(new byte[4], 0, 4));
        Assert.Equal(1, original.StreamAccessCount);

        // CanSeek is the wrapped stream's answer, not a constant — and it flips with it.
        Assert.True(stream.CanSeek);
        probe.ReportCanSeek = false;
        Assert.False(stream.CanSeek);
        probe.ReportCanSeek = true;
        Assert.True(stream.CanSeek);

        // Length is the wrapped stream's, to the byte.
        Assert.Equal(DelegationProbeStream.DecisiveLength, stream.Length);

        // Position reads through: the decisive start plus the single byte consumed above.
        Assert.Equal(DelegationProbeStream.DecisiveInitialPosition + 1, stream.Position);

        // Position writes through: the wrapped stream records the exact value, and the getter
        // reports it back.
        stream.Position = 555_444_333L;
        Assert.Equal(555_444_333L, probe.LastPositionSet);
        Assert.Equal(555_444_333L, stream.Position);

        // Seek forwards the exact arguments and returns the wrapped stream's own answer — which
        // is neither the offset, nor zero, nor the resulting position.
        var seekResult = stream.Seek(-77L, SeekOrigin.End);
        Assert.Equal(DelegationProbeStream.DecisiveSeekResult, seekResult);
        Assert.Equal(-77L, probe.LastSeekOffset);
        Assert.Equal(SeekOrigin.End, probe.LastSeekOrigin);
    }

    /// <summary>
    /// <c>CanRead</c> is delegation too, not the hardcoded <see langword="true"/> that its
    /// pre-acquisition default might suggest: a wrapped stream reporting <c>CanRead</c> false
    /// makes the acquired tee report false as well.
    /// </summary>
    [Fact]
    public void AliveDelegation_CanRead_FollowsTheWrappedStream()
    {
        var probe = new DelegationProbeStream();
        using var original = new StreamProbeContent(probe);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        // Pre-acquisition the answer is the documented default…
        Assert.True(stream.CanRead);

        Assert.Equal(1, stream.Read(new byte[4], 0, 4));

        // …and post-acquisition it is whatever the wrapped stream says — including false.
        Assert.True(stream.CanRead);
        probe.ReportCanRead = false;
        Assert.False(stream.CanRead);
        probe.ReportCanRead = true;
        Assert.True(stream.CanRead);
    }

    // ── 3. Disposal chain ────────────────────────────────────────────────────

    /// <summary>
    /// Disposing the wrapper disposes the CAPTURED ORIGINAL content exactly once — repeats are
    /// no-ops — while the tee stream itself NEVER disposes the wrapped stream, and its own
    /// <c>Dispose</c>/<c>DisposeAsync</c> are idempotent.
    /// </summary>
    [Fact]
    public async Task Dispose_DisposesOriginalExactlyOnce_AndTeeNeverDisposesWrappedStream()
    {
        var original = new RecordingHttpContent(BodyBytes, 8);
        var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        // A real read, so the wrapped stream is genuinely acquired and its disposal is genuinely
        // at stake — a handle alone would leave nothing for anyone to dispose.
        Assert.Equal(8, await stream.ReadAsync(new byte[64].AsMemory(), TestContext.Current.CancellationToken));
        Assert.Equal(1, original.StreamAccessCount);

        // The tee stream's own disposal is idempotent and leaves the wrapped stream alone.
        await ((IAsyncDisposable)stream).DisposeAsync();
        await ((IAsyncDisposable)stream).DisposeAsync();
        stream.Dispose();
        stream.Dispose();

        Assert.Equal(0, original.InnerStream.DisposeCount);
        Assert.Equal(0, original.DisposeCount);

        // Wrapper disposal flows through to the captured original — exactly once, repeats no-op.
        tee.Dispose();
        Assert.Equal(1, original.DisposeCount);

        tee.Dispose();
        tee.Dispose();
        Assert.Equal(1, original.DisposeCount);

        // Ownership of the underlying stream stays with the ORIGINAL content: the stream was
        // disposed exactly once, and it happened inside the original's own disposal — never
        // directly by the tee, which had already been disposed twice by then without touching it.
        Assert.Equal(1, original.InnerStream.DisposeCount);
        Assert.True(original.InnerStream.DisposedWhileOwnerDisposing);
    }

    /// <summary>
    /// Disposal is safe BEFORE the stream was ever acquired: nothing is pulled to satisfy it,
    /// nobody disposes a stream that was never handed out, and the wrapper's own once-only
    /// disposal of the original is unaffected.
    /// </summary>
    [Fact]
    public void DisposeBeforeAcquisition_AcquiresNothing_AndStaysSafe()
    {
        var original = new RecordingHttpContent(BodyBytes);
        var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        stream.Dispose();
        stream.Dispose();

        // Disposal must not have reached for the stream it never had.
        Assert.Equal(0, original.StreamAccessCount);
        Assert.Equal(0, original.InnerStream.DisposeCount);
        Assert.Equal(0, original.DisposeCount);

        tee.Dispose();
        tee.Dispose();

        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(0, original.StreamAccessCount);
    }

    // ── 4. Post-disposal contract ────────────────────────────────────────────

    /// <summary>
    /// After disposal the tee stream is inert: every read overload, <c>Seek</c>, <c>Position</c>
    /// (get AND set), <c>Length</c> and <c>CopyToAsync</c> throw
    /// <see cref="ObjectDisposedException"/>, and all three capability flags report false.
    /// </summary>
    [Fact]
    public async Task TeeStream_AfterDisposal_EnumeratedMembersThrowObjectDisposed()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var buffer = new byte[8];

        // Alive first, so the post-disposal throws below cannot be an artefact of a broken stream.
        Assert.True(stream.CanRead);
        var alive = await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
        Assert.Equal(8, alive);

        await ((IAsyncDisposable)stream).DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            var n = stream.Read(buffer, 0, buffer.Length);
            Assert.Fail($"Read(byte[],int,int) returned {n} after disposal.");
        });

        Assert.Throws<ObjectDisposedException>(() =>
        {
            var n = stream.Read(buffer.AsSpan());
            Assert.Fail($"Read(Span<byte>) returned {n} after disposal.");
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken);
            Assert.Fail($"ReadAsync(byte[],int,int,ct) returned {n} after disposal.");
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
            Assert.Fail($"ReadAsync(Memory<byte>,ct) returned {n} after disposal.");
        });

        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await stream.CopyToAsync(Stream.Null, 4096, TestContext.Current.CancellationToken));

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    // ── 4b. Post-disposal contract via the SYNCHRONOUS Dispose() ─────────────

    /// <summary>
    /// The synchronous <c>Dispose()</c> is its own entry point into the disposed state — not a
    /// side effect of having called <c>DisposeAsync</c> first. After a sync-only disposal the
    /// enumerated members throw <see cref="ObjectDisposedException"/> and the capability flags
    /// all report false, exactly as they do after the async form.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of
    /// <see cref="TeeStream_AfterDisposal_EnumeratedMembersThrowObjectDisposed"/>: dropping the
    /// disposed-state transition from the SYNCHRONOUS path alone would leave that async test
    /// green, and must fail this one.
    /// </remarks>
    [Fact]
    public async Task TeeStream_AfterSynchronousDispose_EnumeratedMembersThrowObjectDisposed()
    {
        using var original = new RecordingHttpContent(BodyBytes, 8);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var buffer = new byte[8];

        // Alive first — and genuinely acquired — so the throws below cannot be an artefact of an
        // unacquired or broken stream.
        Assert.True(stream.CanRead);
        Assert.Equal(8, await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken));
        Assert.Equal(1, original.StreamAccessCount);

        // THE SYNCHRONOUS ENTRY POINT — no DisposeAsync anywhere in this test.
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            var n = stream.Read(buffer, 0, buffer.Length);
            Assert.Fail($"Read(byte[],int,int) returned {n} after a synchronous dispose.");
        });

        Assert.Throws<ObjectDisposedException>(() =>
        {
            var n = stream.Read(buffer.AsSpan());
            Assert.Fail($"Read(Span<byte>) returned {n} after a synchronous dispose.");
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            var n = await stream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken);
            Assert.Fail($"ReadAsync(byte[],int,int,ct) returned {n} after a synchronous dispose.");
        });

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
            Assert.Fail($"ReadAsync(Memory<byte>,ct) returned {n} after a synchronous dispose.");
        });

        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await stream.CopyToAsync(Stream.Null, 4096, TestContext.Current.CancellationToken));

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);

        // The reads that threw did so without consuming anything more from the wrapped stream.
        Assert.Equal(1, original.InnerStream.ReadCallCount);
    }

    /// <summary>
    /// The synchronous <c>Dispose()</c> is idempotent and NEVER disposes the wrapped stream:
    /// repeats are silent no-ops, and the wrapped stream's disposal still belongs exclusively to
    /// the original content.
    /// </summary>
    [Fact]
    public void TeeStream_SynchronousDispose_IsIdempotent_AndNeverDisposesWrappedStream()
    {
        var original = new RecordingHttpContent(BodyBytes, 8);
        var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = tee.ReadAsStream(TestContext.Current.CancellationToken);

        // Acquire for real via the synchronous path.
        Assert.Equal(8, stream.Read(new byte[64], 0, 64));
        Assert.Equal(1, original.StreamAccessCount);

        stream.Dispose();
        stream.Dispose();
        stream.Dispose();

        // Not one of those disposals reached the wrapped stream or the original content.
        Assert.Equal(0, original.InnerStream.DisposeCount);
        Assert.Equal(0, original.DisposeCount);

        // And the original still owns — and performs — the one real stream disposal.
        tee.Dispose();

        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(1, original.InnerStream.DisposeCount);
        Assert.True(original.InnerStream.DisposedWhileOwnerDisposing);
    }

    // ── 5. Write side ────────────────────────────────────────────────────────

    /// <summary>
    /// An SSE response stream is read-only: the write side reports no capability and throws
    /// <see cref="NotSupportedException"/> — distinctly NOT
    /// <see cref="ObjectDisposedException"/> — both before and after disposal.
    /// </summary>
    [Fact]
    public async Task TeeStream_WriteSideUnsupported_BeforeAndAfterDisposal()
    {
        using var original = new RecordingHttpContent(BodyBytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);
        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var payload = new byte[] { 1, 2, 3 };

        Assert.False(stream.CanWrite);
        await AssertWriteSideUnsupportedAsync(stream, payload);

        await ((IAsyncDisposable)stream).DisposeAsync();

        Assert.False(stream.CanWrite);
        await AssertWriteSideUnsupportedAsync(stream, payload);

        static async Task AssertWriteSideUnsupportedAsync(Stream stream, byte[] payload)
        {
            Assert.Throws<NotSupportedException>(() => stream.Write(payload, 0, payload.Length));
            Assert.Throws<NotSupportedException>(() => stream.Write(payload.AsSpan()));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
            await Assert.ThrowsAsync<NotSupportedException>(
                async () => await stream.WriteAsync(payload, 0, payload.Length, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(
                async () => await stream.WriteAsync(payload.AsMemory(), TestContext.Current.CancellationToken));
        }
    }

    // ── 6. Both consumption paths observe the seam ───────────────────────────

    /// <summary>
    /// The SDK's path — <c>ReadAsStreamAsync</c> — routes through the tee: every chunk reaches
    /// <c>OnChunk</c>, in the exact sizes the underlying stream produced.
    /// </summary>
    [Fact]
    public async Task ReadAsStreamAsync_ObservesEveryChunk()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 5);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 1024);

        Assert.Equal(body, received);
        Assert.Equal(body, observer.Bytes);
        Assert.Equal(new[] { 5, 5, 5, 5, 5, 1 }, observer.ChunkSizes);
    }

    /// <summary>
    /// The buffering path — <c>ReadAsByteArrayAsync</c>, which goes through
    /// <c>SerializeToStreamAsync</c> — routes through the tee as well, so ALL content reads are
    /// observable, not just the SDK's stream path.
    /// </summary>
    [Fact]
    public async Task ReadAsByteArrayAsync_ObservesEveryChunk()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 8);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        var received = await tee.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(body, received);
        Assert.Equal(body, observer.Bytes);
        Assert.Equal(new[] { 8, 8, 8, 2 }, observer.ChunkSizes);
    }

    // ── 7. Byte-exact passthrough and read pattern ───────────────────────────

    /// <summary>
    /// What the reader receives and what the observer sees are both byte-for-byte the original
    /// body, delivered in exactly the chunks the underlying stream produced — nothing coalesced,
    /// nothing split, nothing read ahead.
    /// </summary>
    [Fact]
    public async Task Passthrough_IsByteExact_AndPreservesUnderlyingReadPattern()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        // Irregular scripted chunk sizes: 4, 9, 2, 6, then the tail of 5.
        using var original = new RecordingHttpContent(body, 4, 9, 2, 6);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 1024);

        Assert.Equal(body, received);
        Assert.Equal(body, observer.Bytes);
        Assert.Equal(new[] { 4, 9, 2, 6, 5 }, observer.ChunkSizes);

        // One observer invocation per underlying read that produced data — never more.
        Assert.Equal(5, observer.ChunkSizes.Count);
    }

    /// <summary>
    /// EVERY read overload is a tee: <c>Read(byte[],int,int)</c>, <c>Read(Span&lt;byte&gt;)</c>,
    /// <c>ReadAsync(byte[],int,int,ct)</c> and <c>ReadAsync(Memory&lt;byte&gt;,ct)</c> each forward
    /// one underlying read, offer exactly that chunk to the observer, and return it verbatim. An
    /// overload that quietly skipped the seam would leave the parser blind on that path alone.
    /// </summary>
    [Theory]
    [InlineData(ReadMode.SyncArray)]
    [InlineData(ReadMode.SyncSpan)]
    [InlineData(ReadMode.AsyncArray)]
    [InlineData(ReadMode.AsyncMemory)]
    public async Task EveryReadOverload_ObservesChunks_AndReturnsThemVerbatim(ReadMode mode)
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 3, 11, 7);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 1024, mode);

        Assert.Equal(body, received);
        Assert.Equal(body, observer.Bytes);
        Assert.Equal(new[] { 3, 11, 7, 5 }, observer.ChunkSizes);
    }

    // ── 8. Chunk laziness ────────────────────────────────────────────────────

    /// <summary>
    /// Nothing is pre-buffered: no read at construction, and a single partial read consumes
    /// exactly one underlying chunk, leaving the remainder unread.
    /// </summary>
    [Fact]
    public async Task PartialRead_ConsumesOneChunk_AndLeavesRemainderUnread()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 4);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var observer = new ChunkRecorder();
        tee.OnChunk = observer.Record;

        // Construction touches nothing.
        Assert.Equal(0, original.InnerStream.ReadCallCount);
        Assert.Equal(0, original.InnerStream.BytesRead);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        // Acquiring the stream is still not reading it.
        Assert.Equal(0, original.InnerStream.ReadCallCount);
        Assert.Equal(0, original.InnerStream.BytesRead);
        Assert.Empty(observer.ChunkSizes);

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(4, read);
        Assert.Equal(1, original.InnerStream.ReadCallCount);
        Assert.Equal(4, original.InnerStream.BytesRead);
        Assert.Equal(22, original.InnerStream.Remaining);
        Assert.Equal(new[] { 4 }, observer.ChunkSizes);
        Assert.Equal(body.Take(4), observer.Bytes);
    }

    // ── 9. Handler-return contract ───────────────────────────────────────────

    /// <summary>
    /// The successful SSE response leaves the handler wrapped but COMPLETELY UNCONSUMED: no
    /// stream acquisition, no read. Only the caller's own read moves a byte, and what it gets is
    /// the untouched body.
    /// </summary>
    /// <remarks>
    /// The handler is invoked through an <see cref="HttpMessageInvoker"/> rather than an
    /// <see cref="HttpClient"/> on purpose: <c>HttpClient</c>'s default
    /// <see cref="HttpCompletionOption.ResponseContentRead"/> buffers the response body itself,
    /// above the handler, which would consume the stream for reasons that have nothing to do with
    /// the tee. The invoker returns exactly what the handler returned, which is the contract under
    /// test — and it is the shape the real streaming path uses.
    /// </remarks>
    [Fact]
    public async Task Handler_ReturnsWrappedSseResponse_WithStreamUnconsumed()
    {
        var body = BodyBytes;
        var terminal = new RecordingSseTerminalHandler(body, 6);
        var handler = new ChatClientFactory.CopilotResponsesHandler(terminal);
        using var invoker = new HttpMessageInvoker(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"stream":true,"input":[{"role":"user","content":"hi"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };

        var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // Wrapped, with the SSE signal intact…
        Assert.IsType<ChatClientFactory.CopilotResponsesHandler.TeeStreamContent>(response.Content);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // …and untouched: not acquired, not read.
        Assert.Equal(0, terminal.SseContent.StreamAccessCount);
        Assert.Equal(0, terminal.SseContent.InnerStream.ReadCallCount);
        Assert.Equal(0, terminal.SseContent.InnerStream.BytesRead);

        // Nothing has been committed: no chunk has flowed through the tee yet, so the parser can
        // not have seen a response.created event — a stronger proof of the unconsumed contract
        // than any state assertion about the old shared streaming slot.
        Assert.Equal(0, handler.StoreCountForTest);

        // Only the caller's own read moves bytes, and it gets the body verbatim.
        var received = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body, received);
        Assert.Equal(26, terminal.SseContent.InnerStream.BytesRead);

        // The scripted body carries no response.created event, so no id was ever observable and
        // NO entry is written under any key — there is no fallback slot any more.
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);
    }

    // ── 10. Observer-exception containment ───────────────────────────────────

    /// <summary>
    /// A throwing observer is contained and DISABLED: the exception never escapes the tee, no
    /// further chunk is offered to it, and the passthrough still completes byte-exact.
    /// </summary>
    [Fact]
    public async Task ThrowingObserver_IsContainedAndDisabled_PassthroughStillCompletes()
    {
        var body = BodyBytes;
        Assert.Equal(26, body.Length);

        using var original = new RecordingHttpContent(body, 5);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var invocations = 0;
        tee.OnChunk = _ =>
        {
            invocations++;
            throw new InvalidOperationException("parser exploded");
        };

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 1024);

        // The passthrough is unharmed: all six chunks (5,5,5,5,5,1) still arrived, byte-exact.
        Assert.Equal(body, received);

        // The observer was invoked for the FIRST chunk only, then dropped — the remaining five
        // chunks never reached it.
        Assert.Equal(1, invocations);
        Assert.Null(tee.OnChunk);
    }

    // ── 11. Null-observer default ────────────────────────────────────────────

    /// <summary>
    /// With no observer attached — the default — the tee is a pure passthrough: byte-exact, no
    /// failure, and the seam stays null.
    /// </summary>
    [Fact]
    public async Task NullObserver_PassthroughCompletesByteExact()
    {
        var body = BodyBytes;

        using var original = new RecordingHttpContent(body, 7);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        Assert.Null(tee.OnChunk);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 1024);

        Assert.Equal(body, received);
        Assert.Equal(26, received.Length);
        Assert.Null(tee.OnChunk);
    }

    // ── 12. Exactly-once acquisition under COMPETING first reads ─────────────

    /// <summary>
    /// TWO COMPETING ASYNC FIRST READS PULL THE ORIGINAL EXACTLY ONCE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlap is forced, not hoped for. Reader A's acquisition parks inside the fake content
    /// on a <see cref="TaskCompletionSource"/> gate and stays there until this test releases it.
    /// Reader B is then invoked DIRECTLY on the test thread — deliberately not through
    /// <see cref="Task.Run(Func{Task})"/> — because an <c>async</c> method runs synchronously
    /// until its first incomplete await: by the time <c>ReadAsync</c> hands back its (incomplete)
    /// task, B has already run through the tee's acquisition decision and attached itself to the
    /// published authority. B's presence inside the contested region is therefore an OBSERVED
    /// FACT at the moment of the mid-race assertions, not an assumption about scheduling.
    /// </para>
    /// <para>
    /// A "B has started" flag set just BEFORE calling <c>ReadAsync</c> would prove nothing: the
    /// flag necessarily precedes entry, so the test could assert — and even release A's gate —
    /// while B was still queued and had not yet reached the acquisition path at all.
    /// </para>
    /// <para>
    /// SCOPE OF THIS VECTOR. Base <see cref="HttpContent"/> caches its own content-read task and
    /// therefore collapses even CONCURRENT <c>ReadAsStreamAsync</c> calls into a single
    /// <c>CreateContentReadStreamAsync</c>. An async-only double-pull is consequently absorbed by
    /// the base class before this fake can see it, so this test states the contract rather than
    /// falsifying every possible breach of it. The removal-proof vector — the one that fails on
    /// every run when the release-across-await guard comes back — is
    /// <see cref="SyncFirstRead_RacingAParkedAsyncAcquisition_PullsTheOriginalExactlyOnce"/>,
    /// where the second acquisition takes the synchronous path the base class does NOT merge.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CompetingAsyncFirstReads_PullTheOriginalExactlyOnce()
    {
        var body = BodyBytes;
        using var original = new GatedAcquisitionContent(body, chunkSize: 4);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var bufferA = new byte[64];
        var bufferB = new byte[64];

        // A enters the acquisition and parks inside the fake content. Observing this also proves
        // the acquisition authority has been published, so B is guaranteed to meet it.
        var readerA = Task.Run(
            async () => await stream.ReadAsync(bufferA.AsMemory(), TestContext.Current.CancellationToken));
        await original.AcquisitionEntered.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, original.StreamAccessCount);

        // B enters the contested region ON THIS THREAD: the call below returns only after B has
        // synchronously reached — and suspended on — the acquisition it must converge with.
        var readerB = stream.ReadAsync(bufferB.AsMemory(), TestContext.Current.CancellationToken).AsTask();

        // MID-RACE, with B PROVEN inside the region and A PROVEN still parked: the original has
        // been touched exactly once, and no competing pull was ever started.
        Assert.False(readerB.IsCompleted);
        Assert.False(readerA.IsCompleted);
        Assert.False(original.CompetingPullEntered.IsCompleted);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Empty(original.CreatedStreams);

        original.ReleaseAcquisition();

        var readA = await readerA.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);
        var readB = await readerB.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // DECISIVE: across the whole race the original was pulled exactly once, and exactly one
        // wrapped stream was ever created.
        Assert.Equal(1, original.StreamAccessCount);
        Assert.False(original.CompetingPullEntered.IsCompleted);
        Assert.Single(original.CreatedStreams);

        Assert.Equal(4, readA);
        Assert.Equal(4, readB);

        // Between them they consumed the first two 4-byte chunks of the body, in some order, from
        // the one wrapped stream — byte-exact either way.
        var combined = bufferA.Take(readA).Concat(bufferB.Take(readB)).ToArray();
        var forwards = body.Take(8).ToArray();
        var swapped = body.Skip(4).Take(4).Concat(body.Take(4)).ToArray();
        Assert.True(
            combined.SequenceEqual(forwards) || combined.SequenceEqual(swapped),
            $"racing readers produced [{string.Join(",", combined)}]");
    }

    /// <summary>
    /// A SYNCHRONOUS first read racing a parked ASYNCHRONOUS acquisition also pulls exactly once:
    /// the sync caller converges on the in-flight attempt instead of starting a second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE REMOVAL-PROOF VECTOR for the exactly-once guard, and the rendezvous below is
    /// what makes it decisive on EVERY run rather than on lucky ones. A's gate is released only
    /// after the synchronous reader has been PROVEN to be inside the tee's acquisition wait — its
    /// dedicated thread observed blocked with nothing left to execute but the read. A flag set
    /// before calling <c>Read</c> would not do: A could then be released, publish its stream, and
    /// let a broken implementation slip through before the sync thread ever reached the contested
    /// region.
    /// </para>
    /// <para>
    /// The rendezvous resolves on the FIRST of three decisive edges, so a violation is caught
    /// immediately instead of waiting out the bound: the sync reader blocking on the shared
    /// authority (the contract), the sync reader completing early (it took a path of its own), or
    /// a competing pull entering the fake content (an outright second acquisition).
    /// </para>
    /// <para>
    /// The synchronous reader necessarily BLOCKS while the async acquisition is parked, so it runs
    /// on its own dedicated background thread — a pool thread could otherwise be starved by the
    /// very acquisition it is waiting for, turning a deadlock in production into a hang in the
    /// test harness rather than a failure. Every wait is bounded by <see cref="GateTimeout"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SyncFirstRead_RacingAParkedAsyncAcquisition_PullsTheOriginalExactlyOnce()
    {
        var body = BodyBytes;
        using var original = new GatedAcquisitionContent(body, chunkSize: 4);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var asyncBuffer = new byte[64];
        var syncBuffer = new byte[64];

        var asyncReader = Task.Run(
            async () => await stream.ReadAsync(asyncBuffer.AsMemory(), TestContext.Current.CancellationToken));
        await original.AcquisitionEntered.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, original.StreamAccessCount);

        // The blocking sync reader gets a thread of its own so it can park indefinitely without
        // consuming a pool thread the async side may need. Its body contains NOTHING but the read,
        // so once it is observed blocked it can only be blocked inside the tee's acquisition wait.
        var syncResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncThread = new Thread(() =>
        {
            try { syncResult.SetResult(stream.Read(syncBuffer, 0, syncBuffer.Length)); }
            catch (Exception ex) { syncResult.SetException(ex); }
        })
        { IsBackground = true, Name = "tee-sync-racer" };
        syncThread.Start();

        await WaitForSyncRacerToEnterAcquisitionAsync(
            syncThread, syncResult.Task, original.CompetingPullEntered);

        // MID-RACE, with the sync reader PROVEN inside the acquisition wait and the async
        // acquisition PROVEN still parked: exactly one pull between the two of them, and no
        // competing acquisition was ever started.
        Assert.False(original.CompetingPullEntered.IsCompleted);
        Assert.False(syncResult.Task.IsCompleted);
        Assert.False(asyncReader.IsCompleted);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Empty(original.CreatedStreams);

        original.ReleaseAcquisition();

        var asyncRead = await asyncReader.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);
        var syncRead = await syncResult.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // DECISIVE: one pull, one wrapped stream, both readers served from it. A guard that let
        // the sync caller pull while the async attempt was in flight lands on 2 — or fails
        // outright, because the base class forbids a sync retrieval after an async one.
        Assert.Equal(1, original.StreamAccessCount);
        Assert.False(original.CompetingPullEntered.IsCompleted);
        Assert.Same(Assert.Single(original.CreatedStreams), original.LastStreamHandedToTee);
        Assert.Equal(4, asyncRead);
        Assert.Equal(4, syncRead);

        Assert.True(syncThread.Join(GateTimeout), "the synchronous racing reader never finished.");
    }

    /// <summary>
    /// SAME-INSTANCE REUSE UNDER RACE: every racing reader ends up reading from the ONE wrapped
    /// stream the single acquisition produced — proved by instance identity, not by equal bytes.
    /// </summary>
    /// <remarks>
    /// The fake hands out a DISTINCT stream instance per pull, so a second acquisition would give
    /// one of the racers a different object. Reader B enters the contested region synchronously on
    /// the test thread (see
    /// <see cref="CompetingAsyncFirstReads_PullTheOriginalExactlyOnce"/>), so the race is real
    /// before the gate is released.
    /// </remarks>
    [Fact]
    public async Task RacingReaders_AllShareTheOneAcquiredStreamInstance()
    {
        var body = BodyBytes;
        using var original = new GatedAcquisitionContent(body, chunkSize: 2);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var readerA = Task.Run(
            async () => await stream.ReadAsync(new byte[8].AsMemory(), TestContext.Current.CancellationToken));
        await original.AcquisitionEntered.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // B enters the region on this thread and suspends inside it — proven by the incomplete
        // task the direct call hands back.
        var readerB = stream.ReadAsync(new byte[8].AsMemory(), TestContext.Current.CancellationToken).AsTask();

        Assert.False(readerB.IsCompleted);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Empty(original.CreatedStreams);

        original.ReleaseAcquisition();

        await readerA.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);
        await readerB.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // Exactly one stream was ever created, and BOTH racing reads went through that very
        // object — instance identity, not merely equal bytes.
        var created = Assert.Single(original.CreatedStreams);
        Assert.Same(created, original.LastStreamHandedToTee);
        Assert.Equal(2, created.ReadCallCount);

        // A later read joins the same instance — no re-acquisition once the race is over.
        var later = await stream.ReadAsync(new byte[8].AsMemory(), TestContext.Current.CancellationToken);
        Assert.Equal(2, later);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.Same(created, Assert.Single(original.CreatedStreams));
        Assert.Equal(3, created.ReadCallCount);
    }

    /// <summary>
    /// A FAULTED first acquisition is SHARED with whoever raced it: the racers all observe that
    /// one failure, from the one pull, and no stream is ever published from a failed attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the failure half of exactly-once. A guard that let the second reader start its own
    /// pull would show two acquisitions here, and might hand that reader a stream while the first
    /// reader saw an exception — two readers of one response disagreeing about whether it exists.
    /// Reader B again enters the region synchronously on the test thread, so it is provably
    /// waiting on the doomed attempt before that attempt is allowed to fail.
    /// </para>
    /// <para>
    /// The production type documents "retry, not poison": it clears its acquisition slot before
    /// surfacing the failure, so a later read asks again rather than replaying a cached failure of
    /// its own. That re-ask is deliberately NOT asserted here as a SUCCESS, because it is not
    /// observable through an <see cref="HttpContent"/>: the base class caches its own content-read
    /// task, so a content that faulted once re-serves that same faulted task to every later
    /// request. What IS asserted is the observable consequence — a later read still surfaces a
    /// failure rather than a null, a half-published stream, or a silent zero-length read.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FaultedFirstAcquisition_IsSharedByRacers_AndPublishesNoStream()
    {
        var body = BodyBytes;
        using var original = new GatedAcquisitionContent(body, chunkSize: 4) { FailFirstAcquisition = true };
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var readerA = Task.Run(
            async () => await stream.ReadAsync(new byte[64].AsMemory(), TestContext.Current.CancellationToken));
        await original.AcquisitionEntered.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        var readerB = stream.ReadAsync(new byte[64].AsMemory(), TestContext.Current.CancellationToken).AsTask();

        // One attempt is in flight and BOTH racers are provably on it — B is suspended inside the
        // region, not merely queued to enter it.
        Assert.False(readerB.IsCompleted);
        Assert.False(readerA.IsCompleted);
        Assert.Equal(1, original.StreamAccessCount);
        Assert.False(original.CompetingPullEntered.IsCompleted);

        original.ReleaseAcquisition();

        // BOTH racers observe the SAME single failure — B did not paper over it with a pull of
        // its own, and did not get a stream the failing reader never saw.
        var faultA = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await readerA.WaitAsync(GateTimeout, TestContext.Current.CancellationToken));
        var faultB = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await readerB.WaitAsync(GateTimeout, TestContext.Current.CancellationToken));

        Assert.Equal(GatedAcquisitionContent.FailureMessage, faultA.Message);
        Assert.Equal(GatedAcquisitionContent.FailureMessage, faultB.Message);

        // Exactly one pull, and a failed attempt published no stream at all.
        Assert.Equal(1, original.StreamAccessCount);
        Assert.False(original.CompetingPullEntered.IsCompleted);
        Assert.Empty(original.CreatedStreams);

        // A later read surfaces a failure too — never a null stream, a half-published one, or a
        // silent zero-length read that would look like a completed response body.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var n = await stream.ReadAsync(new byte[64].AsMemory(), TestContext.Current.CancellationToken);
            Assert.Fail($"a read after a faulted acquisition returned {n} instead of failing.");
        });

        Assert.Empty(original.CreatedStreams);
    }

    // ── 13. SSE parser vectors driven through the wrapper ────────────────────

    /// <summary>The <c>event:</c> line of a well-formed <c>response.created</c> block.</summary>
    private const string CreatedEventLine = "event: response.created";

    /// <summary>The <c>data:</c> line announcing <paramref name="id"/> as the response id.</summary>
    private static string CreatedDataLine(string id)
        => "data: {\"response\":{\"id\":\"" + id + "\"}}";

    /// <summary>A complete, well-formed <c>response.created</c> block.</summary>
    private static string CreatedBlock(string id)
        => CreatedEventLine + "\n" + CreatedDataLine(id) + "\n\n";

    /// <summary>
    /// The RETAINED byte count a <see cref="CreatedBlock"/> charges to the id search: its two
    /// field lines, terminators excluded.
    /// </summary>
    private static int CreatedBlockRetainedBytes(string id)
        => Encoding.UTF8.GetByteCount(CreatedEventLine) + Encoding.UTF8.GetByteCount(CreatedDataLine(id));

    /// <summary>
    /// A handler plus a parser wired to it over freshly staged state — exactly the pair the
    /// streaming path builds for one successful SSE response.
    /// </summary>
    private static (ChatClientFactory.CopilotResponsesHandler Handler,
                    ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser Parser)
        NewParser(string baseMarker = "staged-base")
    {
        var handler = new ChatClientFactory.CopilotResponsesHandler();
        var stagedBase = new JsonArray(JsonNode.Parse("{\"content\":\"" + baseMarker + "\"}"));
        return (handler, handler.CreateStreamingParserForTest(stagedBase, new List<JsonNode>()));
    }

    /// <summary>
    /// Streams <paramref name="body"/> through the REAL tee into <paramref name="parser"/> with the
    /// given chunk script, and returns exactly the bytes the reader received.
    /// </summary>
    /// <remarks>
    /// A chunk script of <c>1</c> forces SINGLE-BYTE delivery, which splits every multi-byte UTF-8
    /// sequence and every SSE line across chunk boundaries.
    /// </remarks>
    private static async Task<byte[]> DriveParserAsync(
        ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser parser,
        byte[] body,
        params int[] chunkScript)
    {
        using var original = new RecordingHttpContent(body, chunkScript);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return await DrainAsync(stream, 4096);
    }

    private static Task<byte[]> DriveParserAsync(
        ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser parser,
        string body,
        params int[] chunkScript)
        => DriveParserAsync(parser, Encoding.UTF8.GetBytes(body), chunkScript);

    /// <summary>
    /// Streams <paramref name="body"/> through the REAL tee with BOTH seams wired exactly as the
    /// production handler wires them — chunks to the parser, end-of-input to its completion path —
    /// and drains to a read == 0 so the EOF signal actually fires.
    /// </summary>
    /// <remarks>
    /// The distinction from <see cref="DriveParserAsync(ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser, byte[], int[])"/>
    /// is the whole point of these vectors: this is the PRODUCTION wiring, so a final unterminated
    /// line is processed by the transport's own end-of-stream signal rather than by a test calling
    /// the completion API directly.
    /// </remarks>
    private static async Task<byte[]> DriveParserToEofAsync(
        ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser parser,
        byte[] body,
        params int[] chunkScript)
    {
        using var original = new RecordingHttpContent(body, chunkScript);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append, parser.CompleteInput);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return await DrainAsync(stream, 4096);
    }

    private static Task<byte[]> DriveParserToEofAsync(
        ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser parser,
        string body,
        params int[] chunkScript)
        => DriveParserToEofAsync(parser, Encoding.UTF8.GetBytes(body), chunkScript);

    /// <summary>The turn history of an entry, reduced to the <c>content</c> marker of each item.</summary>
    private static string[] HistoryContents(
        ChatClientFactory.CopilotResponsesHandler handler, string id)
    {
        Assert.True(handler.TryGetConversationStateForTest(id, out _, out var history),
            $"expected an entry under '{id}'");
        return history.Select(n => n["content"]?.GetValue<string>() ?? n.ToJsonString()).ToArray();
    }

    /// <summary>The whole entry (base + history) as one JSON string, for containment assertions.</summary>
    private static string EntryJson(ChatClientFactory.CopilotResponsesHandler handler, string id)
    {
        Assert.True(handler.TryGetConversationStateForTest(id, out var baseInput, out var history),
            $"expected an entry under '{id}'");
        return (baseInput?.ToJsonString() ?? string.Empty)
            + string.Concat(history.Select(n => n.ToJsonString()));
    }

    /// <summary>LF line endings parse: the id is extracted and the entry written.</summary>
    [Fact]
    public async Task ParserVector_LfLineEndings_CommitTheId()
    {
        var (handler, parser) = NewParser();
        var body = CreatedBlock("resp_lf");

        var received = await DriveParserAsync(parser, body, 7);

        Assert.Equal("resp_lf", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_lf" }, handler.InsertionOrderForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// CRLF line endings parse identically: the trailing CR is stripped, so the event name is
    /// <c>response.created</c> and not <c>response.created\r</c> — which would match nothing and
    /// write nothing.
    /// </summary>
    [Fact]
    public async Task ParserVector_CrLfLineEndings_CommitTheId()
    {
        var (handler, parser) = NewParser();
        var body = CreatedEventLine + "\r\n" + CreatedDataLine("resp_crlf") + "\r\n\r\n";

        var received = await DriveParserAsync(parser, body, 5);

        Assert.Equal("resp_crlf", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_crlf" }, handler.InsertionOrderForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// MULTI-LINE <c>data:</c> payloads are concatenated with a newline, per the SSE spec. The
    /// fixture splits ONE JSON document across two <c>data:</c> lines, so only concatenation
    /// yields parseable JSON: a first-wins, last-wins or separator-less implementation extracts no
    /// id at all and writes nothing.
    /// </summary>
    [Fact]
    public async Task ParserVector_MultiLineDataPayload_IsConcatenatedPerSpec()
    {
        var (handler, parser) = NewParser();
        var body =
            "event: response.created\n" +
            "data: {\"response\":\n" +
            "data: {\"id\":\"resp_multi\"}}\n" +
            "\n";

        var received = await DriveParserAsync(parser, body, 4);

        Assert.Equal("resp_multi", parser.CommittedIdForTest);
        Assert.True(handler.TryGetConversationStateForTest("resp_multi", out _, out _));
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// THE JOIN CHARACTER IS EXACTLY THE SPEC'S NEWLINE. This fixture splits a <c>data:</c>
    /// payload INSIDE a JSON string literal, where a raw newline is illegal and a space (or no
    /// separator at all) is perfectly legal. The spec-correct join therefore yields an unparseable
    /// payload and the item is skipped; any other separator makes it parse and adds a phantom
    /// item. The well-formed item that follows still lands, proving the parser carried on.
    /// </summary>
    /// <remarks>
    /// The between-token variant above proves concatenation HAPPENS (killing first-wins and
    /// last-wins); this one proves WHICH character joins the lines.
    /// </remarks>
    [Fact]
    public async Task ParserVector_MultiLineDataPayload_IsJoinedWithNewlineNotAnyOtherSeparator()
    {
        var (handler, parser) = NewParser();
        var body =
            CreatedBlock("resp_join") +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"content\":\"alpha\n" +
            "data: beta\"}}\n" +
            "\n" +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"content\":\"well-formed\"}}\n\n" +
            "event: response.completed\ndata: {}\n\n";

        var received = await DriveParserAsync(parser, body, 9);

        Assert.Equal("resp_join", parser.CommittedIdForTest);

        // ONLY the well-formed item — no "alpha beta" or "alphabeta" phantom.
        Assert.Equal(new[] { "well-formed" }, HistoryContents(handler, "resp_join"));

        var entry = EntryJson(handler, "resp_join");
        Assert.DoesNotContain("alpha", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", entry, StringComparison.Ordinal);

        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// <c>event:</c> and <c>data:</c> may appear in EITHER order within a block — SSE imposes no
    /// field ordering, and a block is interpreted only once its blank line arrives.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParserVector_EventAndDataInEitherOrder_CommitTheId(bool eventFirst)
    {
        var (handler, parser) = NewParser();
        var body = eventFirst
            ? CreatedEventLine + "\n" + CreatedDataLine("resp_order") + "\n\n"
            : CreatedDataLine("resp_order") + "\n" + CreatedEventLine + "\n\n";

        var received = await DriveParserAsync(parser, body, 3);

        Assert.Equal("resp_order", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_order" }, handler.InsertionOrderForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// Comment lines (<c>:…</c>) are ignored entirely: they neither disturb the surrounding block
    /// nor cost a single byte of the id-search budget.
    /// </summary>
    [Fact]
    public async Task ParserVector_CommentLines_AreIgnoredAndCostNothing()
    {
        var (handler, parser) = NewParser();
        var body =
            ": this is a keep-alive comment\n" +
            CreatedEventLine + "\n" +
            ": another comment, mid-block\n" +
            CreatedDataLine("resp_comment") + "\n" +
            "\n";

        var received = await DriveParserAsync(parser, body, 6);

        Assert.Equal("resp_comment", parser.CommittedIdForTest);
        Assert.True(handler.TryGetConversationStateForTest("resp_comment", out _, out _));

        // Only the two retained field lines were charged — the comments cost nothing.
        Assert.Equal(CreatedBlockRetainedBytes("resp_comment"), parser.IdSearchBytesForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// A UTF-8 MULTI-BYTE character split across a chunk boundary still decodes correctly: the id
    /// is committed with its exact text. Single-byte chunking guarantees every one of the id's
    /// two-, three- and four-byte sequences is split, so a per-chunk decoder — one that decodes
    /// each chunk in isolation and loses the partial sequence — produces replacement characters
    /// and fails on the DECODED VALUE, not merely on the absence of an exception.
    /// </summary>
    [Fact]
    public async Task ParserVector_Utf8CharacterSplitAcrossChunks_DecodesExactly()
    {
        const string id = "resp_caf\u00e9_\u65e5\u672c_\U0001F600";
        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(CreatedBlock(id));

        // The id really does carry multi-byte sequences — otherwise this vector proves nothing.
        Assert.True(Encoding.UTF8.GetByteCount(id) > id.Length);

        var received = await DriveParserAsync(parser, bytes, 1);

        Assert.Equal(id, parser.CommittedIdForTest);
        Assert.Equal(new[] { id }, handler.InsertionOrderForTest);
        Assert.True(handler.TryGetConversationStateForTest(id, out var baseInput, out _));
        Assert.Equal("staged-base", baseInput![0]!["content"]!.GetValue<string>());
        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// An event split MID-LINE across chunk boundaries reassembles: the script cuts inside the
    /// event name and again inside the JSON payload.
    /// </summary>
    [Fact]
    public async Task ParserVector_EventSplitMidLineAcrossChunks_Reassembles()
    {
        var (handler, parser) = NewParser();
        var body = CreatedBlock("resp_split");

        // 10 bytes lands inside "event: response.created"; the following reads cut through the
        // rest of that line and into the middle of the data payload.
        var received = await DriveParserAsync(parser, body, 10, 9, 13, 4);

        Assert.Equal("resp_split", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_split" }, handler.InsertionOrderForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// The FINAL UNTERMINATED line is processed at end of input, through the parser's explicit
    /// completion API: the block it completes is dispatched and the id committed. Until then it is
    /// deliberately NOT dispatched — a line that could still grow must not be acted on.
    /// </summary>
    [Fact]
    public async Task ParserVector_FinalUnterminatedLine_IsProcessedAtEndOfInput()
    {
        var (handler, parser) = NewParser();

        // No trailing newline at all: the data line is the final, unterminated line.
        var body = CreatedEventLine + "\n" + CreatedDataLine("resp_tail");

        var received = await DriveParserAsync(parser, body, 5);

        // Nothing was dispatched while the line could still grow.
        Assert.Null(parser.CommittedIdForTest);
        Assert.Equal(0, handler.StoreCountForTest);

        parser.CompleteInput();

        Assert.Equal("resp_tail", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_tail" }, handler.InsertionOrderForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// END OF INPUT IS NOT A TERMINAL MARKER: a stream that ends WITHOUT
    /// <c>response.completed</c> keeps its committed entry but DROPS the buffered items, even
    /// after the explicit completion API has processed the final unterminated line. Only
    /// <c>response.completed</c> ever flushes.
    /// </summary>
    /// <remarks>
    /// Driven through the completion API on purpose: that is the one place an implementation might
    /// be tempted to treat "no more bytes" as "the response finished", which would amend an entry
    /// from a stream that was actually truncated.
    /// </remarks>
    [Fact]
    public async Task EndOfInputWithoutCompleted_DoesNotFlushTheBufferedItems()
    {
        var (handler, parser) = NewParser();
        var body =
            CreatedBlock("resp_no_flush") +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"content\":\"buffered\"}}\n\n" +
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"content\":\"tail-item\"}}\n";  // unterminated block

        await DriveParserAsync(parser, body, 13);

        // Committed, with items buffered and no terminal marker in sight.
        Assert.Equal("resp_no_flush", parser.CommittedIdForTest);
        Assert.False(parser.TerminatedForTest);
        Assert.Equal(1, parser.BufferedOutputCountForTest);
        Assert.Empty(HistoryContents(handler, "resp_no_flush"));

        parser.CompleteInput();

        // The final line was processed — the second item is buffered now too…
        Assert.Equal(2, parser.BufferedOutputCountForTest);

        // …but end of input flushed NOTHING: the entry keeps its un-amended staged composition.
        Assert.False(parser.TerminatedForTest);
        Assert.Empty(HistoryContents(handler, "resp_no_flush"));

        var entry = EntryJson(handler, "resp_no_flush");
        Assert.DoesNotContain("buffered", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("tail-item", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// Malformed events are SKIPPED, never fatal: an unknown field, a field-less line, an event
    /// with no data, a data payload that is not JSON and one that is JSON but not an object all
    /// pass through without throwing — and a later well-formed block still commits.
    /// </summary>
    [Fact]
    public async Task ParserVector_MalformedEvents_AreSkippedWithoutThrowing()
    {
        var (handler, parser) = NewParser();
        var body =
            "id: 42\n" +                                    // unknown field
            "retry\n" +                                     // no colon at all
            "\n" +
            "event: response.output_item.done\n" +          // no data
            "\n" +
            "event: response.output_item.done\n" +
            "data: {not json at all\n" +                    // unparseable
            "\n" +
            "event: response.output_item.done\n" +
            "data: [1,2,3]\n" +                             // JSON, but not an object
            "\n" +
            CreatedBlock("resp_after_garbage");

        var received = await DriveParserAsync(parser, body, 11);

        Assert.Equal("resp_after_garbage", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_after_garbage" }, handler.InsertionOrderForTest);

        // Nothing was buffered from any of the malformed blocks, and the parser stayed alive.
        Assert.Equal(0, parser.BufferedOutputCountForTest);
        Assert.False(parser.InertForTest);
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    /// <summary>
    /// THE 8 KB LINE POLICY IS INCLUSIVE, AND TERMINATOR-BLIND: a <c>data:</c> line carrying
    /// EXACTLY 8,192 bytes of FIELD CONTENT is legal and fully retained — under the LF terminator
    /// and the CRLF terminator alike, because the terminator's bytes are not part of the content
    /// being measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is written as a LITERAL 8,192 rather than derived from the production constant:
    /// deriving it would let a mutation of that constant drag this test's expectation along with
    /// it, so the boundary would never be pinned at all.
    /// </para>
    /// <para>
    /// The CRLF row is the regression guard for the corrected arithmetic. Counting the CR toward
    /// the content limit makes this exact line 8,193 bytes and wrongly discards it — legal under
    /// LF, rejected under CRLF, for a line the contract defines identically in both forms.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task LineLimit_ExactlyEightThousandOneHundredNinetyTwoContentBytes_IsRetained(
        string terminator)
    {
        var (handler, parser) = NewParser();

        // A data: line of exactly 8,192 CONTENT bytes whose payload is the id document, padded
        // with JSON whitespace so it still parses.
        var json = CreatedDataLine("resp_exact_line")["data: ".Length..];
        var pad = new string(' ', LineContentLimitBytes - "data: ".Length - json.Length);
        var line = "data: " + json + pad;
        Assert.Equal(LineContentLimitBytes, line.Length);

        var body = CreatedEventLine + terminator + line + terminator + terminator;
        var bytes = Encoding.UTF8.GetBytes(body);

        var received = await DriveParserAsync(parser, bytes, 1021);

        // Retained in full: the id was parsed out of it and the entry written.
        Assert.Equal("resp_exact_line", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_exact_line" }, handler.InsertionOrderForTest);

        // …and charged as exactly its content, terminator excluded, whichever terminator it was.
        Assert.Equal(CreatedEventLine.Length + LineContentLimitBytes, parser.IdSearchBytesForTest);

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// ONE CONTENT BYTE OVER: a <c>data:</c> line of 8,193 content bytes is DISCARDED at the
    /// boundary — under both terminator forms — the terminator that follows resets the line
    /// buffer, and the NEXT line parses normally. The discarded bytes are never charged.
    /// </summary>
    /// <remarks>
    /// Paired with the 8,192 test above, these two fixtures differ by a single content byte, so
    /// shifting the discard boundary in either direction is caught by exactly one of them.
    /// </remarks>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task LineLimit_EightThousandOneHundredNinetyThreeContentBytes_IsDiscarded(
        string terminator)
    {
        var (handler, parser) = NewParser();

        var overLong = "data: " + new string('x', LineContentLimitBytes + 1 - "data: ".Length);
        Assert.Equal(LineContentLimitBytes + 1, overLong.Length);

        var body =
            overLong + terminator + terminator +
            CreatedEventLine + terminator + CreatedDataLine("resp_after_over") + terminator + terminator;
        var bytes = Encoding.UTF8.GetBytes(body);

        var received = await DriveParserAsync(parser, bytes, 1021);

        // The NEXT line parsed: the id was found and the entry written.
        Assert.Equal("resp_after_over", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_after_over" }, handler.InsertionOrderForTest);

        // …and the discarded line cost the id search nothing at all.
        Assert.Equal(CreatedBlockRetainedBytes("resp_after_over"), parser.IdSearchBytesForTest);

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// THE 8 KB LINE POLICY: a <c>data:</c> line longer than
    /// <see cref="ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.MaxLineBytes"/>
    /// is DISCARDED at the boundary, the terminator that follows resets the line buffer, and the
    /// NEXT line parses normally — asserted through its parsed RESULT (the committed id and the
    /// written entry), not merely through the absence of a throw. The passthrough stays byte-exact
    /// throughout.
    /// </summary>
    [Fact]
    public async Task ParserVector_LineOverEightKilobytes_IsDiscardedAndTheNextLineStillParses()
    {
        var (handler, parser) = NewParser();
        var overLong = "data: " + new string('x',
            ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.MaxLineBytes + 1);

        var bytes = Encoding.UTF8.GetBytes(overLong + "\n\n" + CreatedBlock("resp_after_long"));

        var received = await DriveParserAsync(parser, bytes, 997);

        // The next line parsed: the id was found and the entry written.
        Assert.Equal("resp_after_long", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_after_long" }, handler.InsertionOrderForTest);

        // …and the discarded line cost the id search nothing.
        Assert.Equal(CreatedBlockRetainedBytes("resp_after_long"), parser.IdSearchBytesForTest);

        // The reader received the identical stream, over-long line included.
        Assert.Equal(bytes, received);
    }

    // ── 14. The retained-byte caps ───────────────────────────────────────────

    /// <summary>
    /// Emits <c>data:</c> padding blocks whose RETAINED bytes total exactly
    /// <paramref name="totalBytes"/>: every line stays safely under the 8 KB line bound and every
    /// block is closed by a blank line, so each charges as its own complete event.
    /// </summary>
    private static string IdSearchPadding(int totalBytes)
    {
        const int lineLength = 1024;
        const int prefixLength = 6; // "data: "

        var sb = new StringBuilder();
        var remaining = totalBytes;

        while (remaining > 0)
        {
            var length = Math.Min(lineLength, remaining);
            Assert.True(length > prefixLength,
                $"padding remainder {length} cannot carry the 'data: ' prefix — pick another total.");

            sb.Append("data: ").Append('y', length - prefixLength).Append("\n\n");
            remaining -= length;
        }

        return sb.ToString();
    }

    /// <summary>
    /// THE ID CAP IS INCLUSIVE: an event block completely received at EXACTLY
    /// <see cref="ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.IdSearchByteCap"/>
    /// retained bytes is ACCEPTED — the id is extracted and the entry written.
    /// </summary>
    /// <remarks>
    /// This test and its <c>+1</c> twin differ by a SINGLE byte of padding, so an off-by-one in
    /// either direction is caught by exactly one of the two.
    /// </remarks>
    [Fact]
    public async Task IdSearchCap_BlockCompletedAtExactlyTheCap_IsAccepted()
    {
        const string id = "boundary";
        var cap = ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.IdSearchByteCap;

        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(
            IdSearchPadding(cap - CreatedBlockRetainedBytes(id)) + CreatedBlock(id));

        var received = await DriveParserAsync(parser, bytes, 4096);

        // Landed exactly on the cap — and was accepted.
        Assert.Equal(cap, parser.IdSearchBytesForTest);
        Assert.False(parser.IdSearchCapBreachedForTest);
        Assert.Equal(id, parser.CommittedIdForTest);
        Assert.Equal(new[] { id }, handler.InsertionOrderForTest);

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// ONE BYTE OVER: the same fixture with a single extra padding byte makes the created block
    /// STRADDLE the cap — its own final line pushes the total to cap + 1 — so the id search STOPS
    /// and the no-id fallback applies: NO entry under any key, ever.
    /// </summary>
    [Fact]
    public async Task IdSearchCap_BlockStraddlingTheCap_StopsTheSearchAndWritesNoEntry()
    {
        const string id = "boundary";
        var cap = ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.IdSearchByteCap;

        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(
            IdSearchPadding(cap - CreatedBlockRetainedBytes(id) + 1) + CreatedBlock(id));

        var received = await DriveParserAsync(parser, bytes, 4096);

        // The breach happened on the created block's own final line.
        Assert.True(parser.IdSearchCapBreachedForTest);
        Assert.Equal(cap + 1, parser.IdSearchBytesForTest);

        // NO FALLBACK KEY: nothing under the id, and nothing under anything else either.
        Assert.Null(parser.CommittedIdForTest);
        Assert.False(handler.TryGetConversationStateForTest(id, out _, out _));
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);

        // The passthrough is untouched by the breach.
        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// EXACT ACCOUNTING, exclusion 1 — the field-terminating newlines are NOT counted. The fixture
    /// uses CRLF, so counting terminators would add two bytes per line; the assertion pins the sum
    /// of the two line lengths with their terminators excluded.
    /// </summary>
    [Fact]
    public async Task IdSearchAccounting_ExcludesLineTerminators()
    {
        var (_, parser) = NewParser();
        var dataLine = CreatedDataLine("resp_terminators");

        await DriveParserAsync(parser, CreatedEventLine + "\r\n" + dataLine + "\r\n\r\n", 3);

        Assert.Equal("resp_terminators", parser.CommittedIdForTest);
        Assert.Equal(CreatedEventLine.Length + dataLine.Length, parser.IdSearchBytesForTest);
    }

    /// <summary>
    /// EXACT ACCOUNTING, exclusion 2 — bytes DROPPED by the 8 KB malformed-line policy are not
    /// counted: only the created block's two retained lines are charged, even though sixteen
    /// kilobytes of discarded line preceded them.
    /// </summary>
    [Fact]
    public async Task IdSearchAccounting_ExcludesBytesDroppedByTheLinePolicy()
    {
        var (_, parser) = NewParser();
        var maxLine = ChatClientFactory.CopilotResponsesHandler.StreamingResponseParser.MaxLineBytes;

        var body =
            "data: " + new string('x', maxLine * 2) + "\n\n" +
            CreatedBlock("resp_dropped");

        await DriveParserAsync(parser, body, 8192);

        Assert.Equal("resp_dropped", parser.CommittedIdForTest);
        Assert.Equal(CreatedBlockRetainedBytes("resp_dropped"), parser.IdSearchBytesForTest);
    }

    /// <summary>
    /// EXACT ACCOUNTING, exclusion 3 — bytes RELEASED once the id has been extracted are not
    /// counted: the accumulator freezes the moment the search ends, however much event data
    /// follows.
    /// </summary>
    [Fact]
    public async Task IdSearchAccounting_ExcludesBytesAfterTheIdWasExtracted()
    {
        var (_, parser) = NewParser();
        var afterExtraction =
            "event: response.output_item.done\n" +
            "data: {\"item\":{\"type\":\"message\",\"content\":\"post-id\"}}\n\n" +
            "data: " + new string('z', 4096) + "\n\n";

        await DriveParserAsync(parser, CreatedBlock("resp_frozen") + afterExtraction, 512);

        Assert.Equal("resp_frozen", parser.CommittedIdForTest);
        Assert.Equal(CreatedBlockRetainedBytes("resp_frozen"), parser.IdSearchBytesForTest);

        // The trailing traffic really did arrive — the accumulator simply stopped charging.
        Assert.Equal(1, parser.BufferedOutputCountForTest);
    }

    /// <summary>
    /// THE CONTRACTUAL 2 MB OUTPUT CAP, as a LITERAL. Deriving it from the production constant
    /// would let a mutation of that constant move the test's own expectations with it.
    /// </summary>
    private const int OutputCapBytes = 2 * 1024 * 1024;

    /// <summary>THE CONTRACTUAL 8 KB line-content limit, as a LITERAL — same reasoning.</summary>
    private const int LineContentLimitBytes = 8192;

    /// <summary>
    /// Builds ONE <c>response.output_item.done</c> event whose RAW <c>data:</c> payload bytes
    /// total EXACTLY <paramref name="payloadBytes"/>, and whose payload is VALID, parseable JSON
    /// carrying <paramref name="marker"/> as its item content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both properties are load-bearing. EXACT: the cap is a byte boundary, so a fixture that
    /// overshoots by thousands of bytes cannot tell an inclusive comparison from an exclusive one.
    /// VALID: if the payload could never parse, the item's absence from the entry proves nothing —
    /// it would be missing whether the cap dropped it or the JSON parser rejected it. This payload
    /// parses, so its absence can only be the cap's doing.
    /// </para>
    /// <para>
    /// The payload is split across several <c>data:</c> lines, each well under the 8 KB line
    /// limit, at positions between JSON array elements — so the SSE spec's newline join lands in
    /// inter-token whitespace and the concatenated document is still valid. That keeps THIS test
    /// about the output cap rather than about the line policy.
    /// </para>
    /// </remarks>
    private static string ExactPayloadOutputItemEvent(string marker, int payloadBytes)
    {
        const int elementBytes = 8000;                       // safely under the 8 KB line limit
        var head = "{\"item\":{\"content\":\"" + marker + "\",\"p\":[";
        var element = "\"" + new string('z', elementBytes - 3) + "\",";
        Assert.Equal(elementBytes, element.Length);

        var elements = 0;
        int remainder;
        while (true)
        {
            remainder = payloadBytes - head.Length - (elements * elementBytes);
            if (remainder is >= 5 and <= elementBytes) break;
            elements++;
            Assert.True(elements < 10_000, "payload target is unreachable with this element size.");
        }

        var tail = "\"" + new string('z', remainder - 5) + "\"]}}";
        Assert.Equal(remainder, tail.Length);

        var lines = new List<string> { head };
        for (var i = 0; i < elements; i++) lines.Add(element);
        lines.Add(tail);

        // The fixture must be exactly on target, and every line must clear the 8 KB limit.
        Assert.Equal(payloadBytes, lines.Sum(l => l.Length));
        Assert.All(lines, l => Assert.True(l.Length <= LineContentLimitBytes - "data: ".Length));

        // …and the joined payload must be VALID JSON, or its absence would prove nothing.
        Assert.Equal(marker, JsonNode.Parse(string.Join("\n", lines))!["item"]!["content"]!.GetValue<string>());

        var sb = new StringBuilder("event: response.output_item.done\n");
        foreach (var line in lines) sb.Append("data: ").Append(line).Append('\n');
        return sb.Append('\n').ToString();
    }

    /// <summary>One small, complete, valid <c>response.output_item.done</c> event.</summary>
    private static string SmallOutputItemEvent(string marker)
        => "event: response.output_item.done\n"
         + "data: {\"item\":{\"content\":\"" + marker + "\"}}\n\n";

    /// <summary>The RAW payload byte count <see cref="SmallOutputItemEvent"/> charges.</summary>
    private static int SmallOutputItemPayloadBytes(string marker)
        => ("{\"item\":{\"content\":\"" + marker + "\"}}").Length;

    /// <summary>
    /// THE OUTPUT CAP IS INCLUSIVE: an item whose complete event brings the buffered RAW payload
    /// total to EXACTLY 2 MB is ACCEPTED — both items land, and <c>OutputBytesForTest</c> reads
    /// exactly the cap.
    /// </summary>
    /// <remarks>
    /// This test and its <c>+1</c> twin below differ by a SINGLE payload byte, so an off-by-one in
    /// either direction — <c>&gt;</c> swapped for <c>&gt;=</c> or the reverse — is caught by
    /// exactly one of the two. The exact-byte assertion on <c>OutputBytesForTest</c> is what makes
    /// that possible: a fixture that merely overshoots the cap cannot distinguish the comparisons.
    /// </remarks>
    [Fact]
    public async Task OutputCap_ItemLandingExactlyOnTheCap_IsAcceptedWhole()
    {
        var (handler, parser) = NewParser();

        var smallBytes = SmallOutputItemPayloadBytes("kept-first");
        var body =
            CreatedBlock("resp_cap_exact") +
            SmallOutputItemEvent("kept-first") +
            ExactPayloadOutputItemEvent("kept-boundary", OutputCapBytes - smallBytes) +
            "event: response.completed\ndata: {}\n\n";

        var bytes = Encoding.UTF8.GetBytes(body);
        var received = await DriveParserAsync(parser, bytes, 65536);

        // Landed exactly on the cap, and nothing was refused.
        Assert.Equal(OutputCapBytes, parser.OutputBytesForTest);
        Assert.False(parser.OutputCapBreachedForTest);

        // BOTH items are in the entry, in arrival order — the boundary item whole.
        Assert.Equal(new[] { "kept-first", "kept-boundary" }, HistoryContents(handler, "resp_cap_exact"));

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// ONE PAYLOAD BYTE OVER: the identical fixture with a single extra byte makes the second
    /// item's complete event STRADDLE the cap, so it is DROPPED WHOLE while the item buffered
    /// before it still flushes. The dropped item's raw bytes are never charged.
    /// </summary>
    [Fact]
    public async Task OutputCap_ItemStraddlingTheCapByOneByte_IsDroppedWhole()
    {
        var (handler, parser) = NewParser();

        var smallBytes = SmallOutputItemPayloadBytes("kept-first");
        var body =
            CreatedBlock("resp_cap_over") +
            SmallOutputItemEvent("kept-first") +
            ExactPayloadOutputItemEvent("straddler", OutputCapBytes - smallBytes + 1) +
            SmallOutputItemEvent("after-breach") +
            "event: response.completed\ndata: {}\n\n";

        var bytes = Encoding.UTF8.GetBytes(body);
        var received = await DriveParserAsync(parser, bytes, 65536);

        Assert.True(parser.OutputCapBreachedForTest);

        // ONLY the item buffered before the breach was charged and amended in.
        Assert.Equal(smallBytes, parser.OutputBytesForTest);
        Assert.Equal(new[] { "kept-first" }, HistoryContents(handler, "resp_cap_over"));

        // The straddling item — whose payload is perfectly valid JSON, so only the cap can have
        // excluded it — contributed NOTHING, not even a fragment. Nor did the item after it.
        var entry = EntryJson(handler, "resp_cap_over");
        Assert.DoesNotContain("straddler", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("zzzz", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("after-breach", entry, StringComparison.Ordinal);

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// The straddling payload used above really is VALID, parseable JSON that the parser WOULD
    /// have accepted below the cap — the control that makes the drop assertion meaningful.
    /// </summary>
    /// <remarks>
    /// Without this control, "the straddler is absent" is satisfied by a payload that could never
    /// have parsed in the first place. Here the very same multi-line payload shape, at a size
    /// comfortably inside the cap, IS accepted and IS amended in.
    /// </remarks>
    [Fact]
    public async Task OutputCap_TheStraddlingPayloadShape_IsAcceptedWhenItFitsUnderTheCap()
    {
        var (handler, parser) = NewParser();

        var body =
            CreatedBlock("resp_cap_control") +
            ExactPayloadOutputItemEvent("would-fit", 64 * 1024) +
            "event: response.completed\ndata: {}\n\n";

        var received = await DriveParserAsync(parser, Encoding.UTF8.GetBytes(body), 65536);

        Assert.False(parser.OutputCapBreachedForTest);
        Assert.Equal(64 * 1024, parser.OutputBytesForTest);
        Assert.Equal(new[] { "would-fit" }, HistoryContents(handler, "resp_cap_control"));
        Assert.Equal(Encoding.UTF8.GetBytes(body), received);
    }

    // ── 15. Commit ordering and end-of-stream semantics ──────────────────────

    /// <summary>
    /// THE COMMIT HAPPENS BEFORE THE COMPLETING CHUNK RETURNS. The observation is taken from
    /// INSIDE the <c>OnChunk</c> callback — while the tee's <c>Read</c> is still on the stack —
    /// and it already sees the committed entry. Deferring the store write (for example to the
    /// terminal flush) leaves the in-callback observation empty and fails this test.
    /// </summary>
    [Fact]
    public async Task Commit_IsObservableFromInsideTheChunkCallback_BeforeTheReadReturns()
    {
        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(CreatedBlock("resp_ordering"));

        int? storeCountDuringCallback = null;
        var entryVisibleDuringCallback = false;

        using var original = new RecordingHttpContent(bytes);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original,
            chunk =>
            {
                parser.Append(chunk);

                // Still inside the read: what does the durable store hold right now?
                storeCountDuringCallback = handler.StoreCountForTest;
                entryVisibleDuringCallback =
                    handler.TryGetConversationStateForTest("resp_ordering", out _, out _);
            });

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        var buffer = new byte[bytes.Length];
        var read = stream.Read(buffer, 0, buffer.Length);

        // The whole created block was delivered by this single read…
        Assert.Equal(bytes.Length, read);

        // …and the entry was already durable while that read was still on the stack.
        Assert.True(entryVisibleDuringCallback,
            "the committed entry was not visible from inside the OnChunk callback.");
        Assert.Equal(1, storeCountDuringCallback);
    }

    /// <summary>
    /// END OF STREAM BEFORE a valid <c>response.created</c>: nothing was ever committed, so
    /// disposing the response leaves NO entry — the staged state is simply abandoned. This is why
    /// the parser needs no disposal hook.
    /// </summary>
    [Fact]
    public async Task EndOfStreamBeforeAValidCreatedEvent_LeavesNoEntry()
    {
        var (handler, parser) = NewParser();

        // A block that never completes: the id is still unannounced when the stream ends.
        var bytes = Encoding.UTF8.GetBytes(CreatedEventLine + "\n" + "data: {\"resp");

        byte[] received;
        using (var original = new RecordingHttpContent(bytes, 3))
        using (var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append))
        {
            var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            received = await DrainAsync(stream, 4096);
        }

        Assert.Null(parser.CommittedIdForTest);
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);
        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// END OF STREAM AFTER a valid <c>response.created</c>: the entry was committed before the
    /// completing chunk returned, so it survives the response's disposal untouched — no disposal
    /// hook, and no rollback.
    /// </summary>
    [Fact]
    public async Task EndOfStreamAfterAValidCreatedEvent_RetainsTheEntry()
    {
        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(CreatedBlock("resp_kept") + "event: partial\ndata: {\"a");

        using (var original = new RecordingHttpContent(bytes, 3))
        using (var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append))
        {
            var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            await DrainAsync(stream, 4096);
        }

        Assert.Equal("resp_kept", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_kept" }, handler.InsertionOrderForTest);
        Assert.True(handler.TryGetConversationStateForTest("resp_kept", out var baseInput, out var history));
        Assert.Equal("staged-base", baseInput![0]!["content"]!.GetValue<string>());
        Assert.Empty(history);
    }

    // ── 16. The production EOF signal ────────────────────────────────────────

    /// <summary>
    /// A stream backed by a body of a known length, so a caller can read it to a genuine
    /// <c>read == 0</c> and read again afterwards.
    /// </summary>
    private sealed class CountingEofContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly int _chunk;

        public CountingEofContent(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ChunkedStream(_data, _chunk));

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
            => new ChunkedStream(_data, _chunk);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        /// <summary>Hands out at most <c>chunk</c> bytes per read; 0 once exhausted, forever.</summary>
        private sealed class ChunkedStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private int _position;

            public ChunkedStream(byte[] data, int chunk)
            {
                _data = data;
                _chunk = chunk;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;

            public override long Position
            {
                get => _position;
                set => _position = (int)value;
            }

            private int Take(int max)
            {
                var n = Math.Min(Math.Min(_chunk, max), _data.Length - _position);
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = Take(count);
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override int Read(Span<byte> buffer)
            {
                var n = Take(buffer.Length);
                _data.AsSpan(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromResult(Read(buffer, offset, count));

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
                => new(Read(buffer.Span));

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A FINAL UNTERMINATED <c>response.created</c> LINE COMMITS AT EOF, ON THE PRODUCTION PATH.
    /// The tee's own end-of-stream signal — not a test calling the completion API — is what
    /// processes it, so this pins the production wiring rather than the parser in isolation.
    /// </summary>
    /// <remarks>
    /// Removing the EOF signal restores the previously-dead completion path and fails this test:
    /// the final line is never processed and no entry is written.
    /// </remarks>
    [Fact]
    public async Task ProductionEof_FinalUnterminatedCreatedLine_CommitsTheId()
    {
        var (handler, parser) = NewParser();

        // No trailing terminator at all: the data line can only be completed by end of input.
        var body = CreatedEventLine + "\n" + CreatedDataLine("resp_eof_created");
        var bytes = Encoding.UTF8.GetBytes(body);

        var received = await DriveParserToEofAsync(parser, bytes, 9);

        Assert.Equal("resp_eof_created", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_eof_created" }, handler.InsertionOrderForTest);
        Assert.True(handler.TryGetConversationStateForTest("resp_eof_created", out var baseInput, out _));
        Assert.Equal("staged-base", baseInput![0]!["content"]!.GetValue<string>());

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// A FINAL UNTERMINATED <c>response.completed</c> LINE FLUSHES THE BUFFERED OUTPUT AT EOF, on
    /// the production path: the terminal marker arrives as the stream's last, unterminated line,
    /// and the amendment still lands.
    /// </summary>
    [Fact]
    public async Task ProductionEof_FinalUnterminatedCompletedLine_FlushesBufferedOutput()
    {
        var (handler, parser) = NewParser();

        var body =
            CreatedBlock("resp_eof_completed") +
            SmallOutputItemEvent("eof-out-1") +
            SmallOutputItemEvent("eof-out-2") +
            "event: response.completed\n" +
            "data: {}";                       // final line, unterminated

        var bytes = Encoding.UTF8.GetBytes(body);
        var received = await DriveParserToEofAsync(parser, bytes, 11);

        Assert.True(parser.TerminatedForTest);
        Assert.Equal(
            new[] { "eof-out-1", "eof-out-2" }, HistoryContents(handler, "resp_eof_completed"));

        Assert.Equal(bytes, received);
    }

    /// <summary>
    /// THE NEGATIVE: disposal BEFORE end of input processes nothing. A response abandoned
    /// mid-stream never reached its end, so its final partial line must not be treated as
    /// finished — the staged state is simply abandoned and NO entry is written.
    /// </summary>
    /// <remarks>
    /// Wiring the completion path to disposal instead of (or as well as) EOF fails here: the
    /// abandoned stream's unterminated <c>response.created</c> line would commit an entry the
    /// server never finished announcing.
    /// </remarks>
    [Fact]
    public async Task ProductionEof_DisposalBeforeEndOfInput_ProcessesNothing()
    {
        var (handler, parser) = NewParser();

        var eofSignals = 0;
        var body = CreatedEventLine + "\n" + CreatedDataLine("resp_abandoned");
        var bytes = Encoding.UTF8.GetBytes(body);

        var original = new CountingEofContent(bytes, 8);
        var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append, () => { eofSignals++; parser.CompleteInput(); });

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        // ONE read only. The body is longer than a single chunk, so EOF is never observed.
        var buffer = new byte[8];
        var read = await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken);
        Assert.True(read > 0);
        Assert.True(read < bytes.Length);

        tee.Dispose();

        // Disposal raised no signal, so the final line was never processed…
        Assert.Equal(0, eofSignals);
        Assert.Null(parser.CommittedIdForTest);

        // …and nothing at all was written: the staged state is abandoned.
        Assert.Equal(0, handler.StoreCountForTest);
        Assert.Empty(handler.InsertionOrderForTest);
    }

    /// <summary>
    /// EVERY read overload raises the end-of-input signal, so a caller's choice of overload can
    /// never decide whether a final unterminated line is processed.
    /// </summary>
    public enum EofReadMode
    {
        SyncArray,
        SyncSpan,
        AsyncArray,
        AsyncMemory,
    }

    /// <summary>
    /// ALL FOUR READ OVERLOADS fire the EOF signal, each committing the same final unterminated
    /// line. A signal wired into only some overloads leaves the production path's behaviour
    /// dependent on which overload the SDK happens to use.
    /// </summary>
    [Theory]
    [InlineData(EofReadMode.SyncArray)]
    [InlineData(EofReadMode.SyncSpan)]
    [InlineData(EofReadMode.AsyncArray)]
    [InlineData(EofReadMode.AsyncMemory)]
    public async Task ProductionEof_EveryReadOverload_RaisesTheSignal(EofReadMode mode)
    {
        var (handler, parser) = NewParser();
        var bytes = Encoding.UTF8.GetBytes(CreatedEventLine + "\n" + CreatedDataLine("resp_overload"));

        using var original = new CountingEofContent(bytes, 7);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, parser.Append, parser.CompleteInput);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = new MemoryStream();
        var buffer = new byte[16];

        while (true)
        {
            var read = mode switch
            {
                EofReadMode.SyncArray => stream.Read(buffer, 0, buffer.Length),
                EofReadMode.SyncSpan => stream.Read(buffer.AsSpan()),
                EofReadMode.AsyncArray => await stream.ReadAsync(
                    buffer, 0, buffer.Length, TestContext.Current.CancellationToken),
                _ => await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken),
            };

            if (read == 0) break;
            received.Write(buffer, 0, read);
        }

        Assert.Equal("resp_overload", parser.CommittedIdForTest);
        Assert.Equal(new[] { "resp_overload" }, handler.InsertionOrderForTest);
        Assert.Equal(bytes, received.ToArray());
    }

    /// <summary>
    /// THE SIGNAL IS RAISED EXACTLY ONCE, however many times — and through whichever overloads —
    /// the reader keeps reading at end of stream. A repeated signal would re-run the completion
    /// path over already-consumed state.
    /// </summary>
    [Fact]
    public async Task ProductionEof_RepeatedReadsAtEndOfStream_RaiseExactlyOneSignal()
    {
        var signals = 0;

        using var original = new CountingEofContent(Encoding.UTF8.GetBytes("event: x\ndata: 1\n\n"), 4);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, null, () => signals++);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[8];

        // Drain to the first read == 0…
        while (await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken) > 0) { }
        Assert.Equal(1, signals);

        // …then keep reading at EOF through every overload.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
            Assert.Equal(0, stream.Read(buffer.AsSpan()));
            Assert.Equal(0, await stream.ReadAsync(
                buffer, 0, buffer.Length, TestContext.Current.CancellationToken));
            Assert.Equal(0, await stream.ReadAsync(
                buffer.AsMemory(), TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, signals);
    }

    /// <summary>
    /// A THROWING EOF OBSERVER IS CONTAINED and the hook DISABLED, exactly like the chunk seam: a
    /// broken parser can never break the response, and the reader still receives every byte.
    /// </summary>
    [Fact]
    public async Task ProductionEof_ThrowingObserver_IsContainedAndDisabled()
    {
        var invocations = 0;
        var body = Encoding.UTF8.GetBytes("event: delta\ndata: 12345\n\n");

        using var original = new CountingEofContent(body, 5);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(
            original, null, () =>
            {
                invocations++;
                throw new InvalidOperationException("eof observer exploded");
            });

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        // The throw never escapes the read…
        var received = await DrainAsync(stream, 4096);

        Assert.Equal(1, invocations);

        // …the hook was dropped rather than retried…
        Assert.Null(tee.OnEof);

        // …and the passthrough is byte-exact.
        Assert.Equal(body, received);
    }

    /// <summary>
    /// THE NULL DEFAULT IS A PURE NO-OP: with no EOF observer the stream behaves exactly as it did
    /// before the seam existed — byte-exact passthrough, and end of stream reported normally.
    /// </summary>
    [Fact]
    public async Task ProductionEof_NullObserver_ChangesNothingObservable()
    {
        var body = Encoding.UTF8.GetBytes("event: delta\ndata: 12345\n\n");

        using var original = new RecordingHttpContent(body, 6);
        using var tee = new ChatClientFactory.CopilotResponsesHandler.TeeStreamContent(original);

        Assert.Null(tee.OnEof);

        var stream = await tee.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = await DrainAsync(stream, 4096);

        Assert.Equal(body, received);
        Assert.Equal(0, original.InnerStream.Remaining);

        // Reading on at end of stream keeps reporting 0, with no observer to notice.
        Assert.Equal(0, stream.Read(new byte[8], 0, 8));
    }

    /// <summary>
    /// THE HANDLER WIRES BOTH SEAMS. Driven through the real handler, a successful SSE response
    /// whose LAST line is an unterminated <c>response.created</c> commits once the caller consumes
    /// the body to its end — and not before.
    /// </summary>
    [Fact]
    public async Task Handler_WiresEndOfInput_SoAFinalUnterminatedLineCommits()
    {
        var body = Encoding.UTF8.GetBytes(
            "event: response.created\ndata: {\"response\":{\"id\":\"resp_handler_eof\"}}");

        var terminal = new RecordingSseTerminalHandler(body, 5);
        var handler = new ChatClientFactory.CopilotResponsesHandler(terminal);
        using var invoker = new HttpMessageInvoker(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"stream":true,"input":[{"role":"user","content":"hi"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };

        var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // Nothing is committed while the response sits unconsumed.
        Assert.Equal(0, handler.StoreCountForTest);

        var received = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        // Consuming to the end processed the final unterminated line and committed the entry.
        Assert.True(handler.TryGetConversationStateForTest(
            "resp_handler_eof", out var baseInput, out var history));
        Assert.Equal(1, baseInput?.Count);
        Assert.Empty(history);

        Assert.Equal(body, received);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks until the dedicated synchronous racing thread has PROVABLY entered the tee's
    /// acquisition wait — or until it resolves the race some other, contract-violating way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thread's body contains nothing but the read, so once the runtime reports it as blocked
    /// (<see cref="System.Threading.ThreadState.WaitSleepJoin"/>) it can only be blocked inside the
    /// acquisition the tee made it wait on. That is the decisive edge this helper exists to
    /// establish: the caller may then take its mid-race assertions, and release the parked
    /// acquisition, knowing the sync reader is already inside the contested region rather than
    /// still queued to enter it.
    /// </para>
    /// <para>
    /// It returns on the FIRST of three edges so a violation is reported immediately instead of
    /// waiting out the bound: the reader blocked on the acquisition (the contract), the reader
    /// having COMPLETED already (it never contended — it took a path of its own), or a competing
    /// pull entering the fake content (an outright second acquisition). The last two leave the
    /// caller's assertions to fail loudly, which is exactly what should happen.
    /// </para>
    /// <para>
    /// This is the one place a spin is unavoidable — thread-blocked-ness is not an awaitable event
    /// — so it yields rather than sleeping on a fixed interval, and it is bounded by
    /// <see cref="GateTimeout"/> so a broken guard fails by name instead of hanging.
    /// </para>
    /// </remarks>
    private static async Task WaitForSyncRacerToEnterAcquisitionAsync(
        Thread syncThread, Task syncResult, Task competingPullEntered)
    {
        var deadline = Environment.TickCount64 + (long)GateTimeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (syncResult.IsCompleted || competingPullEntered.IsCompleted) return;

            // Blocked with nothing left to run but the read == inside the acquisition wait.
            if ((syncThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0) return;

            if (!syncThread.IsAlive) return;

            await Task.Yield();
        }

        Assert.Fail(
            "the synchronous racing reader never entered the tee's acquisition wait within the gate timeout.");
    }

    /// <summary>
    /// Upper bound on every rendezvous in the race tests. Nothing here waits on timing — each
    /// await is released by an explicit signal — so this bound is only ever reached when the
    /// contract under test is broken, turning a would-be hang into a named failure.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Which <see cref="Stream"/> read overload a drain should exercise.</summary>
    public enum ReadMode
    {
        SyncArray,
        SyncSpan,
        AsyncArray,
        AsyncMemory,
    }

    /// <summary>Reads <paramref name="stream"/> to the end, one read at a time.</summary>
    private static Task<byte[]> DrainAsync(Stream stream, int bufferSize)
        => DrainAsync(stream, bufferSize, ReadMode.AsyncMemory);

    /// <summary>
    /// Reads <paramref name="stream"/> to the end, one read at a time, through the requested
    /// overload.
    /// </summary>
    private static async Task<byte[]> DrainAsync(Stream stream, int bufferSize, ReadMode mode)
    {
        var buffer = new byte[bufferSize];
        var sink = new MemoryStream();

        while (true)
        {
            var read = mode switch
            {
                ReadMode.SyncArray => stream.Read(buffer, 0, buffer.Length),
                ReadMode.SyncSpan => stream.Read(buffer.AsSpan()),
                ReadMode.AsyncArray => await stream.ReadAsync(
                    buffer, 0, buffer.Length, TestContext.Current.CancellationToken),
                _ => await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken),
            };

            if (read == 0) break;
            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }

    /// <summary>Records every chunk the tee offers, copying it (pooled buffers are transient).</summary>
    private sealed class ChunkRecorder
    {
        private readonly List<byte> _bytes = new();
        private readonly List<int> _sizes = new();

        public IReadOnlyList<int> ChunkSizes => _sizes;
        public byte[] Bytes => _bytes.ToArray();

        public void Record(ReadOnlyMemory<byte> chunk)
        {
            _sizes.Add(chunk.Length);
            _bytes.AddRange(chunk.ToArray());
        }
    }

    /// <summary>
    /// A read-only stream that hands out data in a SCRIPTED chunk pattern and records exactly how
    /// it was used, so chunk laziness and read patterns are observable.
    /// </summary>
    /// <remarks>
    /// Once the script is exhausted its last entry repeats. A read never returns more than the
    /// scripted chunk size, so a caller's large buffer cannot mask a coalescing tee.
    /// </remarks>
    private sealed class ScriptedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int[] _script;
        private int _position;
        private int _readIndex;

        public ScriptedReadStream(byte[] data, int[] script)
        {
            _data = data;
            _script = script.Length == 0 ? new[] { int.MaxValue } : script;
        }

        public int ReadCallCount { get; private set; }
        public int BytesRead => _position;
        public int Remaining => _data.Length - _position;
        public int DisposeCount { get; private set; }

        /// <summary>
        /// Set by the owning content while it is disposing itself, so a test can tell whether a
        /// stream disposal came from the OWNER (legitimate) or directly from the tee (forbidden).
        /// </summary>
        public bool OwnerIsDisposing { get; set; }

        /// <summary>Whether every disposal so far happened inside the owner's own disposal.</summary>
        public bool DisposedWhileOwnerDisposing { get; private set; } = true;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => (int)offset,
                SeekOrigin.Current => _position + (int)offset,
                _ => _data.Length + (int)offset,
            };
            return _position;
        }

        private int NextCount(int max)
        {
            ReadCallCount++;
            var scripted = _script[Math.Min(_readIndex, _script.Length - 1)];
            _readIndex++;
            return Math.Min(Math.Min(scripted, max), Remaining);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = NextCount(count);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = NextCount(buffer.Length);
            _data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                if (!OwnerIsDisposing) DisposedWhileOwnerDisposing = false;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that counts stream acquisitions and its own disposals. It owns
    /// the underlying stream — exactly like a real response content — and disposes it as part of
    /// its own disposal, flagging the stream while it does so, so a test can distinguish an
    /// owner-driven stream disposal from a forbidden direct one by the tee.
    /// </summary>
    private sealed class RecordingHttpContent : HttpContent
    {
        private readonly byte[] _data;

        public RecordingHttpContent(byte[] data, params int[] chunkScript)
        {
            _data = data;
            InnerStream = new ScriptedReadStream(data, chunkScript);
        }

        public ScriptedReadStream InnerStream { get; }
        public int StreamAccessCount { get; private set; }
        public int DisposeCount { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamAccessCount++;
            return Task.FromResult<Stream>(InnerStream);
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            StreamAccessCount++;
            return InnerStream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data, 0, _data.Length);

        protected override void SerializeToStream(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => stream.Write(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;

                // Owner-driven stream disposal (HttpContent's own Dispose tears down the content
                // read stream it handed out), flagged so a forbidden DIRECT disposal by the tee
                // stays distinguishable from this legitimate one.
                InnerStream.OwnerIsDisposing = true;
            }

            base.Dispose(disposing);

            if (disposing) InnerStream.OwnerIsDisposing = false;
        }
    }

    /// <summary>Content whose stream access always throws — the lazy-acquisition probe.</summary>
    private sealed class ThrowingStreamAccessContent : HttpContent
    {
        public int StreamAccessCount { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamAccessCount++;
            throw new InvalidOperationException("stream accessed");
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            StreamAccessCount++;
            throw new InvalidOperationException("stream accessed");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("stream accessed");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A wrapped stream built entirely out of DECISIVE answers, so that read-side delegation can
    /// be proved rather than assumed.
    /// </summary>
    /// <remarks>
    /// Every value it reports is one no plausible stub would produce by accident: a length of
    /// 987,654,321, a starting position of 31,337, and a <c>Seek</c> that answers 4,242 whatever
    /// it is asked — deliberately unequal to the offset, the origin-derived position and zero
    /// alike. It also records the exact arguments it was handed, so forwarding is observable, and
    /// its capability flags are settable so the tee can be shown to follow them rather than
    /// hardcode them. Reads yield one byte at a time, which keeps the position arithmetic exact.
    /// </remarks>
    private sealed class DelegationProbeStream : Stream
    {
        public const long DecisiveLength = 987_654_321L;
        public const long DecisiveInitialPosition = 31_337L;
        public const long DecisiveSeekResult = 4_242L;

        private long _position = DecisiveInitialPosition;

        public bool ReportCanRead { get; set; } = true;
        public bool ReportCanSeek { get; set; } = true;

        public long? LastPositionSet { get; private set; }
        public long? LastSeekOffset { get; private set; }
        public SeekOrigin? LastSeekOrigin { get; private set; }
        public int ReadCallCount { get; private set; }
        public int DisposeCount { get; private set; }

        public override bool CanRead => ReportCanRead;
        public override bool CanSeek => ReportCanSeek;
        public override bool CanWrite => false;
        public override long Length => DecisiveLength;

        public override long Position
        {
            get => _position;
            set
            {
                LastPositionSet = value;
                _position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            LastSeekOffset = offset;
            LastSeekOrigin = origin;
            return DecisiveSeekResult;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            ReadCallCount++;
            buffer[offset] = 0xAB;
            _position++;
            return 1;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            ReadCallCount++;
            buffer[0] = 0xAB;
            _position++;
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Minimal content that hands out a caller-supplied stream and counts the acquisitions, for
    /// tests that care about the wrapped stream's own behaviour rather than its bytes.
    /// </summary>
    private sealed class StreamProbeContent : HttpContent
    {
        private readonly Stream _stream;

        public StreamProbeContent(Stream stream) => _stream = stream;

        public int StreamAccessCount { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamAccessCount++;
            return Task.FromResult(_stream);
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            StreamAccessCount++;
            return _stream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A thread-safe scripted stream that also records WHICH instance served each read, so
    /// same-instance reuse under a race can be proved by identity.
    /// </summary>
    private sealed class RaceProbeStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly object _lock = new();
        private int _position;
        private int _readCallCount;

        public RaceProbeStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public int ReadCallCount => Volatile.Read(ref _readCallCount);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get { lock (_lock) { return _position; } }
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            lock (_lock)
            {
                _readCallCount++;
                var n = Math.Max(0, Math.Min(Math.Min(_chunkSize, buffer.Length), _data.Length - _position));
                _data.AsSpan(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Content whose stream acquisition PARKS on an explicit gate until the test releases it, so
    /// competing first reads are guaranteed to overlap instead of merely being likely to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It counts every pull and hands out a DISTINCT stream instance per pull, so a second
    /// acquisition is detectable by identity as well as by the counter.
    /// <see cref="AcquisitionEntered"/> fires when the first pull has begun and parked, which is
    /// what lets a test start its second reader inside a window that is genuinely open.
    /// </para>
    /// <para>
    /// EVERY pull parks on the same gate, including a competing second one. A guard that let a
    /// second pull through therefore leaves that pull visibly parked — <see cref="StreamAccessCount"/>
    /// reads 2 while the race is still open — instead of quietly completing behind the test's
    /// back. <see cref="CompetingPullEntered"/> is completed by any pull after the first, so a
    /// violation announces itself the moment it happens.
    /// </para>
    /// </remarks>
    private sealed class GatedAcquisitionContent : HttpContent
    {
        public const string FailureMessage = "gated acquisition failed";

        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly object _lock = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _competingPullEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<RaceProbeStream> _createdStreams = new();
        private int _streamAccessCount;

        public GatedAcquisitionContent(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        /// <summary>When set, the first pull throws instead of returning a stream.</summary>
        public bool FailFirstAcquisition { get; init; }

        /// <summary>Completes once the FIRST pull has begun and parked on the gate.</summary>
        public TaskCompletionSource AcquisitionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes if any pull BEYOND the first is ever started — i.e. the exactly-once guard
        /// let a competing acquisition through. Under the contract this never completes.
        /// </summary>
        public Task CompetingPullEntered => _competingPullEntered.Task;

        public int StreamAccessCount => Volatile.Read(ref _streamAccessCount);

        /// <summary>Every stream instance this content has ever created, in creation order.</summary>
        public IReadOnlyList<RaceProbeStream> CreatedStreams
        {
            get { lock (_lock) { return _createdStreams.ToArray(); } }
        }

        /// <summary>The most recent stream instance handed to the tee.</summary>
        public RaceProbeStream? LastStreamHandedToTee
        {
            get { lock (_lock) { return _createdStreams.Count == 0 ? null : _createdStreams[^1]; } }
        }

        /// <summary>Releases every parked acquisition.</summary>
        public void ReleaseAcquisition() => _release.TrySetResult();

        private RaceProbeStream NewStream()
        {
            var stream = new RaceProbeStream(_data, _chunkSize);
            lock (_lock) { _createdStreams.Add(stream); }
            return stream;
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _streamAccessCount);

            if (attempt == 1)
            {
                // The first pull parks here, holding the acquisition open for the whole race.
                AcquisitionEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);

                if (FailFirstAcquisition) throw new InvalidOperationException(FailureMessage);
                return NewStream();
            }

            // Any pull beyond the first is a violation of exactly-once — announce it at once, then
            // park on the same gate so the test can observe the breach while the race is open.
            // Once the gate has been released the race is over, so a later pull (the documented
            // retry after a faulted attempt) proceeds immediately.
            _competingPullEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return NewStream();
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
            => CreateContentReadStreamAsync(cancellationToken).GetAwaiter().GetResult();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// Terminal handler returning a successful SSE response backed by a
    /// <see cref="RecordingHttpContent"/>, so the handler-return contract is observable.
    /// </summary>
    private sealed class RecordingSseTerminalHandler : HttpMessageHandler
    {
        public RecordingSseTerminalHandler(byte[] body, params int[] chunkScript)
        {
            SseContent = new RecordingHttpContent(body, chunkScript);
            SseContent.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream; charset=utf-8");
        }

        public RecordingHttpContent SseContent { get; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = SseContent });
    }
}

/// <summary>
/// Tests for <see cref="ChatClientFactory.OwnedCopilotChatClient"/>: the decorator that owns
/// disposal of both the inner <see cref="IChatClient"/> and the injected <see cref="HttpClient"/>,
/// and for the <see cref="ChatClientFactory.CreateCopilotClientForTestFull"/> test seam that builds
/// the full production Copilot stack (handler chain + owned client).
/// </summary>
/// <remarks>
/// Every test in this class is free of process-wide state mutation, so the class stays safely
/// parallelizable. The behavioural proof that the seam has no token dependency lives here too
/// (<see cref="OwnedCopilotChatClientTests.CreateCopilotClientForTestFull_ContributesNoCredentialMaterial"/>)
/// and needs no environment mutation; the complementary cleared-environment variant lives in
/// <see cref="CopilotClientSeamTokenIndependenceTests"/>, which joins the serialized
/// <c>EnvVarMutation</c> collection precisely because it does mutate the process environment.
/// </remarks>
public sealed class OwnedCopilotChatClientTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    /// <summary>A single recorded call to one of the two chat entry points.</summary>
    private sealed record ChatCall(
        IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken CancellationToken);

    /// <summary>
    /// Records every call to every entry point <b>independently</b>, so a decorator that drops one
    /// entry point (or answers it without consulting the inner client) cannot hide behind another
    /// entry point's recording.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatCall> UnaryCalls { get; } = [];
        public List<ChatCall> StreamingCalls { get; } = [];
        public List<(Type ServiceType, object? ServiceKey)> ServiceRequests { get; } = [];

        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("recording", new Uri("https://example.invalid"), "test-model");

        /// <summary>The exact instance <see cref="GetResponseAsync"/> hands back.</summary>
        public ChatResponse UnaryResponse { get; } = new(new ChatMessage(ChatRole.Assistant, "unary-response"));

        /// <summary>The exact update instances <see cref="GetStreamingResponseAsync"/> yields, in order.</summary>
        public ChatResponseUpdate[] StreamingUpdates { get; } =
        [
            new(ChatRole.Assistant, "streaming-update-1"),
            new(ChatRole.Assistant, "streaming-update-2"),
        ];

        /// <summary>A sentinel service instance returned only for a specific type+key pair.</summary>
        public object KeyedService { get; } = new();

        /// <summary>The key <see cref="KeyedService"/> is registered under.</summary>
        public static readonly object ServiceKeySentinel = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            UnaryCalls.Add(new ChatCall(messages, options, cancellationToken));
            return Task.FromResult(UnaryResponse);
        }

        // Deliberately NOT an async iterator: the call must be recorded when the method is invoked,
        // so a decorator that returns an unrelated (or empty) stream is still observed as "did not
        // forward" rather than silently producing no recording at all.
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            StreamingCalls.Add(new ChatCall(messages, options, cancellationToken));
            return EnumerateAsync();
        }

        private async IAsyncEnumerable<ChatResponseUpdate> EnumerateAsync()
        {
            foreach (var update in StreamingUpdates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ServiceRequests.Add((serviceType, serviceKey));

            if (ReferenceEquals(serviceKey, ServiceKeySentinel))
                return serviceType == typeof(object) ? KeyedService : null;

            if (serviceKey is not null) return null;
            if (serviceType == typeof(ChatClientMetadata)) return Metadata;
            if (serviceType == typeof(RecordingChatClient)) return this;
            return null;
        }

        public void Dispose()
        {
            DisposeCount++;
            Disposed = true;
        }
    }

    /// <summary>
    /// Throws a freshly-created exception from a distinctly named, non-inlined frame, recording the
    /// instance first. This makes BOTH halves of the propagation contract testable:
    /// <list type="bullet">
    /// <item><description><b>Identity</b> — <see cref="Thrown"/> can be compared with
    /// <c>Assert.Same</c>, so a re-wrapped or substituted exception fails.</description></item>
    /// <item><description><b>Stack preservation</b> — <see cref="OriginFrame"/> must still be on the
    /// propagated exception's stack. This is exactly what the production
    /// <c>ExceptionDispatchInfo</c> rethrow (and the bare <c>throw;</c> in the construction-failure
    /// path) guarantees; a stack-resetting <c>throw ex;</c> would drop this frame.</description></item>
    /// </list>
    /// The exception is created <em>at</em> the throw site rather than pre-created and stored,
    /// because <c>throw storedException;</c> resets the stack trace before the production code ever
    /// observes it, which would make the stack assertion vacuous.
    /// </summary>
    private sealed class SentinelThrower
    {
        private readonly string _message;

        public SentinelThrower(string message) => _message = message;

        /// <summary>The exact exception instance that was thrown, or null if not yet thrown.</summary>
        public Exception? Thrown { get; private set; }

        /// <summary>The frame name that must survive on the propagated exception's stack.</summary>
        public static string OriginFrame => nameof(ThrowFromSentinelOrigin);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void ThrowFromSentinelOrigin()
        {
            var ex = new InvalidOperationException(_message);
            Thrown = ex;
            throw ex;
        }
    }

    /// <summary>
    /// Asserts that <paramref name="thrown"/> is the very instance the sentinel raised AND that the
    /// sentinel's original throw site is still on its stack.
    /// </summary>
    private static void AssertOriginalFailureSurvived(SentinelThrower sentinel, Exception thrown)
    {
        Assert.NotNull(sentinel.Thrown);
        Assert.Same(sentinel.Thrown, thrown);
        Assert.NotNull(thrown.StackTrace);
        Assert.Contains(SentinelThrower.OriginFrame, thrown.StackTrace);
    }

    /// <summary>An inner client whose disposal throws from a sentinel origin frame.</summary>
    private sealed class ThrowingDisposalChatClient : IChatClient
    {
        public SentinelThrower Sentinel { get; }

        public ThrowingDisposalChatClient(string message = "inner disposal failed")
            => Sentinel = new SentinelThrower(message);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Sentinel.ThrowFromSentinelOrigin();
    }

    /// <summary>
    /// Terminal handler whose disposal records the attempt and then throws from a sentinel origin
    /// frame, so "cleanup was attempted" and "cleanup failed" can both be asserted.
    /// </summary>
    private sealed class ThrowingDisposalTerminalHandler : HttpMessageHandler
    {
        public SentinelThrower Sentinel { get; }

        public ThrowingDisposalTerminalHandler(string message = "transport disposal failed")
            => Sentinel = new SentinelThrower(message);

        /// <summary>Set before the failure is raised, proving disposal was actually attempted.</summary>
        public bool DisposeAttempted { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            DisposeAttempted = true;
            Sentinel.ThrowFromSentinelOrigin();
        }
    }

    /// <summary>Tracks whether the terminal handler was disposed (chain disposal reached it).</summary>
    private sealed class TrackingTerminalHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Terminal handler that records the outgoing request body <em>and</em> its
    /// <c>Authorization</c> header, then answers with a canned JSON body.
    /// </summary>
    private sealed class ProbeTerminalHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public ProbeTerminalHandler(string responseBody = "{}") => _responseBody = responseBody;

        public string? LastBody { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.Authorization;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An inner client that actually sends a raw request through the supplied
    /// <see cref="HttpClient"/>, so the production handler chain inside it is exercised for real.
    /// </summary>
    private sealed class HttpProbeChatClient : IChatClient
    {
        private readonly HttpClient _httpClient;

        public HttpProbeChatClient(HttpClient httpClient) => _httpClient = httpClient;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
            {
                Content = new StringContent(
                    """{"model":"gpt-5","reasoning":{"effort":"extra_high"},"input":[]}""",
                    Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── Delegation ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GetResponseAsync</c> forwards the exact messages, options and cancellation token to the
    /// inner client and hands back the inner client's exact response — and does not touch the
    /// streaming entry point.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_ForwardsEveryArgumentAndResultToInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        var messages = new List<ChatMessage> { new(ChatRole.User, "unary-question") };
        var options = new ChatOptions { ModelId = "gpt-5" };
        using var cts = new CancellationTokenSource();

        var response = await wrapper.GetResponseAsync(messages, options, cts.Token);

        // Exactly one unary call, and the streaming path was not used as a substitute.
        var call = Assert.Single(inner.UnaryCalls);
        Assert.Empty(inner.StreamingCalls);

        Assert.Same(messages, call.Messages);
        Assert.Same(options, call.Options);
        Assert.Equal(cts.Token, call.CancellationToken);
        Assert.NotEqual(CancellationToken.None, call.CancellationToken);

        // The inner client's exact response instance is handed back unmodified.
        Assert.Same(inner.UnaryResponse, response);
    }

    /// <summary>
    /// <c>GetStreamingResponseAsync</c> forwards the exact messages, options and cancellation token
    /// and yields the inner client's exact update sequence — and does not touch the unary entry
    /// point. A decorator that returned an unrelated or empty stream would fail here.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ForwardsEveryArgumentAndUpdatesFromInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        var messages = new List<ChatMessage> { new(ChatRole.User, "streaming-question") };
        var options = new ChatOptions { ModelId = "gpt-5-streaming" };
        using var cts = new CancellationTokenSource();

        var received = new List<ChatResponseUpdate>();
        await foreach (var update in wrapper.GetStreamingResponseAsync(messages, options, cts.Token))
            received.Add(update);

        // Exactly one streaming call, and the unary path was not used as a substitute.
        var call = Assert.Single(inner.StreamingCalls);
        Assert.Empty(inner.UnaryCalls);

        Assert.Same(messages, call.Messages);
        Assert.Same(options, call.Options);
        Assert.Equal(cts.Token, call.CancellationToken);
        Assert.NotEqual(CancellationToken.None, call.CancellationToken);

        // The inner client's exact updates, in order — not a substituted or empty stream.
        Assert.Equal(inner.StreamingUpdates.Length, received.Count);
        for (var i = 0; i < received.Count; i++)
            Assert.Same(inner.StreamingUpdates[i], received[i]);
    }

    /// <summary>
    /// <c>GetService</c> forwards both the service type and a non-null service key verbatim, and
    /// returns the inner client's exact answer for each.
    /// </summary>
    [Fact]
    public void GetService_ForwardsTypeAndNonNullKeyToInner()
    {
        var inner = new RecordingChatClient();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new TrackingTerminalHandler()));

        // Keyless resolution.
        Assert.Same(inner.Metadata, wrapper.GetService(typeof(ChatClientMetadata)));
        Assert.Same(inner, wrapper.GetService(typeof(RecordingChatClient)));

        // Keyed resolution: the sentinel key must reach the inner client unchanged, and the
        // keyed answer must come back unchanged.
        Assert.Same(inner.KeyedService, wrapper.GetService(typeof(object), RecordingChatClient.ServiceKeySentinel));

        // A key that the inner client does not recognise still round-trips (null answer forwarded).
        Assert.Null(wrapper.GetService(typeof(string), "unknown-key"));

        Assert.Equal(
            [
                (typeof(ChatClientMetadata), (object?)null),
                (typeof(RecordingChatClient), (object?)null),
                (typeof(object), RecordingChatClient.ServiceKeySentinel),
                (typeof(string), (object?)"unknown-key"),
            ],
            inner.ServiceRequests);
    }

    /// <summary>
    /// The decorator declares no <c>Metadata</c> property: <see cref="IChatClient"/> has none, and
    /// callers must resolve metadata through <c>GetService(typeof(ChatClientMetadata))</c>.
    /// </summary>
    [Fact]
    public void OwnedCopilotChatClient_DeclaresNoMetadataProperty()
    {
        Assert.Null(typeof(ChatClientFactory.OwnedCopilotChatClient).GetProperty(
            "Metadata",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static));
    }

    // ── Dispose semantics ───────────────────────────────────────────────────

    /// <summary>Disposing the wrapper disposes both the inner client and the owned HttpClient.</summary>
    [Fact]
    public void Dispose_DisposesInnerAndOwnedHttpClient()
    {
        var inner = new RecordingChatClient();
        var terminal = new TrackingTerminalHandler();
        using var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        wrapper.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed, "Disposing the owned HttpClient must reach the handler chain.");
    }

    /// <summary>
    /// Dispose is idempotent: the inner client and the HttpClient (via its terminal handler)
    /// are each disposed <b>exactly once</b> even when Dispose is called multiple times.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var inner = new RecordingChatClient();
        var terminal = new TrackingTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        wrapper.Dispose();
        wrapper.Dispose();
        wrapper.Dispose();

        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed);
        // The Interlocked guard in OwnedCopilotChatClient.Dispose must ensure each cleanup
        // runs exactly once, even across repeated calls.
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, terminal.DisposeCount);
    }

    /// <summary>
    /// An inner-client disposal failure must not prevent the owned HttpClient from being disposed,
    /// and the original exception <b>instance</b> — with its original stack — must surface.
    /// </summary>
    [Fact]
    public void Dispose_InnerThrows_StillDisposesHttpClientAndRethrowsSameInstance()
    {
        var inner = new ThrowingDisposalChatClient();
        var terminal = new TrackingTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<InvalidOperationException>(wrapper.Dispose);

        // Identity + original stack: a re-wrapped, substituted or stack-resetting rethrow fails.
        AssertOriginalFailureSurvived(inner.Sentinel, thrown);
        Assert.Equal("inner disposal failed", thrown.Message);

        Assert.True(terminal.Disposed, "The owned HttpClient must still be disposed when the inner client throws.");
    }

    /// <summary>
    /// A transport-only failure must still surface — as the same instance, with its original
    /// stack — when the inner client disposes cleanly.
    /// </summary>
    [Fact]
    public void Dispose_HttpClientThrows_RethrowsSameInstanceAndStillDisposesInner()
    {
        var inner = new RecordingChatClient();
        var terminal = new ThrowingDisposalTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<InvalidOperationException>(wrapper.Dispose);

        AssertOriginalFailureSurvived(terminal.Sentinel, thrown);
        Assert.Equal("transport disposal failed", thrown.Message);
        Assert.True(inner.Disposed, "The inner client must still be disposed when the transport throws.");
    }

    /// <summary>
    /// When BOTH cleanups fail, both original exception <b>instances</b> must be carried by the
    /// <see cref="AggregateException"/>, with the inner client's failure first.
    /// </summary>
    [Fact]
    public void Dispose_BothThrow_AggregatesBothOriginalInstances()
    {
        var inner = new ThrowingDisposalChatClient();
        var terminal = new ThrowingDisposalTerminalHandler();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(inner, new HttpClient(terminal));

        var thrown = Assert.Throws<AggregateException>(wrapper.Dispose);

        Assert.Equal(2, thrown.InnerExceptions.Count);

        // Both original instances, in order (inner/primary first), with their stacks intact.
        AssertOriginalFailureSurvived(inner.Sentinel, thrown.InnerExceptions[0]);
        AssertOriginalFailureSurvived(terminal.Sentinel, thrown.InnerExceptions[1]);
        Assert.Equal("inner disposal failed", thrown.InnerExceptions[0].Message);
        Assert.Equal("transport disposal failed", thrown.InnerExceptions[1].Message);
    }

    /// <summary>Once disposal has run (even with a failure), later calls are no-ops.</summary>
    [Fact]
    public void Dispose_AfterFailure_IsIdempotent()
    {
        var inner = new ThrowingDisposalChatClient();
        var wrapper = new ChatClientFactory.OwnedCopilotChatClient(
            inner, new HttpClient(new ThrowingDisposalTerminalHandler()));

        Assert.Throws<AggregateException>(wrapper.Dispose);
        wrapper.Dispose(); // must not throw again
    }

    // ── CreateCopilotClientForTestFull ──────────────────────────────────────

    /// <summary>
    /// The test seam builds the FULL production stack: the owned client over the production handler
    /// chain, with the injectable inner-client factory receiving the constructed HttpClient.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_BuildsFullProductionStack()
    {
        HttpClient? factoryHttpClient = null;
        var terminal = new TrackingTerminalHandler();
        var inner = new RecordingChatClient();

        var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: false, terminal,
            httpClient =>
            {
                factoryHttpClient = httpClient;
                return inner;
            });

        Assert.IsType<ChatClientFactory.OwnedCopilotChatClient>(client);
        Assert.NotNull(factoryHttpClient);
        Assert.Equal(Timeout.InfiniteTimeSpan, factoryHttpClient!.Timeout);

        // Disposing the wrapper disposes both the inner client and the owned HttpClient.
        client.Dispose();
        Assert.True(inner.Disposed);
        Assert.True(terminal.Disposed);

        // Dispose is idempotent even after a successful disposal.
        client.Dispose();
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, terminal.DisposeCount);
    }

    /// <summary>
    /// The full chain (resilience → Copilot handler → reasoning-effort mapping with
    /// <see cref="ChatClientFactory.CopilotExtraHighMapping"/>) is present and live: a real request
    /// sent through the owned HttpClient reaches the terminal with <c>"xhigh"</c> for ExtraHigh.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTestFull_ExtraHigh_ReachesTerminalAsXhigh()
    {
        var terminal = new ProbeTerminalHandler("""{"output":[]}""");
        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: true, terminal, httpClient => new HttpProbeChatClient(httpClient));

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(terminal.LastBody!)!;
        Assert.Equal("xhigh", sent["reasoning"]!["effort"]!.GetValue<string>());
    }

    /// <summary>
    /// The seam has no token dependency, proven without mutating any process-wide state: it
    /// contributes <b>no credential material at all</b>. The <see cref="HttpClient"/> it hands to
    /// the factory carries no default headers (in particular no <c>Authorization</c>) and no
    /// <see cref="HttpClient.BaseAddress"/>, and a real request driven through the entire chain
    /// reaches the terminal handler without an <c>Authorization</c> header. Nothing in the seam can
    /// therefore be reading — let alone requiring — <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTestFull_ContributesNoCredentialMaterial()
    {
        var terminal = new ProbeTerminalHandler("""{"output":[]}""");
        HttpClient? factoryHttpClient = null;

        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: true, terminal,
            httpClient =>
            {
                factoryHttpClient = httpClient;
                return new HttpProbeChatClient(httpClient);
            });

        Assert.NotNull(factoryHttpClient);
        Assert.Null(factoryHttpClient!.DefaultRequestHeaders.Authorization);
        Assert.Empty(factoryHttpClient.DefaultRequestHeaders);
        Assert.Null(factoryHttpClient.BaseAddress);

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(terminal.LastBody);
        Assert.Null(terminal.LastAuthorization);
    }

    /// <summary>
    /// Factory failure must dispose the constructed HttpClient and propagate the <b>same</b>
    /// exception instance, with its original stack intact.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryThrows_DisposesHttpClientAndPropagatesSameInstance()
    {
        var terminal = new TrackingTerminalHandler();
        var factorySentinel = new SentinelThrower("factory boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal,
                _ =>
                {
                    factorySentinel.ThrowFromSentinelOrigin();
                    throw new UnreachableException();
                }));

        AssertOriginalFailureSurvived(factorySentinel, thrown);
        Assert.Equal("factory boom", thrown.Message);
        Assert.True(terminal.Disposed, "The constructed HttpClient must be disposed when the factory throws.");
    }

    /// <summary>
    /// The cleanup on construction failure must never mask the original error: when the factory
    /// throws AND the best-effort disposal of the constructed HttpClient <em>also</em> throws, the
    /// caller must still receive the original factory exception instance, unchanged (same instance,
    /// same message, original stack) — while the cleanup is nevertheless attempted.
    /// </summary>
    /// <remarks>
    /// Without the throwing terminal handler, an implementation whose cleanup exception escaped and
    /// replaced the factory failure would go unnoticed, because a nonthrowing disposal can never
    /// produce a masking exception in the first place.
    /// </remarks>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryThrowsAndCleanupThrows_OriginalErrorSurvivesUnchanged()
    {
        var terminal = new ThrowingDisposalTerminalHandler("cleanup disposal failed");
        var factorySentinel = new SentinelThrower("factory boom");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal,
                _ =>
                {
                    factorySentinel.ThrowFromSentinelOrigin();
                    throw new UnreachableException();
                }));

        // The original factory failure survives, byte for byte.
        AssertOriginalFailureSurvived(factorySentinel, thrown);
        Assert.Equal("factory boom", thrown.Message);

        // The disposal failure must be swallowed: never surfaced, never aggregated over the
        // original, never attached as an inner exception.
        Assert.NotSame(terminal.Sentinel.Thrown, thrown);
        Assert.IsNotType<AggregateException>(thrown);
        Assert.Null(thrown.InnerException);

        // ...and the cleanup must genuinely have been attempted, not skipped.
        Assert.True(terminal.DisposeAttempted,
            "The constructed HttpClient must still be disposed (best-effort) when the factory throws.");
        Assert.NotNull(terminal.Sentinel.Thrown);
    }

    /// <summary>A null factory result is rejected and the HttpClient is disposed.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryReturnsNull_DisposesHttpClientAndThrows()
    {
        var terminal = new TrackingTerminalHandler();

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, _ => null!));

        Assert.Equal("inner", thrown.ParamName);
        Assert.True(terminal.Disposed, "The constructed HttpClient must be disposed when the factory result is null.");
    }

    /// <summary>
    /// A null result whose cleanup also throws must still surface the null-guard failure, not the
    /// cleanup failure.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_FactoryReturnsNullAndCleanupThrows_NullGuardSurvives()
    {
        var terminal = new ThrowingDisposalTerminalHandler("cleanup disposal failed");

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, _ => null!));

        Assert.Equal("inner", thrown.ParamName);
        Assert.NotSame(terminal.Sentinel.Thrown, thrown);
        Assert.True(terminal.DisposeAttempted);
    }

    /// <summary>Null arguments are rejected up front.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_NullTerminalHandler_Throws()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, null!, _ => new RecordingChatClient()));

        Assert.Equal("terminalHandler", thrown.ParamName);
    }

    /// <summary>Null factory is rejected up front, before any HttpClient is created.</summary>
    [Fact]
    public void CreateCopilotClientForTestFull_NullFactory_Throws()
    {
        var terminal = new TrackingTerminalHandler();

        var thrown = Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateCopilotClientForTestFull(
                useResponsesApi: false, terminal, null!));

        Assert.Equal("clientFactory", thrown.ParamName);

        // Validation happens before construction, so no transport was ever built (and therefore
        // none had to be torn down).
        Assert.False(terminal.Disposed);
    }
}

/// <summary>
/// The one seam test that genuinely needs to observe a <em>cleared</em> ambient environment, and
/// therefore mutates the process-wide <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> variables. It joins the
/// repository's serialized <c>EnvVarMutation</c> collection
/// (see <c>EnvVarMutationCollection</c>, <c>DisableParallelization = true</c>) so it cannot corrupt
/// — or be corrupted by — token-dependent tests running on other threads.
/// </summary>
/// <remarks>
/// The non-mutating half of the proof (the seam contributes no credential material at all) lives in
/// <see cref="OwnedCopilotChatClientTests.CreateCopilotClientForTestFull_ContributesNoCredentialMaterial"/>.
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class CopilotClientSeamTokenIndependenceTests : IDisposable
{
    private readonly string? _origGhToken;
    private readonly string? _origGithubToken;

    public CopilotClientSeamTokenIndependenceTests()
    {
        _origGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _origGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _origGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _origGithubToken);
    }

    /// <summary>Terminal handler that never transmits; only its construction matters here.</summary>
    private sealed class InertTerminalHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Minimal inner client for construction-only assertions.</summary>
    private sealed class InertChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// With both token variables cleared — the state under which the token-dependent Copilot path
    /// refuses to build a client — the seam still constructs successfully.
    /// </summary>
    [Fact]
    public void CreateCopilotClientForTestFull_WithTokensCleared_StillConstructs()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: false, new InertTerminalHandler(), _ => new InertChatClient());

        Assert.IsType<ChatClientFactory.OwnedCopilotChatClient>(client);
    }

    // ── RequiresResponsesEndpoint — endpoint classification ──────────────────

    /// <summary>
    /// Models in the gpt-6 family, including the case-insensitive form,
    /// must be classified as requiring the /responses endpoint.
    /// </summary>
    [Fact]
    public void RequiresResponsesEndpoint_Gpt6Family_RoutedToResponses()
    {
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-6-astra"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-6"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("GPT-6-ASTRA"));
    }

    /// <summary>
    /// The pre-existing model families (gpt-5 and the o-series) must remain
    /// classified as requiring the /responses endpoint.
    /// </summary>
    [Fact]
    public void RequiresResponsesEndpoint_LegacyFamilies_RoutedToResponses()
    {
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-5"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-5.4"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-5-mini"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("o3"));
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("o4-mini"));
    }

    /// <summary>
    /// The prefix rule's intended boundary behavior: any gpt-6* name matches
    /// (the same family form gpt-5 exhibits with gpt-5.4), while models from
    /// other families do not.
    /// </summary>
    [Fact]
    public void RequiresResponsesEndpoint_PrefixBoundary_TheIntendedMatches()
    {
        Assert.True(ChatClientFactory.RequiresResponsesEndpoint("gpt-60"));

        Assert.False(ChatClientFactory.RequiresResponsesEndpoint("gpt-4"));
        Assert.False(ChatClientFactory.RequiresResponsesEndpoint("gpt-4o"));
        Assert.False(ChatClientFactory.RequiresResponsesEndpoint("claude-opus"));
        Assert.False(ChatClientFactory.RequiresResponsesEndpoint("llama-3"));
    }
}
