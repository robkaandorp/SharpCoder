namespace SharpCoder.Providers.Web;

/// <summary>
/// The discriminated outcome of a typed <see cref="OllamaWebClient"/> call: either a successful,
/// untruncated, host-only payload or exactly one <see cref="WebToolErrorKind"/> with a fixed safe
/// display message.
/// </summary>
/// <typeparam name="TData">
/// <see cref="WebSearchData"/> or <see cref="WebFetchData"/>. Successful payloads are HOST ONLY.
/// Their raw fields require explicit accessor calls and are absent from default public property
/// traversal, but a host must still never destructure, serialize, or log them. They must pass
/// through the mandatory host output transform before anything derived from them is model-visible.
/// Queries and fetch URLs are disclosed to the configured backend BEFORE that future output
/// transform; the host therefore authorizes that backend/input disclosure separately.
/// </typeparam>
/// <remarks>
/// <para>Direct callers branch on <see cref="IsSuccess"/> / <see cref="ErrorKind"/>, never text.</para>
/// <para><see cref="DisplayMessage"/> is fixed library-owned text on failure and empty on success.</para>
/// </remarks>
public sealed class WebToolResult<TData> where TData : class
{
    private readonly TData? _data;

    private WebToolResult(TData? data, WebToolErrorKind? errorKind, string displayMessage)
    {
        _data = data;
        ErrorKind = errorKind;
        DisplayMessage = displayMessage;
    }

    /// <summary>True when the backend returned a usable success payload.</summary>
    public bool IsSuccess => _data is not null;

    /// <summary>The machine-readable failure classification, or null on success.</summary>
    public WebToolErrorKind? ErrorKind { get; }

    /// <summary>The fixed safe display message for the failure; empty on success.</summary>
    public string DisplayMessage { get; }

    /// <summary>
    /// Explicitly retrieves the complete untruncated, untransformed host-only payload, or null on
    /// failure. Deliberately a method so raw data is not visited by default property traversal.
    /// </summary>
    public TData? GetRawData() => _data;

    internal static WebToolResult<TData> Success(TData data) =>
        new(data ?? throw new ArgumentNullException(nameof(data)), null, string.Empty);

    internal static WebToolResult<TData> Failure(WebToolErrorKind kind) =>
        new(null, kind, WebToolErrorMessages.For(kind));

    /// <summary>A fixed, data-free marker on success; the fixed safe message on failure.</summary>
    public override string ToString() => IsSuccess ? nameof(WebToolResult<TData>) : DisplayMessage;
}
