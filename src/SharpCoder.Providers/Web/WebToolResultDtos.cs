namespace SharpCoder.Providers.Web;

/// <summary>
/// One untruncated search entry exactly as the configured backend returned it (fields that were
/// missing or null are normalized to the empty string).
/// </summary>
/// <remarks>
/// <para>
/// <strong>HOST ONLY.</strong> Raw backend content is untrusted data and may contain sensitive
/// values echoed by the backend. The explicit <c>GetRaw*</c> accessors are the only way to read it;
/// raw content is deliberately absent from public property traversal, JSON serialization, and
/// formatting. Hosts must not destructure, serialize, or log these values. The mandatory host
/// output transform (iteration 2) is the ONLY route to model-visible content.
/// </para>
/// <para>Entries are never clipped or re-ranked here; count selection is the later adapter's job.</para>
/// </remarks>
public sealed class WebSearchResult
{
    private readonly string _title;
    private readonly string _url;
    private readonly string _content;

    internal WebSearchResult(string title, string url, string content)
    {
        _title = title;
        _url = url;
        _content = content;
    }

    /// <summary>Returns the complete untruncated title to an explicit host-only caller.</summary>
    public string GetRawTitle() => _title;

    /// <summary>Returns the complete untruncated URL to an explicit host-only caller.</summary>
    public string GetRawUrl() => _url;

    /// <summary>Returns the complete untruncated content to an explicit host-only caller.</summary>
    public string GetRawContent() => _content;

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebSearchResult);
}

/// <summary>A successful, untruncated search payload in upstream order.</summary>
/// <remarks>
/// <strong>HOST ONLY.</strong> The host must use <see cref="GetRawResults"/> explicitly and must not
/// destructure or log its contents. No public raw-content property is exposed for default traversal.
/// </remarks>
public sealed class WebSearchData
{
    private readonly IReadOnlyList<WebSearchResult> _results;

    internal WebSearchData(IReadOnlyList<WebSearchResult> results) => _results = results;

    /// <summary>Returns every retained entry, untruncated, to an explicit host-only caller.</summary>
    public IReadOnlyList<WebSearchResult> GetRawResults() => _results;

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebSearchData);
}

/// <summary>A successful, untruncated fetch payload with normalized missing/null fields.</summary>
/// <remarks>
/// <strong>HOST ONLY.</strong> Explicit accessors return the complete data; hosts must not
/// destructure, serialize, or log it. Null/non-string links were intentionally skipped by transport.
/// </remarks>
public sealed class WebFetchData
{
    private readonly string _title;
    private readonly string _content;
    private readonly IReadOnlyList<string> _links;

    internal WebFetchData(string title, string content, IReadOnlyList<string> links)
    {
        _title = title;
        _content = content;
        _links = links;
    }

    /// <summary>Returns the complete untruncated title to an explicit host-only caller.</summary>
    public string GetRawTitle() => _title;

    /// <summary>Returns the complete untruncated content to an explicit host-only caller.</summary>
    public string GetRawContent() => _content;

    /// <summary>Returns every valid link string, in order, to an explicit host-only caller.</summary>
    public IReadOnlyList<string> GetRawLinks() => _links;

    /// <summary>A fixed, data-free marker. Raw content is never emitted by formatting/logging.</summary>
    public override string ToString() => nameof(WebFetchData);
}
