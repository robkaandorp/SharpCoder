using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace SharpCoder.Providers.Web;

/// <summary>
/// Typed, host-configured transport for the Ollama <c>api/web_search</c> and
/// <c>api/web_fetch</c> endpoints, returning discriminated typed outcomes with fixed safe messages.
/// </summary>
/// <remarks>
/// <para>
/// <strong>HOST-ONLY SUCCESS PAYLOADS.</strong> Successful DTOs may echo sensitive backend data.
/// Raw fields require explicit accessors, but hosts must still never destructure, serialize, or log
/// them. The mandatory host output transform (iteration 2) is the only model-visible route. Queries
/// and fetch URLs are sent to the configured backend before that transform; host authorization of
/// that backend/input disclosure is therefore separate from output redaction.
/// </para>
/// <para>
/// The host supplies one <see cref="HttpClient"/>, credentials, endpoint, limits and clock. The
/// library reads no ambient/global credentials, adds no core/CLI dependency, and never disposes the
/// supplied client. Authorization is request-scoped for the configured endpoint only; the host owns
/// redirect credential forwarding, egress policy, normal TLS validation, logging, and buffering.
/// </para>
/// <para>
/// <see cref="HttpClient.Timeout"/> normally covers only receipt of headers, not a later body read.
/// The library deadline therefore covers send through the final body read. Expiry is determined by
/// elapsed monotonic time at EVERY send/open/read completion or throw; the timer exists only to
/// request cooperative cancellation. Delayed timer delivery cannot weaken classification. Caller
/// cancellation is checked first and propagates the caller-linked cancellation. A host timeout uses
/// <see cref="WebToolTimeoutException"/>; its message and inner exception are never surfaced.
/// </para>
/// <para>
/// The byte cap limits bytes this library requests, reads, and retains, not upstream host buffering.
/// Non-success before expiry returns HttpFailure without reading the body. Once EOF is observed
/// within the deadline, later expiry cannot change parsing or the result. Awaited work is always
/// joined, never abandoned, and cleanup faults never replace an established outcome.
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

    /// <summary>Creates a typed transport from host-supplied configuration.</summary>
    /// <param name="options">Host-owned endpoint, client, credential accessor, deadline and limits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">An endpoint, host component, cap, or deadline is invalid.</exception>
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

    /// <summary>POSTs exactly <c>{query,max_results}</c>; the complete valid entry list is retained.</summary>
    public async Task<WebToolResult<WebSearchData>> SearchAsync(
        string? query,
        int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
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

    /// <summary>POSTs exactly <c>{url}</c>; there is deliberately no max_lines transport argument.</summary>
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

    private async Task<(byte[]? Result, WebToolErrorKind Kind)> SendAsync(
        Uri endpoint,
        string requestJson,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();

        // Contain ALL accessor outcomes in one place, then re-check caller cancellation before ANY
        // credential-unavailable result. Cancellation identity is never converted to a typed error.
        string? rawCredential = null;
        try
        {
            rawCredential = _credentialAccessor();
        }
        catch (Exception)
        {
            // Exception text is deliberately discarded.
        }
        callerToken.ThrowIfCancellationRequested();
        if (!WebToolBearerCredential.TryNormalize(rawCredential, out var credential))
            return (null, WebToolErrorKind.CredentialUnavailable);
        callerToken.ThrowIfCancellationRequested();

        // This object is the sole request owner. A throwing cleanup is contained so it cannot mask
        // an already-established typed outcome or caller cancellation.
        using var request = new SafeOwnedResource<HttpRequestMessage>(new HttpRequestMessage(HttpMethod.Post, endpoint));
        request.Value.Headers.Remove("Authorization");
        request.Value.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, credential);
        request.Value.Content = new StringContent(requestJson, Encoding.UTF8, JsonMediaType);

        // Capture the start immediately before creating/sending the deadline operation. Expiry is
        // based on this monotonic timestamp, NOT on whether a host timer callback has run.
        var deadlineStart = _timeProvider.GetTimestamp();
        var deadlineCts = _deadline == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        using var linkedOwner = new SafeOwnedResource<CancellationTokenSource>(linked);
        using var deadlineOwner = new SafeOwnedResource<CancellationTokenSource>(deadlineCts!);

        // Forwarding is cooperative only. Timer callbacks swallow every host registration fault,
        // including AggregateException from CancellationTokenSource.Cancel().
        using var deadlineRegistration = new SafeOwnedRegistration(
            deadlineCts?.Token.Register(static state =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (Exception) { }
            }, linked) ?? default);
        using var timerOwner = deadlineCts is null
            ? null
            : new SafeOwnedResource<ITimer>(_timeProvider.CreateTimer(
                static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (Exception) { }
                }, deadlineCts, _deadline, Timeout.InfiniteTimeSpan));

        var operation = new DeadlineOperation(this, deadlineStart);
        var send = await AwaitSendAsync(request.Value, linked.Token, callerToken, operation).ConfigureAwait(false);
        if (send.Response is null) return (null, send.Kind);

        // Single ownership chain: the response owns its content; content owns its source stream.
        // We do not separately dispose content or a source stream returned by ReadAsStreamAsync.
        using var responseOwner = new SafeOwnedResource<HttpResponseMessage>(send.Response);
        var headers = ClassifyHeaders(send.Response, callerToken, operation);
        if (!headers.Success) return (null, headers.Kind);
        return await ReadBodyAsync(send.Response, linked.Token, callerToken, operation).ConfigureAwait(false);
    }

    private async Task<(HttpResponseMessage? Response, WebToolErrorKind Kind)> AwaitSendAsync(
        HttpRequestMessage request,
        CancellationToken linkedToken,
        CancellationToken callerToken,
        DeadlineOperation operation)
    {
        HttpResponseMessage? response = null;
        Exception? fault = null;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        try
        {
            callerToken.ThrowIfCancellationRequested();
            if (operation.IsExpired()) return (null, WebToolErrorKind.Timeout);
            if (fault is not null) return (null, ClassifyTransportFault(fault));
            var live = response!;
            response = null;
            return (live, default);
        }
        finally
        {
            SafeOwnedResource<HttpResponseMessage>.DisposeQuietly(response);
        }
    }

    private static (bool Success, WebToolErrorKind Kind) ClassifyHeaders(
        HttpResponseMessage response,
        CancellationToken callerToken,
        DeadlineOperation operation)
    {
        callerToken.ThrowIfCancellationRequested();
        if (operation.IsExpired()) return (false, WebToolErrorKind.Timeout);
        var success = (int)response.StatusCode >= 200 && (int)response.StatusCode <= 299;
        return (success, success ? default : WebToolErrorKind.HttpFailure);
    }

    private async Task<(byte[]? Bytes, WebToolErrorKind Kind)> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken linkedToken,
        CancellationToken callerToken,
        DeadlineOperation operation)
    {
        Stream? stream = null;
        Exception? openFault = null;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            openFault = ex;
        }

        // Stream-open is an awaited operation with the same precedence as send and byte reads.
        callerToken.ThrowIfCancellationRequested();
        if (operation.IsExpired()) return (null, WebToolErrorKind.Timeout);
        if (openFault is not null) return (null, ClassifyTransportFault(openFault));
        if (stream is null) return (null, WebToolErrorKind.MalformedResponse);

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];
        while (true)
        {
            int read = 0;
            Exception? readFault = null;
            // Ask only for remaining capacity plus ONE byte. That byte distinguishes exact-cap EOF
            // from overflow without ever retaining more than the configured cap.
            var remaining = _maxResponseBytes - buffer.Length;
            var requested = (int)Math.Min(chunk.Length, (long)remaining + 1);
            try
            {
                read = await stream.ReadAsync(chunk.AsMemory(0, requested), linkedToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                readFault = ex;
            }

            callerToken.ThrowIfCancellationRequested();
            if (operation.IsExpired()) return (null, WebToolErrorKind.Timeout);
            if (readFault is not null) return (null, ClassifyTransportFault(readFault));
            if (read == 0) return (buffer.ToArray(), default); // No deadline check after this exit.
            if (read > remaining) return (null, WebToolErrorKind.ResponseTooLarge);
            buffer.Write(chunk, 0, read);
        }
    }

    private static WebToolErrorKind ClassifyTransportFault(Exception fault)
    {
        if (fault is WebToolTimeoutException || ContainsTimeoutMarker(fault)) return WebToolErrorKind.Timeout;
        if (fault is OperationCanceledException or IOException or HttpRequestException)
            return WebToolErrorKind.TransportFailure;
        ExceptionDispatchInfo.Capture(fault).Throw();
        throw fault;
    }

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
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return false;
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
                if (entry.ValueKind != JsonValueKind.Object) continue;
                entries.Add(new WebSearchResult(
                    ReadNullableString(entry, "title"),
                    ReadNullableString(entry, "url"),
                    ReadNullableString(entry, "content")));
            }
            return WebToolResult<WebSearchData>.Success(new WebSearchData(entries));
        }
        catch (Exception ex) when (IsResponseDecodingException(ex))
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
                    if (link.ValueKind == JsonValueKind.String) links.Add(link.GetString()!);
            }
            return WebToolResult<WebFetchData>.Success(new WebFetchData(title, content, links));
        }
        catch (Exception ex) when (IsResponseDecodingException(ex))
        {
            return WebToolResult<WebFetchData>.Failure(WebToolErrorKind.MalformedResponse);
        }
    }

    /// <summary>
    /// Only JSON syntax/shape/Unicode traversal failures belong here. Unexpected host exceptions are
    /// intentionally outside this filter and retain their direct-client propagation behavior.
    /// </summary>
    private static bool IsResponseDecodingException(Exception ex) =>
        ex is JsonException or InvalidOperationException or ArgumentException or DecoderFallbackException;

    private static string ReadNullableString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Wrong-type field.");
        return value.GetString()!;
    }

    /// <summary>Mutable elapsed-time state shared by all classification boundaries for one call.</summary>
    private sealed class DeadlineOperation
    {
        private readonly OllamaWebClient _client;
        private readonly long _started;

        internal DeadlineOperation(OllamaWebClient client, long started)
        {
            _client = client;
            _started = started;
        }

        internal bool IsExpired()
        {
            if (_client._deadline == Timeout.InfiniteTimeSpan) return false;
            var now = _client._timeProvider.GetTimestamp();
            var elapsedTicks = (long)Math.Round(
                (now - _started) * (double)TimeSpan.TicksPerSecond / _client._timeProvider.TimestampFrequency,
                MidpointRounding.AwayFromZero);
            return elapsedTicks >= _client._deadline.Ticks;
        }
    }

    /// <summary>One deterministic ownership link; cleanup failures are contained and non-masking.</summary>
    private sealed class SafeOwnedResource<T> : IDisposable where T : class
    {
        internal SafeOwnedResource(T value) => Value = value;
        internal T Value { get; }
        public void Dispose() => DisposeQuietly(Value);
        internal static void DisposeQuietly(T? value)
        {
            if (value is null) return;
            try
            {
                switch (value)
                {
                    case ITimer timer: timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); break;
                    case IDisposable disposable: disposable.Dispose(); break;
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>Specialization for CancellationTokenRegistration (a struct, not an IDisposable class).</summary>
    private sealed class SafeOwnedRegistration : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        internal SafeOwnedRegistration(CancellationTokenRegistration registration) => _registration = registration;
        public void Dispose()
        {
            try { _registration.Dispose(); }
            catch (Exception) { }
        }
    }
}
