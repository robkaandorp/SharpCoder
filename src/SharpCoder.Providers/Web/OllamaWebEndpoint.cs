namespace SharpCoder.Providers.Web;

/// <summary>
/// Validates and normalizes the host-supplied base URI, and resolves the two web-tool API paths
/// beneath it.
/// </summary>
internal static class OllamaWebEndpoint
{
    private const string SearchPath = "api/web_search";
    private const string FetchPath = "api/web_fetch";

    /// <summary>
    /// Requires an absolute <c>https</c> URI with no user info, query, or fragment. Any allowed
    /// path prefix is trimmed of trailing slashes and given exactly one trailing slash, so
    /// <c>api/web_search</c> / <c>api/web_fetch</c> always resolve beneath it.
    /// </summary>
    /// <param name="baseUri">The host-supplied base URI.</param>
    /// <returns>The two absolute endpoint URIs.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown at construction time when <paramref name="baseUri"/> is null, relative, non-HTTPS,
    /// or carries user info, a query, or a fragment. No request is ever sent for such a value.
    /// </exception>
    internal static (Uri Search, Uri Fetch) Resolve(Uri? baseUri)
    {
        if (baseUri is null) throw new ArgumentException("An absolute HTTPS base URI is required.");
        if (!baseUri.IsAbsoluteUri) throw new ArgumentException("The base URI must be absolute.");
        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The base URI must use HTTPS.");

        // Uri.UserInfo is empty for BOTH "https://host" and "https://@host". Detect the '@'
        // delimiter in the escaped authority so even empty user-info syntax is rejected. '%40'
        // remains legal host/path data rather than being mistaken for this delimiter.
        var original = baseUri.OriginalString;
        var authorityStart = original.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart >= 0)
        {
            var authorityEnd = original.IndexOfAny(['/', '\\'], authorityStart + 3);
            var rawAuthority = authorityEnd < 0 ? original[(authorityStart + 3)..] : original[(authorityStart + 3)..authorityEnd];
            if (rawAuthority.Contains('@', StringComparison.Ordinal))
                throw new ArgumentException("The base URI must not contain user info.");
        }
        if (baseUri.Query.Length != 0) throw new ArgumentException("The base URI must not contain a query.");
        if (baseUri.Fragment.Length != 0) throw new ArgumentException("The base URI must not contain a fragment.");

        // Escaped path only: '%2F' or '%3F' inside a prefix is data, not a query or a segment
        // boundary. Trailing slashes are normalized to exactly one.
        var prefix = baseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        if (prefix.Length != 0) prefix += "/";

        var authority = baseUri.GetLeftPart(UriPartial.Authority);
        var root = authority + "/" + prefix;
        return (new Uri(root + SearchPath, UriKind.Absolute), new Uri(root + FetchPath, UriKind.Absolute));
    }
}
