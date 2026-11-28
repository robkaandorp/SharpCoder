using System.Text;

namespace SharpCoder.Providers.Web;

/// <summary>
/// Authoritative table of every fixed model-visible web-tool literal and required sequence.
/// No renderer or adapter may introduce another fixed display string.
/// </summary>
internal static class WebToolDisplayLiterals
{
    internal const string TitlePrefix = "Title: ";
    internal const string ContentPrefix = "Content: ";
    internal const string LinksHeading = "Links:";
    internal const string UrlPrefix = "URL: ";
    internal const string ResultsOmitted = "RESULTS-OMITTED";
    internal const string Truncated = "TRUNCATED";
    internal const string NoResults = "No results found.";
    internal const string EmptyPage = "Empty page.";
    internal const string ToolRequestFailed = "Tool request failed.";
    internal const string UnexpectedToolFailure = "Unexpected tool failure.";
    internal const string TransformFailure = "Tool output transformation failed.";

    internal static readonly IReadOnlyList<string> All =
    [
        WebToolErrorMessages.Validation,
        WebToolErrorMessages.CredentialUnavailable,
        WebToolErrorMessages.HttpFailure,
        WebToolErrorMessages.TransportFailure,
        WebToolErrorMessages.MalformedResponse,
        WebToolErrorMessages.ResponseTooLarge,
        WebToolErrorMessages.Timeout,
        ToolRequestFailed,
        UnexpectedToolFailure,
        TransformFailure,
        NoResults,
        EmptyPage,
        TitlePrefix,
        ContentPrefix,
        LinksHeading,
        UrlPrefix,
        ResultsOmitted,
        Truncated,
        ResultsOmitted + "\n\n" + Truncated,
    ];

    /// <summary>Computed solely from the authoritative table's UTF-8 byte counts.</summary>
    internal static readonly int MinimumDisplayBytes = All.Max(Encoding.UTF8.GetByteCount);
}
