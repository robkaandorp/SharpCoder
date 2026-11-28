namespace SharpCoder.Providers.Web;

/// <summary>
/// Machine-readable classification of an <see cref="OllamaWebClient"/> failure. Direct callers
/// must branch on this value; they must never parse the accompanying display text.
/// </summary>
/// <remarks>
/// The display text paired with each kind is fixed, library-owned, and deliberately free of
/// exception text, HTTP reason phrases, response bodies, URLs and credentials.
/// </remarks>
public enum WebToolErrorKind
{
    /// <summary>The caller supplied an unusable argument; no request was sent.</summary>
    Validation,

    /// <summary>The host credential accessor could not supply a usable credential; no request was sent.</summary>
    CredentialUnavailable,

    /// <summary>The configured endpoint returned a non-success HTTP status.</summary>
    HttpFailure,

    /// <summary>The request or response could not be transported.</summary>
    TransportFailure,

    /// <summary>A success response body was not JSON in the expected shape.</summary>
    MalformedResponse,

    /// <summary>A success response body exceeded the configured transport byte cap.</summary>
    ResponseTooLarge,

    /// <summary>The library deadline expired (or the host signalled its own timeout marker).</summary>
    Timeout,
}
