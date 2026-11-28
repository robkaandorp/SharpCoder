using System.Text.Json.Serialization;

namespace SharpCoder.Providers.Web;

/// <summary>
/// One untruncated search entry exactly as the configured backend returned it (fields that were
/// missing or null are normalized to the empty string).
/// </summary>
/// <remarks>
/// <para>
/// <strong>HOST ONLY.</strong> Raw backend content is untrusted data and may contain sensitive
/// values echoed by the backend. This type has no model-visible or loggable representation: its
/// <see cref="ToString"/> is a fixed type marker and its fields are excluded from automatic JSON
/// serialization. The mandatory host output transform (iteration 2) is the ONLY route by which any
/// content derived from these fields may be rendered into a model-visible message.
/// </para>
/// <para>
/// Entries are never clipped, counted down, or re-ranked here. Count selection is deliberately
/// the later adapter's job.
/// </para>
/// </remarks>
public sealed class WebSearchResult
{
    internal WebSearchResult(string title, string url, string content)
    {
        Title = title;
        Url = url;
        Content = content;
    }

    /// <summary>The untruncated title, or an empty string when the backend supplied none.</summary>
    [JsonIgnore]
    public string Title { get; }

    /// <summary>The untruncated URL, or an empty string when the backend supplied none.</summary>
    [JsonIgnore]
    public string Url { get; }

    /// <summary>The untruncated content, or an empty string when the backend supplied none.</summary>
    [JsonIgnore]
    public string Content { get; }

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebSearchResult);
}

/// <summary>
/// A successful, untruncated search payload: every valid upstream entry, in upstream order.
/// </summary>
/// <remarks><strong>HOST ONLY.</strong> See <see cref="WebSearchResult"/>.</remarks>
public sealed class WebSearchData
{
    internal WebSearchData(IReadOnlyList<WebSearchResult> results) => Results = results;

    /// <summary>All retained entries. Empty is a successful no-results response, not a failure.</summary>
    [JsonIgnore]
    public IReadOnlyList<WebSearchResult> Results { get; }

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebSearchData);
}

/// <summary>
/// A successful, untruncated fetch payload. Missing/null title or content become empty strings;
/// missing/null links become an empty list; null or non-string link entries are skipped.
/// </summary>
/// <remarks><strong>HOST ONLY.</strong> See <see cref="WebSearchResult"/>.</remarks>
public sealed class WebFetchData
{
    internal WebFetchData(string title, string content, IReadOnlyList<string> links)
    {
        Title = title;
        Content = content;
        Links = links;
    }

    /// <summary>The untruncated title, or an empty string when the backend supplied none.</summary>
    [JsonIgnore]
    public string Title { get; }

    /// <summary>The untruncated content, or an empty string when the backend supplied none.</summary>
    [JsonIgnore]
    public string Content { get; }

    /// <summary>The valid link strings in upstream order; never null.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Links { get; }

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebFetchData);
}
