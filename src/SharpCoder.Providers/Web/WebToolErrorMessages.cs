namespace SharpCoder.Providers.Web;

/// <summary>
/// The single owner of every fixed, safe, library-owned display message for the web-tool
/// transport. These strings are the ONLY failure text this layer produces.
/// </summary>
/// <remarks>
/// <para>
/// Iteration 2's adapter must add every fixed model-visible literal to its authoritative display
/// table before using it. Nothing here is derived from an exception, an HTTP reason phrase, a
/// response body, or a credential.
/// </para>
/// </remarks>
public static class WebToolErrorMessages
{
    /// <summary>Display message for <see cref="WebToolErrorKind.Validation"/>.</summary>
    public const string Validation = "Invalid web tool request.";

    /// <summary>Display message for <see cref="WebToolErrorKind.CredentialUnavailable"/>.</summary>
    public const string CredentialUnavailable = "Web tool unavailable.";

    /// <summary>Display message for <see cref="WebToolErrorKind.HttpFailure"/>.</summary>
    public const string HttpFailure = "Web tool service unavailable.";

    /// <summary>Display message for <see cref="WebToolErrorKind.TransportFailure"/>.</summary>
    public const string TransportFailure = "Web tool service unavailable.";

    /// <summary>Display message for <see cref="WebToolErrorKind.MalformedResponse"/>.</summary>
    public const string MalformedResponse = "Web tool service unavailable.";

    /// <summary>Display message for <see cref="WebToolErrorKind.ResponseTooLarge"/>.</summary>
    public const string ResponseTooLarge = "Web tool service unavailable.";

    /// <summary>Display message for <see cref="WebToolErrorKind.Timeout"/>.</summary>
    public const string Timeout = "Web tool service unavailable.";

    /// <summary>
    /// Returns the fixed display message for <paramref name="kind"/>. Unknown values resolve to
    /// <see cref="Validation"/> so no dynamic enum text can ever reach a display surface.
    /// </summary>
    /// <param name="kind">The classified failure kind.</param>
    /// <returns>A constant, library-owned string with no caller- or backend-derived content.</returns>
    public static string For(WebToolErrorKind kind) => kind switch
    {
        WebToolErrorKind.Validation => Validation,
        WebToolErrorKind.CredentialUnavailable => CredentialUnavailable,
        WebToolErrorKind.HttpFailure => HttpFailure,
        WebToolErrorKind.TransportFailure => TransportFailure,
        WebToolErrorKind.MalformedResponse => MalformedResponse,
        WebToolErrorKind.ResponseTooLarge => ResponseTooLarge,
        WebToolErrorKind.Timeout => Timeout,
        _ => Validation,
    };
}
