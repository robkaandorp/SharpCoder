namespace SharpCoder.Providers.Web;

/// <summary>Host configuration for the model-visible Ollama web-tool adapter.</summary>
/// <remarks>
/// <see cref="OutputTransform"/> is mandatory and governs MODEL OUTPUT ONLY. Queries and fetch URLs
/// are sent to the configured backend before transformation; the host must authorize that backend
/// and input disclosure separately. Raw successful DTOs can contain sensitive backend echoes and
/// remain host-only. The transform is trusted code and its own allocations are not bounded here.
/// </remarks>
public sealed class OllamaWebToolsOptions
{
    /// <summary>Default final UTF-8 display cap: 64 KiB.</summary>
    public const int DefaultDisplayBytes = 64 * 1024;

    /// <summary>Default number of logical lines used by web_fetch: 100.</summary>
    public const int DefaultMaxLines = 100;

    /// <summary>Hard maximum number of logical lines rendered by web_fetch: 1000.</summary>
    public const int MaximumMaxLines = 1000;

    /// <summary>
    /// Required host transform applied to every selected model-visible field and fixed message.
    /// Returning null or throwing selects the fixed transform-failure fallback. Returning an empty
    /// string is valid successful redaction. No identity transform is installed by default.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>> OutputTransform { get; set; } = null!;

    /// <summary>
    /// Final UTF-8 output cap, including headings, joining newlines, separators and notices. Must
    /// be at least <c>OllamaWebTools.MinimumDisplayBytes</c>. Defaults to 64 KiB.
    /// </summary>
    public int DisplayBytes { get; set; } = DefaultDisplayBytes;

    /// <summary>
    /// Host ceiling for explicit web_fetch <c>max_lines</c> values, in the inclusive range 1–1000.
    /// The omitted argument uses <see cref="DefaultMaxLines"/>; nonpositive explicit values are
    /// rejected before transport and values above this ceiling are clamped.
    /// </summary>
    public int MaxFetchLines { get; set; } = MaximumMaxLines;
}
