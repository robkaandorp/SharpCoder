using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoder.Providers;

/// <summary>
/// Resolves the per-account GitHub Copilot API endpoint advertised for a token, and rewrites
/// outgoing Copilot requests to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> GitHub advertises a seat-specific API host in
/// <c>GET https://api.github.com/copilot_internal/user</c> under <c>endpoints.api</c> — for example
/// <c>https://api.business.githubcopilot.com</c> for a Copilot Business seat — and GitHub's own CLI
/// targets that host directly. Some accounts fail against the default host, so discovered endpoints
/// are used when available. This is optional hardening: the lookup is best-effort and every failure
/// degrades silently to <see cref="ChatClientFactory.DefaultCopilotApiEndpoint"/>.
/// </para>
/// <para>
/// <b>Own transport.</b> The lookup runs over its own plain <see cref="HttpClient"/> — never the
/// Copilot chat chain — so it carries no resilience policy, no reasoning-effort mapping, no
/// diagnostic capture of the token, a short timeout and no retries.
/// </para>
/// <para>
/// <b>One cached entry, keyed by a SHA-256 hash of the token.</b> The raw token is never stored
/// (nor logged): the cache holds only the hash and the resolved endpoint, so a token change — or a
/// process restart — is what refreshes the lookup. The fallback result is cached too, so a lookup
/// that fails is not repeated on every request. Concurrent callers for the same token share one
/// in-flight lookup, which runs with <see cref="CancellationToken.None"/>: a caller that cancels
/// therefore neither poisons the cache nor cancels the lookup the others are awaiting — it observes
/// its cancellation on its own await instead.
/// </para>
/// </remarks>
internal static class CopilotEndpointDiscovery
{
    /// <summary>
    /// The GitHub API lookup that advertises the account's Copilot endpoints.
    /// </summary>
    internal const string LookupUrl = "https://api.github.com/copilot_internal/user";

    /// <summary>
    /// The lookup's own timeout — deliberately short, and never linked to a caller's cancellation
    /// token: discovery is an optimization and must never be a reason for a chat request to stall.
    /// </summary>
    /// <remarks>
    /// The private setter exists only for tests: they shorten the deadline so a hung-lookup test
    /// stays fast and deterministic. Production never writes it.
    /// </remarks>
    internal static TimeSpan LookupTimeout { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The <c>User-Agent</c> sent on the lookup; <c>api.github.com</c> rejects requests without one.
    /// </summary>
    internal const string LookupUserAgent = "SharpCoder";

    /// <summary>
    /// The only domain a discovered endpoint may live under: the host must be exactly this, or a
    /// subdomain of it.
    /// </summary>
    private const string CopilotDomain = "githubcopilot.com";

    private const string EndpointsProperty = "endpoints";
    private const string ApiProperty = "api";
    private const string BearerScheme = "Bearer";
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Guards every read and write of <see cref="_currentTokenHash"/>, <see cref="_inFlight"/>,
    /// <see cref="_cachedEndpoint"/> and <see cref="_generation"/>, so the entry always describes
    /// exactly one whole token and exactly one lookup.
    /// </summary>
    private static readonly object SyncRoot = new();

    /// <summary>The SHA-256 hash of the token the cached entry (or in-flight lookup) belongs to.</summary>
    private static string? _currentTokenHash;

    /// <summary>The shared in-flight lookup for <see cref="_currentTokenHash"/>, if one is running.</summary>
    private static Task<Uri>? _inFlight;

    /// <summary>
    /// The resolved endpoint for <see cref="_currentTokenHash"/> — the discovered endpoint or the
    /// fallback — or <see langword="null"/> until the lookup has finished.
    /// </summary>
    private static Uri? _cachedEndpoint;

    /// <summary>
    /// The identity of the one lookup currently allowed to publish the entry. Every lookup start
    /// (and every <see cref="ResetCache"/>) advances it, so a lookup that has been superseded —
    /// even by a later lookup for the SAME token, as in token A → B → A — can recognize at
    /// completion that it no longer owns the entry and must neither publish nor clear the newer
    /// lookup's in-flight slot.
    /// </summary>
    private static long _generation;

    /// <summary>
    /// Test seam: the handler the lookup's own <see cref="HttpClient"/> is built over, replacing the
    /// real transport so discovery can be exercised offline.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> (the default) means production, where the lookup client owns and
    /// disposes its <see cref="HttpClientHandler"/>. An injected handler is deliberately never
    /// disposed by the lookup, so a test can drive several lookups through one instance.
    /// </remarks>
    internal static HttpMessageHandler? LookupHandler { get; set; }

    /// <summary>
    /// Test seam: drops the cached entry and the in-flight lookup slot, so the next resolution
    /// performs a fresh lookup.
    /// </summary>
    /// <remarks>
    /// There is deliberately no production caller: the cache is meant to live for the process and is
    /// refreshed only by a token change. Tests need to start from a clean cache.
    /// </remarks>
    internal static void ResetCache()
    {
        lock (SyncRoot)
        {
            _currentTokenHash = null;
            _inFlight = null;
            _cachedEndpoint = null;
            _generation++;
        }
    }

    /// <summary>
    /// Resolves the endpoint for <paramref name="token"/>: the account endpoint GitHub advertises,
    /// or <see cref="ChatClientFactory.DefaultCopilotApiEndpoint"/> when anything at all goes wrong.
    /// </summary>
    /// <param name="token">
    /// The GitHub token. A null, empty or whitespace token cannot be looked up and resolves to the
    /// default endpoint without any network access.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels <b>this caller's await</b> only — the shared lookup underneath is not linked to it,
    /// so cancelling here neither cancels the lookup other callers are awaiting nor poisons the
    /// cache.
    /// </param>
    /// <returns>The resolved (or cached) endpoint; never <see langword="null"/>.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    internal static async Task<Uri> GetEndpointAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ChatClientFactory.DefaultCopilotApiEndpoint;

        // A pre-cancelled caller never starts — or joins — a lookup, so cancellation cannot have any
        // effect on the shared entry in the first place.
        cancellationToken.ThrowIfCancellationRequested();

        var lookup = GetOrStartLookup(token, ComputeTokenHash(token));

        // WaitAsync, not plain await: this caller's token governs THIS await only. The lookup task
        // itself was started with no token at all, so cancelling here leaves the shared lookup — and
        // the entry it will write — untouched for everyone else.
        return await lookup.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the cached endpoint for <paramref name="tokenHash"/>, the in-flight lookup a caller
    /// with the same token is already awaiting, or starts one.
    /// </summary>
    private static Task<Uri> GetOrStartLookup(string token, string tokenHash)
    {
        lock (SyncRoot)
        {
            if (_currentTokenHash == tokenHash)
            {
                // A finished entry always wins: the fallback is cached too, so a failing lookup is
                // performed once per token rather than on every request.
                if (_cachedEndpoint is not null) return Task.FromResult(_cachedEndpoint);

                // Still unresolved but already running: share that single lookup instead of
                // starting a second one.
                if (_inFlight is not null) return _inFlight;
            }

            // A different token replaces the single entry, along with whatever it was doing.
            _currentTokenHash = tokenHash;
            _cachedEndpoint = null;
            _inFlight = null;

            // This lookup becomes the entry's sole publisher; any earlier one is now superseded.
            var lookup = QueryEndpointAsync(token, ++_generation);

            // A lookup over a transport that completes synchronously (an offline fake) finishes
            // re-entrantly right here: it has already recorded the endpoint and cleared this slot,
            // so publishing the finished task again would retain the token inside its closure for
            // no reason.
            if (_cachedEndpoint is null) _inFlight = lookup;

            return lookup;
        }
    }

    /// <summary>
    /// Performs the one HTTP lookup for <paramref name="token"/> and caches its outcome.
    /// </summary>
    /// <remarks>
    /// This method <b>never faults and never cancels</b>: every failure — non-2xx, timeout, network
    /// error, invalid JSON, a missing or non-string <c>endpoints.api</c>, an untrusted value —
    /// resolves to the fallback endpoint, which is then cached exactly like a discovered one.
    /// </remarks>
    /// <param name="token">The token to look the endpoint up for.</param>
    /// <param name="generation">
    /// This lookup's identity (see <see cref="_generation"/>): the entry is published only while it
    /// is still the current one.
    /// </param>
    private static async Task<Uri> QueryEndpointAsync(string token, long generation)
    {
        var endpoint = ChatClientFactory.DefaultCopilotApiEndpoint;

        try
        {
            using var client = CreateLookupClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, LookupUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
            request.Headers.UserAgent.ParseAdd(LookupUserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

            // CancellationToken.None, deliberately: this lookup is shared, so it must not inherit
            // any single caller's cancellation. Only the client's own short timeout bounds it.
            using var response = await client
                .SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content
                    .ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);

                endpoint = TryNormalizeEndpoint(ReadApiEndpoint(body))
                    ?? ChatClientFactory.DefaultCopilotApiEndpoint;
            }
        }
        catch
        {
            // A failed lookup is not an error path: it silently degrades to the default endpoint,
            // which the cache records so the failure is not repeated on every request.
        }

        lock (SyncRoot)
        {
            // Only the lookup that currently owns the entry may write it. Matching the token hash
            // alone is not enough: after A → B → A, the first A lookup would see "the current token
            // is A" and overwrite the second A lookup's entry — and clear its in-flight slot.
            if (_generation == generation)
            {
                _cachedEndpoint = endpoint;
                _inFlight = null;
            }
        }

        return endpoint;
    }

    /// <summary>
    /// Creates the lookup's own <see cref="HttpClient"/>: a plain transport with a short timeout and
    /// no resilience policy at all.
    /// </summary>
    private static HttpClient CreateLookupClient()
    {
        var injectedHandler = LookupHandler;

        // A test-injected handler belongs to the test, so it is never disposed here; a production
        // client owns (and therefore disposes) the handler it creates itself.
        return injectedHandler is null
            ? new HttpClient { Timeout = LookupTimeout }
            : new HttpClient(injectedHandler, disposeHandler: false) { Timeout = LookupTimeout };
    }

    /// <summary>
    /// Reads the string at <c>endpoints.api</c> from a lookup response body.
    /// </summary>
    /// <returns>
    /// The value when the body is a JSON object carrying an <c>endpoints</c> object with a string
    /// <c>api</c> property; <see langword="null"/> for malformed JSON, any other shape, and a
    /// missing, <see langword="null"/>, non-string or empty value.
    /// </returns>
    private static string? ReadApiEndpoint(string body)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (json is not JsonObject root) return null;
        if (!root.TryGetPropertyValue(EndpointsProperty, out var endpoints) || endpoints is not JsonObject endpointsObject)
            return null;
        if (!endpointsObject.TryGetPropertyValue(ApiProperty, out var api) || api is not JsonValue value)
            return null;
        if (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return null;

        return text;
    }

    /// <summary>
    /// Normalizes an advertised value to a trusted endpoint, or returns <see langword="null"/> when
    /// it must be rejected.
    /// </summary>
    /// <remarks>
    /// A value is trusted only when it is an absolute <c>https</c> URI with no userinfo, the default
    /// port, and a host that is exactly <c>githubcopilot.com</c> or a subdomain of it — look-alikes
    /// such as <c>githubcopilot.com.evil.com</c> or <c>evilgithubcopilot.com</c> are rejected,
    /// because the authorization header this endpoint receives carries the user's token. The result
    /// is reduced to scheme + authority, so any path, query or fragment the value carried is
    /// dropped.
    /// </remarks>
    private static Uri? TryNormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (uri.UserInfo.Length != 0) return null;
        if (!uri.IsDefaultPort) return null;

        var host = uri.Host;
        if (!string.Equals(host, CopilotDomain, StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith("." + CopilotDomain, StringComparison.OrdinalIgnoreCase))
            return null;

        return new UriBuilder(Uri.UriSchemeHttps, host).Uri;
    }

    /// <summary>
    /// Hashes the token for the cache key. The raw token never becomes the key — and is never stored
    /// or logged — so a token change is the only thing that can invalidate an entry.
    /// </summary>
    private static string ComputeTokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// A <see cref="DelegatingHandler"/> that sends Copilot requests to the endpoint discovered for its
/// token instead of the one the OpenAI SDK was configured with.
/// </summary>
/// <remarks>
/// <para>
/// It sits immediately above the terminal handler, so the rewrite is the last thing that happens
/// before transmission — and, because the resilience handler above it re-sends the same request
/// instance per attempt, every retry passes through here again and lands on the same resolved host.
/// </para>
/// <para>
/// Only the scheme and authority of the request URI are replaced; the path and query the SDK built
/// are preserved exactly. Resolution is lazy — it happens on the first request, awaited from the
/// shared cache — so constructing a client performs no network access at all.
/// </para>
/// </remarks>
internal sealed class CopilotEndpointHandler : DelegatingHandler
{
    /// <summary>The token whose account endpoint the requests are sent to.</summary>
    private readonly string _token;

    /// <summary>Creates the handler for <paramref name="token"/>.</summary>
    /// <param name="token">The GitHub token that identifies the account whose endpoint is used.</param>
    internal CopilotEndpointHandler(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = token;
    }

    /// <summary>
    /// Rewrites the request URI onto the resolved endpoint and forwards the request unchanged.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = await ChatClientFactory
            .GetCopilotApiEndpointAsync(_token, cancellationToken).ConfigureAwait(false);

        request.RequestUri = RewriteEndpoint(request.RequestUri, endpoint);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the scheme and authority of <paramref name="requestUri"/> with
    /// <paramref name="endpoint"/>'s, keeping its path and query intact.
    /// </summary>
    /// <remarks>
    /// The normalized endpoint carries no userinfo, and the URI it produces is the scheme, host and
    /// port it received plus the request's own path and query — nothing else is copied over.
    /// </remarks>
    private static Uri? RewriteEndpoint(Uri? requestUri, Uri endpoint) =>
        requestUri is null
            ? null
            : new UriBuilder(requestUri)
            {
                Scheme = endpoint.Scheme,
                Host = endpoint.Host,
                Port = endpoint.Port,
            }.Uri;
}
