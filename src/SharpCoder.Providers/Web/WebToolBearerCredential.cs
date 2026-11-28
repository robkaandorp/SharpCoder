namespace SharpCoder.Providers.Web;

/// <summary>
/// Validates host-supplied credentials: trims ONLY boundary ASCII space/tab, then requires the
/// RFC 6750 <c>b64token</c> grammar.
/// </summary>
internal static class WebToolBearerCredential
{
    /// <summary>
    /// Attempts to normalize <paramref name="raw"/> into a credential accepted by the protocol.
    /// </summary>
    /// <param name="raw">The value returned by the host accessor; may be null.</param>
    /// <param name="credential">The normalized credential when this method returns true.</param>
    /// <returns>
    /// False for null, blank, any non-boundary whitespace, CR/LF, control characters, non-ASCII
    /// whitespace, or any character outside the RFC 6750 <c>b64token</c> alphabet.
    /// </returns>
    internal static bool TryNormalize(string? raw, out string credential)
    {
        credential = string.Empty;
        if (raw is null) return false;

        // ONLY boundary ASCII space (0x20) and horizontal tab (0x09) may be stripped. Every
        // other whitespace form (CR, LF, NBSP, Unicode separators, …) stays in the value and is
        // rejected by the grammar check below.
        var start = 0;
        var end = raw.Length;
        while (start < end && (raw[start] == ' ' || raw[start] == '\t')) start++;
        while (end > start && (raw[end - 1] == ' ' || raw[end - 1] == '\t')) end--;
        if (start == end) return false;

        for (var i = start; i < end; i++)
        {
            var c = raw[i];
            var allowed = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '.' || c == '_' || c == '~' || c == '+' || c == '/' || c == '=';
            if (!allowed) return false;
        }

        credential = raw[start..end];
        return true;
    }
}
