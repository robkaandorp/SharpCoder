using Microsoft.Extensions.AI;

using SharpCoder.Providers;

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Coverage completion for per-account Copilot endpoint discovery, alongside
/// <see cref="CopilotEndpointDiscoveryTests"/>: the explicit non-2xx statuses (401/404/500 — each
/// answered <b>exactly once</b>, proving the lookup rides its own plain HttpClient with no
/// resilience policy), the lookup timeout under a shortened internal deadline, N-caller contention
/// on the shared lookup over dedicated threads, and the token-less test seams proving no lookup
/// happens when no token is involved.
/// </summary>
/// <remarks>
/// The discovery cache and the lookup seam are process-wide static state, so this class joins the
/// serialized <c>EnvVarMutation</c> collection
/// (<c>DisableParallelization = true</c>, see <see cref="EnvVarMutationCollection"/>).
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class CopilotEndpointDiscoveryCoverageTests : IDisposable
{
    private const string DiscoveredEndpoint = "https://api.business.githubcopilot.com";
    private const string Token = "gho_coverage_token";

    private const string ChatCompletionsBody =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private const string ResponsesBody = """{"id":"resp_1","output":[]}""";

    private readonly HttpMessageHandler? _originalLookupHandler = CopilotEndpointDiscovery.LookupHandler;
    private readonly TimeSpan _originalLookupTimeout = CopilotEndpointDiscovery.LookupTimeout;

    public CopilotEndpointDiscoveryCoverageTests()
    {
        CopilotEndpointDiscovery.LookupHandler = null;
        CopilotEndpointDiscovery.ResetCache();
    }

    public void Dispose()
    {
        SetLookupTimeout(_originalLookupTimeout);
        CopilotEndpointDiscovery.LookupHandler = _originalLookupHandler;
        CopilotEndpointDiscovery.ResetCache();
    }

    // ── Lookup failures: explicit non-2xx statuses ──────────────────────────

    /// <summary>
    /// Each of 401, 404 and 500 degrades quietly to the default endpoint — and each is answered
    /// EXACTLY once: the lookup runs on its own plain HttpClient with no resilience policy, so a
    /// failing status is never retried (a retry would push the count above one).
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetCopilotApiEndpointAsync_Non2xxLookup_FallsBackWithoutRetry(HttpStatusCode status)
    {
        var lookup = Serves(status, "{\"message\":\"nope\"}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var endpoint = await ChatClientFactory.GetCopilotApiEndpointAsync(
            Token, TestContext.Current.CancellationToken);

        Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
        Assert.Equal(1, lookup.Count);

        var request = Assert.Single(lookup.Requests);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
    }

    // ── The lookup's own timeout ────────────────────────────────────────────

    /// <summary>
    /// A lookup transport that never produces a response is ended by the lookup's OWN short
    /// timeout — deliberately not linked to any caller's token — and the resolver degrades to the
    /// default endpoint well within the production five-second budget, proving the deadline really
    /// was shortened for this test.
    /// </summary>
    [Fact]
    public async Task GetCopilotApiEndpointAsync_HungLookup_TimesOutWithinShortenedDeadline()
    {
        var lookup = new HungLookupHandler();
        CopilotEndpointDiscovery.LookupHandler = lookup;
        SetLookupTimeout(TimeSpan.FromMilliseconds(250));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resolution = ChatClientFactory.GetCopilotApiEndpointAsync(
                Token, TestContext.Current.CancellationToken);

            // Bounded ONLY so a removed timeout cannot hang the suite: with the production
            // behavior intact the resolver returns via its internal deadline long before this
            // guard fires.
            var endpoint = await resolution.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            stopwatch.Stop();
            Assert.Equal(ChatClientFactory.DefaultCopilotApiEndpoint, endpoint);
            Assert.Equal(1, lookup.Count);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(4),
                $"The hung lookup must end via its own short deadline; took {stopwatch.Elapsed}.");
        }
        finally
        {
            SetLookupTimeout(_originalLookupTimeout);
        }
    }

    // ── Contention on the shared lookup ─────────────────────────────────────

    /// <summary>
    /// N callers arriving on DEDICATED THREADS for the same token share exactly one lookup: the
    /// first request is held at the transport until every caller has been released, and the final
    /// transport count is one. A caller that starts its own lookup instead of joining the shared
    /// one is indistinguishable only before release — the final count catches it.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallers_SameToken_PerformExactlyOneLookup()
    {
        const int callerCount = 8;
        var lookup = Serves(
            HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}", holding: true);
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var testToken = TestContext.Current.CancellationToken;
        var done = new CountdownEvent(callerCount);
        var results = new Uri[callerCount];
        var failures = new Exception?[callerCount];
        var threads = new Thread[callerCount];

        for (var i = 0; i < callerCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    results[index] = ChatClientFactory.GetCopilotApiEndpointAsync(Token, testToken)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
                finally
                {
                    done.Signal();
                }
            })
            { IsBackground = true };
        }

        foreach (var thread in threads)
            thread.Start();

        // The first caller has reached the transport and is held there — the lookup really runs.
        Assert.True(
            await lookup.FirstRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "The lookup never reached the transport.");

        lookup.Release();

        Assert.True(
            await WaitForCountdownAsync(done, TimeSpan.FromSeconds(30)),
            "Not every concurrent caller completed.");

        Assert.All(failures, Assert.Null);
        Assert.Equal(1, lookup.Count);
        Assert.All(results, r =>
            Assert.Equal(DiscoveredEndpoint, r.GetLeftPart(UriPartial.Authority)));
    }

    // ── Token-less test seams: no lookup when no token is involved ──────────

    /// <summary>
    /// <see cref="ChatClientFactory.CreateCopilotClientForTestFull"/> has no token dependency: a
    /// request driven through the FULL production chain it builds reaches the terminal handler but
    /// performs no endpoint lookup at all.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTestFull_NoLookupWhenNoTokenIsInvolved()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new RecordingTerminalHandler(ChatCompletionsBody);
        HttpClient? chainClient = null;
        using var client = ChatClientFactory.CreateCopilotClientForTestFull(
            useResponsesApi: false,
            terminal,
            httpClient =>
            {
                chainClient = httpClient;
                return new UnusedChatClient();
            });

        Assert.NotNull(chainClient);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await chainClient!.SendAsync(request, TestContext.Current.CancellationToken);

        // Non-vacuity: the request really traversed the production chain.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(terminal.Uris);

        // No token is involved anywhere in this stack: no lookup ran.
        Assert.Equal(0, lookup.Count);
    }

    /// <summary>
    /// The <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler)"/>
    /// HttpClient-overload seam wires no endpoint token: a /responses request through the chain
    /// reaches the terminal handler but performs no endpoint lookup.
    /// </summary>
    [Fact]
    public async Task CreateCopilotClientForTest_HttpClientOverload_NoLookupWhenNoTokenIsInvolved()
    {
        var lookup = Serves(HttpStatusCode.OK, $"{{\"endpoints\":{{\"api\":\"{DiscoveredEndpoint}\"}}}}");
        CopilotEndpointDiscovery.LookupHandler = lookup;

        var terminal = new RecordingTerminalHandler(ResponsesBody);
        using var chainClient = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: true, ChatClientFactory.CopilotExtraHighMapping, terminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/responses")
        {
            Content = new StringContent(
                """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await chainClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Non-vacuity: the request really traversed the production chain.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(terminal.Uris);

        // No token is involved anywhere in this stack: no lookup ran.
        Assert.Equal(0, lookup.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Sets the lookup's internal timeout via its internal setter seam.</summary>
    private static void SetLookupTimeout(TimeSpan value) =>
        typeof(CopilotEndpointDiscovery)
            .GetProperty("LookupTimeout", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, value);

    /// <summary>
    /// Waits, without blocking the test thread, until the countdown is fully signalled — the
    /// async equivalent of <see cref="CountdownEvent.Wait(int)"/>, so a stuck thread surfaces as a
    /// bounded test failure rather than a hung suite.
    /// </summary>
    private static async Task<bool> WaitForCountdownAsync(CountdownEvent countdown, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < timeout)
        {
            if (countdown.IsSet) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        return countdown.IsSet;
    }

    /// <summary>Builds a recording lookup handler that answers with a fixed response.</summary>
    private static RecordingLookupHandler Serves(HttpStatusCode status, string body, bool holding = false) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        })
        { Holding = holding };

    /// <summary>One captured lookup request.</summary>
    private sealed record CapturedLookup(string Method, string Uri, string? AuthorizationScheme, string? AuthorizationParameter);

    /// <summary>
    /// The injectable lookup transport: records every request, can hold requests open so
    /// concurrency can be driven deterministically, and answers from a per-attempt responder.
    /// </summary>
    private sealed class RecordingLookupHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;
        private readonly List<CapturedLookup> _requests = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstRequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public RecordingLookupHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

        /// <summary>Whether each request is held until <see cref="Release"/> is called.</summary>
        public bool Holding { get; init; }

        /// <summary>How many lookup requests reached the transport.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The recorded requests, in arrival order.</summary>
        public IReadOnlyList<CapturedLookup> Requests
        {
            get { lock (_requests) { return _requests.ToArray(); } }
        }

        /// <summary>Completes when the first lookup request has reached the transport.</summary>
        public Task<bool> FirstRequestArrived => _firstRequestArrived.Task;

        /// <summary>Unblocks every held request.</summary>
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _count);
            if (count == 1) _firstRequestArrived.TrySetResult(true);

            lock (_requests)
            {
                _requests.Add(new CapturedLookup(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? string.Empty,
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter));
            }

            if (Holding) await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return _respond(count);
        }
    }

    /// <summary>
    /// A lookup transport that never produces a response: the only thing that can end the request
    /// is the lookup client's own timeout, which is exactly what the test under it verifies.
    /// </summary>
    private sealed class HungLookupHandler : HttpMessageHandler
    {
        private int _count;

        /// <summary>How many lookup requests reached the transport.</summary>
        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);

            // Never completes on its own; the client's linked timeout token is the only exit.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            throw new UnreachableException();
        }
    }

    /// <summary>Terminal handler that records the URI and integration header of every request.</summary>
    private sealed class RecordingTerminalHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingTerminalHandler(string responseBody) => _responseBody = responseBody;

        public List<string> Uris { get; } = new();

        public List<string> IntegrationIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
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
                await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// An <see cref="IChatClient"/> the factory must accept but these tests never call — they drive
    /// the captured <see cref="HttpClient"/> directly.
    /// </summary>
    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}