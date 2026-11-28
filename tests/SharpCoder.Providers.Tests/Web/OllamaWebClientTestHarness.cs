using System.Net;
using System.Text;
using SharpCoder.Providers.Web;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Shared construction helpers for the typed transport tests. Every client is built from a
/// host-supplied handler, accessor, URI, clock and limits; no ambient state is involved.
/// </summary>
internal static class OllamaWebClientTestHarness
{
    internal const string Endpoint = "https://web.example.test";
    internal const string Credential = "abcDEF123-._~+/=";
    internal const string SecretSentinel = "SENTINEL_SECRET_DO_NOT_LEAK_7f31";

    internal static OllamaWebClientOptions Options(
        HttpMessageHandler handler,
        string? credential = Credential,
        Func<string?>? accessor = null,
        string baseUri = Endpoint,
        TimeProvider? timeProvider = null,
        TimeSpan? deadline = null,
        int maxResponseBytes = OllamaWebClientOptions.DefaultMaxResponseBytes)
    {
        var client = new HttpClient(handler);
        // Deliberately install an inherited Authorization value as well as a competing marker to
        // prove the request-scoped credential wins and inherited auth is cleared.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer INHERITED_SENTINEL");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Inherited-Authorization", "INHERITED_SENTINEL");
        return new OllamaWebClientOptions
        {
            BaseUri = new Uri(baseUri),
            HttpClient = client,
            CredentialAccessor = accessor ?? (() => credential),
            TimeProvider = timeProvider,
            Deadline = deadline ?? OllamaWebClientOptions.DefaultDeadline,
            MaxResponseBytes = maxResponseBytes,
        };
    }

    internal static OllamaWebClient Create(OllamaWebClientOptions options) => new(options);

    internal static RecordingWebHandler SearchOk(string results = "[]") =>
        RecordingWebHandler.Json("{\"results\":" + results + "}");

    internal static RecordingWebHandler FetchOk(string body = "{}") =>
        RecordingWebHandler.Json(body);

    internal static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

    internal static HttpResponseMessage Response(HttpStatusCode status, HttpContent content) =>
        RecordingWebHandler.CreateResponse(status, content);

    /// <summary>Asserts a typed failure with the exact fixed safe message and no payload.</summary>
    internal static void AssertFailure<T>(WebToolResult<T> result, WebToolErrorKind kind)
        where T : class
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Data);
        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(WebToolErrorMessages.For(kind), result.DisplayMessage);
        Assert.DoesNotContain(SecretSentinel, result.DisplayMessage, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(result.DisplayMessage));
    }

    /// <summary>
    /// Asserts the fixed message is library-owned and contains no dynamic data. Bounded diagnostic
    /// only: the sentinel, raw status text, and common exception words are checked explicitly.
    /// </summary>
    internal static void AssertSafeMessage<T>(WebToolResult<T> result)
        where T : class
    {
        var message = result.DisplayMessage;
        Assert.NotNull(message);
        Assert.True(message.Length < 128, "Display message must be a short fixed literal.");
        Assert.DoesNotContain(SecretSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message, StringComparison.Ordinal);
    }
}
