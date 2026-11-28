using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace SharpCoder.Providers.Web;

/// <summary>
/// Typed, host-configured transport for the Ollama <c>api/web_search</c> and
/// <c>api/web_fetch</c> endpoints. Returns a discriminated
/// <see cref="WebToolResult{TData}"/> for every expected outcome; never throws raw backend or
/// exception text into a result.
/// </summary>
/// <remarks>
/// <para>
/// <strong>HOST-ONLY SUCCESS PAYLOADS.</strong> <see cref="WebSearchData"/> and
/// <see cref="WebFetchData"/> contain untruncated, untransformed backend content that may echo
/// sensitive values. This client never logs, formats, serializes, or renders it. A mandatory
/// host-supplied output transform (iteration 2) is the ONLY route by which anything derived from a
/// successful payload may become model-visible. Direct callers must branch on
/// <see cref="WebToolErrorKind"/>, not on <see cref="WebToolResult{TData}.DisplayMessage"/>.
/// </para>
/// <para>
/// <strong>NO GLOBAL CREDENTIAL READS.</strong> The host supplies exactly one
/// <see cref="HttpClient"/>, one credential accessor, the endpoint, the deadline and the byte cap
/// in <see cref="OllamaWebClientOptions"/>. Nothing here reads environment variables,
/// configuration files, or ambient credentials, and no core/CLI dependency is introduced. The
/// accessor runs once per call before any transport. Clear of inherited
/// <c>Authorization</c>, each request carries only its own request-scoped Bearer credential for
/// the configured endpoint — never for a model-supplied fetch URL.
/// </para>
/// <para>
/// <strong>HOST OWNS THE HTTP PIPELINE.</strong> The supplied client retains its ownership,
/// lifetime and normal TLS certificate validation. The host is responsible for redirect policy and
/// for preventing credential forwarding to untrusted rewritten hosts, for backend URL/egress
/// policy, for logging (which must not record credentials or raw content), and for any buffering
/// inside its own handlers, proxies or <see cref="HttpContent"/>. The <see cref="HttpClient.Timeout"/>
/// configured on that client normally applies only up to the point response headers are received;
/// it is NOT a guarantee that a later body read is bounded. This library therefore installs its own
/// deadline over the whole send/header/body operation, but the byte cap
/// (<see cref="OllamaWebClientOptions.MaxResponseBytes"/>) bounds ONLY the bytes Providers reads and
/// retains — not upstream host buffering. Remote content and remote errors are untrusted data, never
/// instructions.
/// </para>
/// <para>
/// <strong>COOPERATIVE DEADLINE.</strong> The library deadline starts after a valid credential has
/// been obtained, immediately before sending, and uses <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
/// The library NEVER abandons an outstanding handler or stream operation: every awaited
/// send/header/read operation is allowed to complete or throw, and only then is precedence
/// applied. Caller cancellation always wins and propagates the caller-linked cancellation. Next, an
/// expired library deadline classifies <see cref="WebToolErrorKind.Timeout"/> BEFORE the returned
/// status or the thrown exception type is examined — including late non-success responses and late
/// unrelated <see cref="OperationCanceledException"/>, <see cref="IOException"/>,
/// <see cref="HttpRequestException"/>, <see cref="WebToolTimeoutException"/> and unexpected
/// <see cref="InvalidOperationException"/>. Before expiry, an awaited
/// <see cref="WebToolTimeoutException"/> (including one nested in an exception chain) maps to
/// Timeout; unrelated OCE/IO/HTTP exceptions map to TransportFailure; unexpected direct-client
/// host exceptions may propagate for host diagnostics ONLY when caller/deadline precedence permits.
/// Once a success body has been fully read within the deadline, later expiry cannot affect parsing
/// or the result. Non-success headers before expiry yield immediate HttpFailure without reading the
/// body.
/// </para>
/// <para>
/// A host implementing its own timeout should raise <see cref="WebToolTimeoutException"/> from its
/// handler or response stream and preserve that type through wrapping. Its dynamic message and
/// inner exception are never surfaced. Requests, responses, streams, linked token sources and
/// deadline timers are disposed deterministically, including on every failure path.
/// </para>
/// <para>
/// <strong>ITERATION 1 SCOPE.</strong> This is a typed protocol/DTO/transport layer only. Count
/// selection, clipping, rendering, display caps, and registered tool invocation are deliberately
/// deferred. The later invocation wrapper will be a Providers-owned <c>AIFunction</c> decorator
/// following <c>ToolAuthorizationGuard.AuthorizedAIFunction</c>: it wraps the inner
/// <c>InvokeAsync</c>, preserves schema metadata, propagates caller cancellation, and returns the
/// fixed <c>Unexpected tool failure.</c> for unexpected binding/invocation faults. It is NOT an
/// HTTP/client abstraction, and no SharpCoder core reference is required (Microsoft.Extensions.AI is
/// already referenced by this project).
/// </para>
/// </remarks>
public sealed class OllamaWebClient
{
    /// <summary>Default number of search results requested.</summary>
    public const int DefaultMaxResults = 5;

    /// <summary>Hard ceiling on requested search results.</summary>
    public const int MaximumMaxResults = 10;

    private const string JsonMediaType = "application/json";
    private const string BearerScheme = "Bearer";
    private const int ReadBufferSize = 8192;

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _credentialAccessor;
    private readonly Uri _searchUri;
    private readonly Uri _fetchUri;
    private readonly int _maxResponseBytes;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a typed transport from host-supplied configuration. Nothing is read from ambient
    /// state and the host's <see cref="HttpClient"/> is never disposed by this instance.
    /// </summary>
    /// <param name="options">Host-owned endpoint, client, credential accessor, deadline and limits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A required option is missing, the base URI is not absolute HTTPS (or carries user info,
    /// query, or fragment), the byte cap is not positive, or the deadline is outside 1–300 seconds
    /// and is not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public OllamaWebClient(OllamaWebClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = options.HttpClient ?? throw new ArgumentException("A host-supplied HttpClient is required.", nameof(options));
        _credentialAccessor = options.CredentialAccessor ?? throw new ArgumentException("A host-supplied credential accessor is required.", nameof(options));
        (_searchUri, _fetchUri) = OllamaWebEndpoint.Resolve(options.BaseUri);

        if (options.MaxResponseBytes <= 0)
            throw new ArgumentException("MaxResponseBytes must be positive.", nameof(options));

        if (options.Deadline != Timeout.InfiniteTimeSpan &&
            (options.Deadline < OllamaWebClientOptions.MinimumDeadline ||
             options.Deadline > OllamaWebClientOptions.MaximumDeadline))
            throw new ArgumentException("Deadline must be between 1 and 300 seconds inclusive, or Timeout.InfiniteTimeSpan.", nameof(options));

        _maxResponseBytes = options.MaxResponseBytes;
        _deadline = options.Deadline;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// POSTs exactly <c>{query,max_results}</c> to <c>api/web_search</c> and returns every valid
    /// upstream entry, untruncated. Count selection is the later adapter's responsibility.
    /// </summary>
    /// <param name="query">The search query. Blank (whitespace-only) yields Validation before transport.</param>
    /// <param name="maxResults">
    /// Requested result count. Defaults to <see cref="DefaultMaxResults"/> (5); nonpositive
    /// values map to 1; values above <see cref="MaximumMaxResults"/> (10) clamp to 10.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation; always takes precedence.</param>
    /// <returns>A discriminated success or failure. Never throws for an expected transport outcome.</returns>
    public async Task<WebToolResult<WebSearchData>> SearchAsync(
        string? query,
        int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        // Caller cancellation wins over every classification, including a no-send validation.
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return WebToolResult<WebSearchData>.Failure(WebToolErrorKind.Validation);

        var selected = maxResults <= 0 ? 1 : Math.Min(maxResults, MaximumMaxResults);
        var body = "{\"query\":" + JsonSerializer.Serialize(query) +
                   ",\"max_results\":" + selected.ToString(CultureInfo.InvariantCulture) + "}";
        var outcome = await SendAsync(_searchUri, body, cancellationToken).ConfigureAwait(false);
        return outcome.Result is null
            ? WebToolResult<WebSearchData>.Failure(outcome.Kind)
            : ParseSearch(outcome.Result);
    }

    /// <summary>
    /// POSTs exactly <c>{url}</c> to <c>api/web_fetch</c>. There is deliberately NO
    /// <c>max_lines</c> argument at this layer; display limits belong to the later renderer.
    /// </summary>
    /// <param name="url">The URL to fetch. Blank (whitespace-only) yields Validation before transport.</param>
    /// <param name="cancellationToken">Caller cancellation; always takes precedence.</param>
    /// <returns>A discriminated success or failure. Never throws for an expected transport outcome.</returns>
    public async Task<WebToolResult<WebFetchData>> FetchAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(url))
            return WebToolResult<WebFetchData>.Failure(WebToolErrorKind.Validation);

        var body = "{\"url\":" + JsonSerializer.Serialize(url) + "}";
        var outcome = await SendAsync(_fetchUri, body, cancellationToken).ConfigureAwait(false);
        return outcome.Result is null
            ? WebToolResult<WebFetchData>.Failure(outcome.Kind)
            : ParseFetch(outcome.Result);
    }

    /// <summary>
    /// The single cooperative send/header/body pipeline. The deadline covers everything from just
    /// before <c>SendAsync</c> through the last body read; it is not applied to credential
    /// resolution or to parsing.
    /// </summary>
    private async Task<(byte[]? Result, WebToolErrorKind Kind)> SendAsync(
        Uri endpoint,
        string requestJson,
        CancellationToken cancellationToken)
    {
        // Caller cancellation wins before any side effect, including credential resolution.
        cancellationToken.ThrowIfCancellationRequested();

        string credential;
        try
        {
            // No implicit/global reads: the only credential source is the host accessor. Its
            // exception text (and any credential it may have been about to return) never escapes.
            if (!WebToolBearerCredential.TryNormalize(_credentialAccessor(), out credential))
                return (null, WebToolErrorKind.CredentialUnavailable);
        }
        catch (Exception)
        {
            return (null, WebToolErrorKind.CredentialUnavailable);
        }

        // Re-check after credential resolution: the accessor may have taken caller-controlled time.
        cancellationToken.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Remove("Authorization");
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, credential);
        request.Content = new StringContent(requestJson, Encoding.UTF8, JsonMediaType);

        // The deadline starts after a valid credential, immediately before sending. A dedicated
        // deadline CTS (cancelled by a timer on the HOST-SUPPLIED TimeProvider) is linked with the
        // caller's token. All three objects are owned by this call and disposed in finally —
        // including on every failure path.
        var deadlineCts = _deadline == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Forward deadline expiry to the token handed to the handler/stream. The dedicated
        // deadlineCts remains the authoritative classification signal, so a caller-cancelled or
        // otherwise-cancelled linked token can never be mistaken for a library timeout.
        using var deadlineRegistration = deadlineCts?.Token.Register(
            static state => ((CancellationTokenSource)state!).Cancel(), linked);
        try
        {
            ITimer? timer = null;
            if (deadlineCts is not null)
            {
                // Tests rendezvous on this exact registered timer (one-shot, non-periodic, with the
                // deadline CTS as state) before advancing it. The callback is best-effort: a late
                // firing after the call has already completed (and disposed its CTS) must never
                // throw, and can never retroactively affect an already-returned result.
                timer = _timeProvider.CreateTimer(
                    static state =>
                    {
                        try { ((CancellationTokenSource)state!).Cancel(); }
                        catch (ObjectDisposedException) { }
                    },
                    deadlineCts,
                    _deadline,
                    Timeout.InfiniteTimeSpan);
            }

            try
            {
                var sendOutcome = await AwaitSendWithPrecedenceAsync(request, linked, deadlineCts, cancellationToken).ConfigureAwait(false);
                if (sendOutcome.Response is null) return (null, sendOutcome.Kind);

                // Dispose BOTH the response and its content explicitly. A host content implementation
                // may lazily create its read stream (so never reading the body still leaves a
                // host-owned object that must be released), and disposal must be deterministic on
                // every path — including an immediate non-success or an expired deadline.
                using (sendOutcome.Response)
                using (sendOutcome.Response.Content)
                {
                    var headersOutcome = await ClassifyHeadersAsync(
                        sendOutcome.Response, deadlineCts, cancellationToken).ConfigureAwait(false);
                    if (headersOutcome.Response is null) return (null, headersOutcome.Kind);

                    var readOutcome = await ReadWithPrecedenceAsync(
                        sendOutcome.Response, linked, deadlineCts, cancellationToken).ConfigureAwait(false);
                    if (readOutcome.Bytes is null) return (null, readOutcome.Kind);

                    // SUCCESS BODY FULLY READ: the exit boundary. From here on, expiry is irrelevant —
                    // parsing and the returned result are never re-checked against the deadline.
                    return (readOutcome.Bytes, default);
                }
            }
            finally
            {
                if (timer is not null) await DisposeTimerAsync(timer).ConfigureAwait(false);
            }
        }
        finally
        {
            linked.Dispose();
            deadlineCts?.Dispose();
        }
    }

    /// <summary>
    /// Awaits the send/header operation without ever abandoning it. Applies caller-first, then
    /// expired-deadline precedence to BOTH completion and throw, before inspecting status or type.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, WebToolErrorKind Kind)> AwaitSendWithPrecedenceAsync(
        HttpRequestMessage request,
        CancellationTokenSource linked,
        CancellationTokenSource? deadlineCts,
        CancellationToken callerToken)
    {
        HttpResponseMessage? response = null;
        Exception? fault = null;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        // PRECEDENCE: caller cancellation, then expired library deadline, then anything else.
        // The awaited operation has ALWAYS been joined at this point. A response that arrived on a
        // discarded path (late success/non-success, caller cancellation, or a fault) is disposed
        // before the outcome is returned or an exception propagated: no response, content, or
        // stream is ever abandoned.
        try
        {
            callerToken.ThrowIfCancellationRequested();
            if (IsLibraryDeadlineExpired(deadlineCts, callerToken))
                return (null, WebToolErrorKind.Timeout);

            if (fault is not null)
            {
                if (fault is WebToolTimeoutException || ContainsTimeoutMarker(fault))
                    return (null, WebToolErrorKind.Timeout);
                if (fault is OperationCanceledException or IOException or HttpRequestException)
                    return (null, WebToolErrorKind.TransportFailure);
                // Unexpected direct-client host exceptions may propagate for host diagnostics, but
                // ONLY when caller/deadline precedence permits (checked above). Preserve identity.
                ExceptionDispatchInfo.Capture(fault).Throw();
                throw fault;
            }

            // Hand ownership of the live response to the caller (the send/headers/read pipeline).
            var live = response!;
            response = null;
            return (live, default);
        }
        finally
        {
            DisposeResponse(response);
        }
    }

    /// <summary>
    /// Deterministically releases a response and its content on a classification path that does
    /// not read the body. Disposal is best-effort with respect to host content implementations: it
    /// must never throw and change a typed outcome or mask caller cancellation.
    /// </summary>
    private static void DisposeResponse(HttpResponseMessage? response)
    {
        if (response is null) return;
        try { response.Content?.Dispose(); }
        catch (Exception) { /* diagnostics/cleanup never change the classification */ }
        try { response.Dispose(); }
        catch (Exception) { /* diagnostics/cleanup never change the classification */ }
    }

    /// <summary>
    /// Classifies a returned response. A late response whose deadline already expired is Timeout
    /// (checked in <see cref="AwaitSendWithPrecedenceAsync"/> before this method is reached), and a
    /// non-success response before expiry is immediate HttpFailure with NO body consumption.
    /// </summary>
    private Task<(HttpResponseMessage? Response, WebToolErrorKind Kind)> ClassifyHeadersAsync(
        HttpResponseMessage response,
        CancellationTokenSource? deadlineCts,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (IsLibraryDeadlineExpired(deadlineCts, callerToken))
            return Task.FromResult<(HttpResponseMessage?, WebToolErrorKind)>((null, WebToolErrorKind.Timeout));

        var success = (int)response.StatusCode >= 200 && (int)response.StatusCode <= 299;
        return Task.FromResult<(HttpResponseMessage?, WebToolErrorKind)>(
            success ? (response, default) : (null, WebToolErrorKind.HttpFailure));
    }

    /// <summary>
    /// Reads a successful body incrementally, enforcing the byte cap exactly at the read boundary.
    /// Each awaited read (including throwing reads) is joined, then precedence is applied.
    /// </summary>
    private async Task<(byte[]? Bytes, WebToolErrorKind Kind)> ReadWithPrecedenceAsync(
        HttpResponseMessage response,
        CancellationTokenSource linked,
        CancellationTokenSource? deadlineCts,
        CancellationToken callerToken)
    {
        Stream? stream = null;
        Exception? openFault = null;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            openFault = ex;
        }

        // ReadAsStreamAsync is itself an awaited operation: join it, then apply caller-first and
        // expired-deadline precedence BEFORE inspecting the thrown exception's type.
        callerToken.ThrowIfCancellationRequested();
        if (IsLibraryDeadlineExpired(deadlineCts, callerToken))
        {
            stream?.Dispose();
            return (null, WebToolErrorKind.Timeout);
        }
        if (openFault is not null)
            return (null, ClassifyReadFault(openFault, deadlineCts, callerToken).Kind);

        // Dispose the stream itself as well as the response (the response's Dispose would normally
        // cover it, but we own the reference we obtained here). Non-null: the open operation was
        // joined above and any fault returned before this point.
        var bodyStream = stream ?? throw new InvalidOperationException("Response stream was not opened.");
        using (bodyStream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[ReadBufferSize];
            while (true)
            {
                int read;
                Exception? fault = null;
                try
                {
                    read = await bodyStream.ReadAsync(chunk.AsMemory(0, chunk.Length), linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    read = 0;
                    fault = ex;
                }

                // Join EVERY read operation (including a throwing one) before any classification.
                callerToken.ThrowIfCancellationRequested();
                if (IsLibraryDeadlineExpired(deadlineCts, callerToken))
                    return (null, WebToolErrorKind.Timeout);

                if (fault is not null) return (null, ClassifyReadFault(fault, deadlineCts, callerToken).Kind);

                if (read == 0)
                {
                    // Fully read. Caller/deadline checks above are the LAST deadline-sensitive point.
                    return (buffer.ToArray(), default);
                }

                // Enforce the cap at the read boundary: never retain more than cap + 0 bytes.
                if (read > _maxResponseBytes - buffer.Length)
                    return (null, WebToolErrorKind.ResponseTooLarge);
                buffer.Write(chunk, 0, read);
            }
        }
    }

    /// <summary>Classifies a fault from an awaited stream/read operation using the same precedence.</summary>
    private static (byte[]? Bytes, WebToolErrorKind Kind) ClassifyReadFault(
        Exception fault,
        CancellationTokenSource? deadlineCts,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (IsLibraryDeadlineExpired(deadlineCts, callerToken)) return (null, WebToolErrorKind.Timeout);
        if (fault is WebToolTimeoutException || ContainsTimeoutMarker(fault))
            return (null, WebToolErrorKind.Timeout);
        if (fault is OperationCanceledException or IOException or HttpRequestException)
            return (null, WebToolErrorKind.TransportFailure);
        // Unexpected host exception: propagate only when caller/deadline precedence permits.
        ExceptionDispatchInfo.Capture(fault).Throw();
        throw fault;
    }

    /// <summary>
    /// True only when the library's own deadline token fired and the caller did not cancel. Caller
    /// cancellation is always checked first by callers of this method.
    /// </summary>
    private static bool IsLibraryDeadlineExpired(CancellationTokenSource? deadlineCts, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested && deadlineCts is not null && deadlineCts.IsCancellationRequested;

    /// <summary>
    /// Walks the whole exception graph — including <see cref="AggregateException.InnerExceptions"/> —
    /// so several host timeout markers, at any nesting depth, still classify as Timeout. Only the
    /// marker's TYPE is inspected; its message and inner exception are never surfaced.
    /// </summary>
    private static bool ContainsTimeoutMarker(Exception fault)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(fault);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is WebToolTimeoutException) return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return false;
    }

    private static async ValueTask DisposeTimerAsync(ITimer timer)
    {
        await timer.DisposeAsync().ConfigureAwait(false);
    }

    private static WebToolResult<WebSearchData> ParseSearch(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return WebToolResult<WebSearchData>.Failure(WebToolErrorKind.MalformedResponse);

            var entries = new List<WebSearchResult>();
            foreach (var entry in results.EnumerateArray())
            {
                // Null and non-object entries are intentionally skipped (not malformed). A valid
                // array containing only skipped entries is a successful empty result set.
                if (entry.ValueKind != JsonValueKind.Object) continue;
                entries.Add(new WebSearchResult(
                    ReadNullableString(entry, "title"),
                    ReadNullableString(entry, "url"),
                    ReadNullableString(entry, "content")));
            }

            return WebToolResult<WebSearchData>.Success(new WebSearchData(entries));
        }
        catch (JsonException)
        {
            return WebToolResult<WebSearchData>.Failure(WebToolErrorKind.MalformedResponse);
        }
    }

    private static WebToolResult<WebFetchData> ParseFetch(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            // {} is valid: missing and null title/content/links are all tolerated.
            if (root.ValueKind != JsonValueKind.Object)
                return WebToolResult<WebFetchData>.Failure(WebToolErrorKind.MalformedResponse);

            var title = ReadNullableString(root, "title");
            var content = ReadNullableString(root, "content");
            var links = new List<string>();
            if (root.TryGetProperty("links", out var linksElement) && linksElement.ValueKind != JsonValueKind.Null)
            {
                if (linksElement.ValueKind != JsonValueKind.Array)
                    return WebToolResult<WebFetchData>.Failure(WebToolErrorKind.MalformedResponse);
                foreach (var link in linksElement.EnumerateArray())
                {
                    // Null and non-string links are intentionally skipped.
                    if (link.ValueKind == JsonValueKind.String) links.Add(link.GetString()!);
                }
            }

            return WebToolResult<WebFetchData>.Success(new WebFetchData(title, content, links));
        }
        catch (JsonException)
        {
            return WebToolResult<WebFetchData>.Failure(WebToolErrorKind.MalformedResponse);
        }
    }

    /// <summary>Reads a missing/null string field as empty; any other type is a shape error.</summary>
    private static string ReadNullableString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException("Wrong-type field.");
        return value.GetString()!;
    }
}
