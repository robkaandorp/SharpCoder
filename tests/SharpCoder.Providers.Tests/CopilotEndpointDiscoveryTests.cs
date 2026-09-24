using Microsoft.Extensions.AI;

using SharpCoder.Providers;

using System.Net;
using System.Text;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Focused self-check for per-account Copilot endpoint discovery:
/// <see cref="ChatClientFactory.GetCopilotApiEndpointAsync(string, CancellationToken)"/>,
/// <see cref="ChatClientFactory.DefaultCopilotApiEndpoint"/> and the
/// <c>CopilotEndpointHandler</c> that sends chat-completions and /responses traffic to the resolved
/// host.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario drives the REAL production lookup (through the injectable
/// <c>CopilotEndpointDiscovery.LookupHandler</c> seam) or the REAL production handler chain (through
/// <c>CreateCopilotClientForTestWithEndpoint</c>) into a recording terminal handler — no network
/// access and no sleeps. Each test also asserts a non-vacuous precondition (the lookup really ran,
/// or the retry really happened).
/// </para>
/// <para>
/// The discovery cache and the lookup seam are process-wide static state, so this class joins the
/// serialized <c>EnvVarMutation</c> collection
/// (<c>DisableParallelization = true</c>, see <see cref="EnvVarMutationCollection"/>).
/// </para>
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class CopilotEndpointDiscoveryTests : IDisposable
{
    private const string DiscoveredEndpoint = "https://api.business.githubcopilot.com";
    private const string Token = "gho_discovery_token";

    private const string ChatCompletionsBody =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private const string ResponsesBody = """{"id":"resp_1","output":[]}""";

    private readonly HttpMessageHandler? _originalLookupHandler = CopilotEndpointDiscovery.LookupHandler;

    public CopilotEndpointDiscoveryTests()
    {
        CopilotEndpointDiscovery.LookupHandler = null;
        CopilotEndpointDiscovery.ResetCache();
    }

    public void Dispose()
    {
        CopilotEndpointDiscovery.LookupHandler = _originalLookupHandler;
        CopilotEndpointDiscovery.ResetCache();
    }

    // ── Public surface ──────────────────────────────────────────────────────

    /// <summary>The default endpoint is the documented Copilot host.</summary>
    [Fact]
    public void DefaultCopilotApiEndpoint_IsTheCopilotHost()
    {
        Assert.Equal("https://api.githubcopilot.com/", ChatClientFactory.DefaultCopilotApiEndpoint.ToString());
        Assert.Equal("https", ChatClientFactory.DefaultCopilotApiEndpoint.Scheme);
    }

    /// <summary>A token-less resolution cannot be looked up: it is the default, with no lookup.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCopilotApiEndpointAsync_BlankToken_ReturnsDefaultWithoutLookup(string? token)
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            token!, TestContext.Current.CancellationToken);

        Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
        Assert.Equal(0, lookup.Count);
    }

    // ── The lookup request itself ────────────────────────────────────────────

    /// <summary>
    /// The lookup is ONE GET to the documented URL carrying the token as a bearer, a non-empty
    /// <c>User-Agent</c> (api.github.com rejects requests without one) and an
    /// <c>Accept: application/json</c>.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_LookupRequest_IsABearerGetWithUserAgentAndAccept()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            Token, TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveredEndpoint, endpoint.GetLeftPart(UriPartial.Authority));
        Assert.Equal(1, lookup.Count);

        var request = Assert.Single(lookup.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.github.com/copilot_internal/user", request.Uri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.False(string.IsNullOrWhiteSpace(request.UserAgent));
        Assert.Contains("application/json", request.Accept);
    }

    // ── Trust rule ──────────────────────────────────────────────────────────

    /// <summary>
    /// Advertised values that must be rejected: look-alike hosts, a non-https scheme, userinfo, a
    /// non-default port, and non-URI garbage. Every one of them degrades to the default host — the
    /// value is sent the user's token, so only <c>githubcopilot.com</c> and its subdomains are
    /// trusted.
    /// </summary>
    [Theory]
    [InlineData("https://githubcopilot.com.evil.com")]
    [InlineData("https://evilgithubcopilot.com")]
    [InlineData("https://api.githubcopilot.com.evil.com")]
    [InlineData("https://githubcopilot.com.evil.com/")]
    [InlineData("http://api.githubcopilot.com")]
    [InlineData("ftp://api.githubcopilot.com")]
    [InlineData("https://user:pass@api.githubcopilot.com")]
    [InlineData("https://user@api.githubcopilot.com")]
    [InlineData("https://api.githubcopilot.com:8443")]
    [InlineData("not-a-uri")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCopilotApiEndpointAsync_UntrustedValue_FallsBackToDefault(string advertised)
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{advertised}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            Token, TestContext.Current.CancellationToken);

        Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// Trusted values are normalized to scheme + authority: any path, query or fragment is dropped,
    /// and the host comparison is case-insensitive.
    /// </summary>
    [Theory]
    [InlineData("https://api.githubcopilot.com", "https://api.githubcopilot.com/")]
    [InlineData("https://api.business.githubcopilot.com", "https://api.business.githubcopilot.com/")]
    [InlineData("https://githubcopilot.com", "https://githubcopilot.com/")]
    [InlineData("https://API.GITHUBCOPILOT.COM", "https://api.githubcopilot.com/")]
    [InlineData("  https://api.githubcopilot.com  ", "https://api.githubcopilot.com/")]
    [InlineData(
        "https://api.business.githubcopilot.com:443/path?query=1#fragment",
        "https://api.business.githubcopilot.com/")]
    [InlineData("https://api.githubcopilot.com/some/path", "https://api.githubcopilot.com/")]
    public async Task GetCopilotApiEndpointAsync_TrustedValue_IsNormalizedToSchemeAndAuthority(
        string advertised, string expected)
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{advertised}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            Token, TestContext.Current.CancellationToken);

        Assert.Equal(expected, endpoint.ToString());
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// Every lookup failure — non-2xx, invalid JSON, a missing <c>endpoints</c>/<c>api</c>, a
    /// non-string value — degrades quietly to the default endpoint instead of throwing.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_LookupFailures_ReturnDefaultAndNeverThrow()
    {
        (string description, Func<int, HttpResponseMessage> response)[] failures =
        [
            ("non-2xx", _ => Raw(HttpStatusCode.Forbidden, "{\"message\":\"nope\"}")),
            ("invalid JSON", _ => Raw(HttpStatusCode.OK, "<html>not json</html>")),
            ("JSON array", _ => Raw(HttpStatusCode.OK, "[]")),
            ("missing endpoints", _ => Raw(HttpStatusCode.OK, "{\"login\":\"octocat\"}")),
            ("missing api", _ => Raw(HttpStatusCode.OK, "{\"endpoints\":{}}")),
            ("non-object endpoints", _ => Raw(HttpStatusCode.OK, "{\"endpoints\":\"nope\"}")),
            ("non-string api", _ => Raw(HttpStatusCode.OK, "{\"endpoints\":{\"api\":42}}")),
            ("null api", _ => Raw(HttpStatusCode.OK, "{\"endpoints\":{\"api\":null}}")),
            ("empty body", _ => Raw(HttpStatusCode.OK, "")),
        ];

        foreach (var (description, response) in failures)
        {
            var lookup = Serves(response);
            CopilotEndpointDiscovery.LookupHandler = lookup;
            CopilotEndpointDiscovery.ResetCache();

            var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
                Token, TestContext.Current.CancellationToken);

            Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
            Assert.True(lookup.Count >= 1, $"The lookup must really have run for the '{description}' case.");
        }
    }

    /// <summary>A network failure (the transport throwing) also degrades to the default endpoint.</summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_TransportThrows_ReturnsDefault()
    {
        var lookup = Serves(_ => throw new HttpRequestException("connection refused"));
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            Token, TestContext.Current.CancellationToken);

        Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
        Assert.True(lookup.Count >= 1);
    }

    // ── Caching ─────────────────────────────────────────────────────────────

    /// <summary>A discovered endpoint is cached: the second call performs no lookup at all.</summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_SecondCall_ServedFromCache()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var first = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);
        var second = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveredEndpoint, first.GetLeftPart(UriPartial.Authority));
        Assert.Equal(first, second);
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// The FALLBACK is cached too: a failing lookup is not repeated for the same token.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_FailedLookup_IsCachedToo()
    {
        var lookup = Serves(_ => throw new HttpRequestException("offline"));
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var first = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);
        var second = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);

        Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, first);
        Assert.Equal(first, second);
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>A different token replaces the single cached entry, and going back re-resolves it.</summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_DifferentToken_ReplacesTheEntry()
    {
        var lookup = Serves(count => count switch
        {
            1 => Raw(HttpStatusCode.OK, "{\"endpoints\":{\"api\":\"https://api.business.githubcopilot.com\"}}"),
            2 => Raw(HttpStatusCode.OK, "{\"endpoints\":{\"api\":\"https://api.individual.githubcopilot.com\"}}"),
            _ => Raw(HttpStatusCode.OK, "{\"endpoints\":{\"api\":\"https://api.business.githubcopilot.com\"}}"),
        });
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var forTokenA = await ChatClientFactory.GetCopilotApiEndpointAsync("token-a", TestContext.Current.CancellationToken);
        var forTokenB = await ChatClientFactory.GetCopilotApiEndpointAsync("token-b", TestContext.Current.CancellationToken);
        var backToTokenA = await ChatClientFactory.GetCopilotApiEndpointAsync("token-a", TestContext.Current.CancellationToken);

        Assert.Equal("https://api.business.githubcopilot.com", forTokenA.GetLeftPart(UriPartial.Authority));
        Assert.Equal("https://api.individual.githubcopilot.com", forTokenB.GetLeftPart(UriPartial.Authority));
        Assert.Equal("https://api.business.githubcopilot.com", backToTokenA.GetLeftPart(UriPartial.Authority));
        Assert.Equal(3, lookup.Count);
    }

    // ── Concurrency and cancellation ────────────────────────────────────────

    /// <summary>
    /// Two concurrent callers for the same token share ONE in-flight lookup and both observe its
    /// result.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_ConcurrentSameToken_ShareOneLookup()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}", holding: true);
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var first = ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);
        var second = ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);

        // Neither can have resolved yet: the lookup is held at the transport.
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, lookup.Count);

        lookup.Release();

        Assert.Equal(DiscoveredEndpoint, (await first).GetLeftPart(UriPartial.Authority));
        Assert.Equal(DiscoveredEndpoint, (await second).GetLeftPart(UriPartial.Authority));
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// One caller cancelling neither cancels the shared lookup nor poisons the cache: a second caller
    /// for the same token still receives the discovered endpoint, and the entry is reused.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_OneCallerCancels_OthersStillResolve()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}", holding: true);
        CopilotEndpointDiscovery.LookupHandler = lookup;

        using var cancellation = new CancellationTokenSource();
        var cancelling = ChatClientFactory.GetCopilotApiEndpointAsync(Token, cancellation.Token);
        var surviving = ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling);
        Assert.False(surviving.IsCompleted, "The shared lookup must not be cancelled by one caller's token.");

        lookup.Release();

        Assert.Equal(DiscoveredEndpoint, (await surviving).GetLeftPart(UriPartial.Authority));

        // The entry the surviving caller resolved is cached: no further lookup happens.
        var cached = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);
        Assert.Equal(DiscoveredEndpoint, cached.GetLeftPart(UriPartial.Authority));
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// A pre-cancelled caller observes its cancellation as <see cref="OperationCanceledException"/>
    /// and records nothing: a later caller resolves normally.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_PreCancelledCaller_ThrowsAndDoesNotPoisonTheCache()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChatClientFactory.GetCopilotApiEndpointAsync(Token, cancellation.Token));

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(Token, TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveredEndpoint, endpoint.GetLeftPart(UriPartial.Authority));
        Assert.Equal(1, lookup.Count);
    }

    // ── Consumption in the chat client ──────────────────────────────────────

    /// <summary>
    /// A request driven through the REAL production chain lands on the discovered host, while the
    /// path and query the SDK built are preserved exactly.
    /// </summary>
    [Fact]
    public async Task ChatCompletionsRequest_IsSentToTheDiscoveredEndpoint()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new RecordingTerminalHandler(ChatCompletionsBody);
        using var client = ChatClientFactory.CreateCopilotClientForTestWithEndpoint(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal, Token, out _);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/chat/completions?api-version=2025")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, lookup.Count);
        var captured = Assert.Single(terminal.Uris);
        Assert.Equal($"{DiscoveredEndpoint}/chat/completions?api-version=2025", captured);

        // The integration marker still rides the rewritten request, exactly once.
        Assert.Equal("copilot-developer-cli", Assert.Single(terminal.IntegrationIds));
    }

    /// <summary>The /responses branch is rewritten to the discovered host just the same.</summary>
    [Fact]
    public async Task ResponsesRequest_IsSentToTheDiscoveredEndpoint()
    {
        CopilotEndpointDiscovery.LookupHandler =
            Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");

        var terminal = new RecordingTerminalHandler(ResponsesBody);
        using var client = ChatClientFactory.CreateCopilotClientForTestWithEndpoint(
            useResponsesApi: true, ChatClientFactory.CopilotExtraHighMapping, terminal, Token, out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = Assert.Single(terminal.Uris);
        Assert.Equal($"{DiscoveredEndpoint}/responses", captured);

        // The integration marker still rides the rewritten request, exactly once.
        Assert.Equal("copilot-developer-cli", Assert.Single(terminal.IntegrationIds));
    }

    /// <summary>
    /// A retried attempt reaches the SAME resolved host: the real resilience pipeline re-sends the
    /// request through the endpoint handler, which resolves from the cache. The attempt count is
    /// asserted so the test cannot pass on a single un-retried attempt.
    /// </summary>
    [Fact]
    public async Task RetriedAttempt_ReachesTheSameDiscoveredEndpoint()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new RecordingTerminalHandler(ResponsesBody) { RemainingFailures = 1 };
        using var client = ChatClientFactory.CreateCopilotClientForTestWithEndpoint(
            useResponsesApi: true, ChatClientFactory.CopilotExtraHighMapping, terminal, Token, out _,
            FastRetry);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, terminal.Uris.Count);
        Assert.All(terminal.Uris, uri => Assert.Equal($"{DiscoveredEndpoint}/responses", uri));

        // The retry reused the cached endpoint rather than looking it up again.
        Assert.Equal(1, lookup.Count);
    }

    /// <summary>
    /// When the lookup fails, the very same request path still works — it simply goes to the default
    /// host, exactly as it did before discovery existed.
    /// </summary>
    [Fact]
    public async Task FailedLookup_RequestFallsBackToTheDefaultEndpoint()
    {
        var lookup = Serves(_ => throw new HttpRequestException("offline"));
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new RecordingTerminalHandler(ChatCompletionsBody);
        using var client = ChatClientFactory.CreateCopilotClientForTestWithEndpoint(
            useResponsesApi: false, ChatClientFactory.CopilotExtraHighMapping, terminal, Token, out _);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lookup.Count >= 1);
        var captured = Assert.Single(terminal.Uris);
        Assert.Equal("https://api.githubcopilot.com/chat/completions", captured);
    }

    /// <summary>
    /// <see cref="ChatClientFactory.Create"/> stays synchronous and offline: building a production
    /// Copilot client performs no endpoint lookup at all (the resolution is lazy, on the first
    /// request).
    /// </summary>
    [Fact]
    public void Create_CopilotClient_PerformsNoEndpointLookup()
    {
        var originalProvider = GetTokenProviderForTest();
        var originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        try
        {
            ChatClientFactory.SetTokenProvider(() => null!);
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", Token);

            var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
            CopilotEndpointDiscovery.LookupHandler = lookup;

            using var client = ChatClientFactory.Create("copilot/gpt-4o");

            Assert.IsType<ChatClientFactory.OwnedCopilotChatClient>(client);
            Assert.Equal(0, lookup.Count);
        }
        finally
        {
            ChatClientFactory.SetTokenProvider(originalProvider!);
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGhToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithubToken);
        }
    }

    /// <summary>
    /// The PRODUCTION Copilot path end to end: the client the production factory method builds for
    /// the Copilot provider — token resolution → branch selection → real handler chain, endpoint
    /// handler included — transmits to the endpoint discovered for the RESOLVED token, so a request
    /// really leaves for the per-account host.
    /// </summary>
    [Fact]
    public async Task ProductionCopilotClient_SendsChatCompletionsToTheDiscoveredEndpoint()
    {
        var originalProvider = GetTokenProviderForTest();
        var originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var terminal = new RecordingTerminalHandler(ChatCompletionsBody);

        try
        {
            ChatClientFactory.SetTokenProvider(() => null!);
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", Token);

            CopilotEndpointDiscovery.LookupHandler =
                Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");

            using var client = ChatClientFactory.CreateCopilotClientForTest("gpt-4o", terminal);

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("ok", response.Text);

            var captured = Assert.Single(terminal.Uris);
            Assert.Equal($"{DiscoveredEndpoint}/chat/completions", captured);

            // The integration marker still reaches the wire, unchanged.
            Assert.Equal("copilot-developer-cli", Assert.Single(terminal.IntegrationIds));
        }
        finally
        {
            ChatClientFactory.SetTokenProvider(originalProvider!);
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGhToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithubToken);
        }
    }

    /// <summary>
    /// The Ollama stack is Copilot-unrelated: a real request through it performs no endpoint lookup
    /// and keeps its own host.
    /// </summary>
    [Fact]
    public async Task OllamaRequest_DoesNoEndpointLookup()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new OllamaTerminalHandler();
        using var client = ChatClientFactory.CreateOllamaClientForTest("gpt-oss:20b", terminal);

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, lookup.Count);
        var captured = Assert.Single(terminal.Uris);
        Assert.StartsWith("http://localhost:11434", captured);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Near-zero base retry delay, mirroring the repository's retry-test pattern.</summary>
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(1);

    /// <summary>Builds a recording lookup handler that answers with a fixed response.</summary>
    private static RecordingLookupHandler Serves(HttpStatusCode status, string body, bool holding = false) =>
        new(_ => Raw(status, body)) { Holding = holding };

    /// <summary>Builds a recording lookup handler that answers with a per-attempt responder.</summary>
    private static RecordingLookupHandler Serves(Func<int, HttpResponseMessage> response, bool holding = false) =>
        new(response) { Holding = holding };

    private static HttpResponseMessage Raw(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static Func<string?>? GetTokenProviderForTest()
    {
        var field = typeof(ChatClientFactory).GetField(
            "_tokenProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<string?>?)field?.GetValue(null);
    }

    /// <summary>One captured lookup request.</summary>
    private sealed record LookupRequest(
        string Method, string Uri, string? AuthorizationScheme, string? AuthorizationParameter,
        string UserAgent, IReadOnlyList<string> Accept);

    /// <summary>
    /// The injectable lookup transport: records every request, can hold it open so concurrency and
    /// cancellation can be driven deterministically, and answers from a per-attempt responder.
    /// </summary>
    private sealed class RecordingLookupHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;
        private readonly List<LookupRequest> _requests = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public RecordingLookupHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

        /// <summary>Whether each request is held until <see cref="Release"/> is called.</summary>
        public bool Holding { get; init; }

        /// <summary>How many lookup requests reached the transport.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The recorded requests, in arrival order.</summary>
        public IReadOnlyList<LookupRequest> Requests
        {
            get { lock (_requests) { return _requests.ToArray(); } }
        }

        /// <summary>Unblocks every held request.</summary>
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);

            lock (_requests)
            {
                _requests.Add(new LookupRequest(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? string.Empty,
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.Select(a => a.MediaType ?? string.Empty).ToArray()));
            }

            // A real transport honors the token it is handed: a request whose token fires fails with
            // a cancellation instead of completing. That is what makes the "not linked to any one
            // caller's token" guarantee observable here.
            cancellationToken.ThrowIfCancellationRequested();

            if (Holding) await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // A throwing responder must propagate as the transport failure it models.
            return _respond(_count);
        }
    }

    /// <summary>
    /// Terminal handler that records the URI of every outgoing request and answers with a canned
    /// body, failing a configurable number of times first so the resilience pipeline really retries.
    /// </summary>
    private sealed class RecordingTerminalHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private int _remainingFailures;

        public RecordingTerminalHandler(string responseBody) => _responseBody = responseBody;

        /// <summary>Number of leading attempts answered with a retryable <c>500</c>.</summary>
        public int RemainingFailures
        {
            get => _remainingFailures;
            init => _remainingFailures = value;
        }

        public List<string> Uris { get; } = new();

        public List<string> IntegrationIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            lock (Uris)
            {
                Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
                IntegrationIds.AddRange(
                    request.Headers.TryGetValues("Copilot-Integration-Id", out var ids)
                        ? ids
                        : Array.Empty<string>());
            }

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = new StringContent("""{"error":"transient"}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Terminal handler answering with the Ollama-shaped NDJSON body.</summary>
    private sealed class OllamaTerminalHandler : HttpMessageHandler
    {
        public List<string> Uris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            lock (Uris)
            {
                Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            const string done =
                """{"model":"gpt-oss:20b","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"hi"},"done":true,"done_reason":"stop"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(done, Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }
}
