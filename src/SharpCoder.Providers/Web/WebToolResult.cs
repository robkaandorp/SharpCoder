namespace SharpCoder.Providers.Web;

/// <summary>
/// The discriminated outcome of a typed <see cref="OllamaWebClient"/> call: either a successful,
/// untruncated, host-only <typeparamref name="TData"/> payload or exactly one
/// <see cref="WebToolErrorKind"/> with a fixed safe display message.
/// </summary>
/// <typeparam name="TData">
/// <see cref="WebSearchData"/> or <see cref="WebFetchData"/>. Successful payloads are HOST ONLY and
/// must be passed through the mandatory host output transform before anything derived from them
/// becomes model-visible or logged.
/// </typeparam>
/// <remarks>
/// <para>
/// Direct callers branch on <see cref="IsSuccess"/> / <see cref="ErrorKind"/> and never parse
/// <see cref="DisplayMessage"/>. The message is a constant library-owned string: exception text,
/// HTTP reason phrases, response bodies, URLs and credentials can never appear in it.
/// </para>
/// <para>
/// <see cref="DisplayMessage"/> is always the empty string on success. This type never formats or
/// serializes <see cref="Data"/>: its <see cref="ToString"/> is a fixed marker, so accidental
/// logging or string interpolation cannot disclose raw backend content.
/// </para>
/// </remarks>
public sealed class WebToolResult<TData> where TData : class
{
    private WebToolResult(TData? data, WebToolErrorKind? errorKind, string displayMessage)
    {
        Data = data;
        ErrorKind = errorKind;
        DisplayMessage = displayMessage;
    }

    /// <summary>True when the backend returned a usable success payload.</summary>
    public bool IsSuccess => Data is not null;

    /// <summary>
    /// The successful, untruncated, host-only payload, or <see langword="null"/> on any failure.
    /// </summary>
    public TData? Data { get; }

    /// <summary>
    /// The machine-readable failure classification, or <see langword="null"/> on success.
    /// </summary>
    public WebToolErrorKind? ErrorKind { get; }

    /// <summary>
    /// The fixed, library-owned, safe display message for the failure; empty on success. Never
    /// contains backend or exception-derived text and is never a rendering of <see cref="Data"/>.
    /// </summary>
    public string DisplayMessage { get; }

    /// <summary>Creates a successful outcome holding the host-only payload.</summary>
    /// <param name="data">The untransformed payload.</param>
    /// <returns>A successful result with an empty <see cref="DisplayMessage"/>.</returns>
    internal static WebToolResult<TData> Success(TData data) =>
        new(data ?? throw new ArgumentNullException(nameof(data)), null, string.Empty);

    /// <summary>Creates a typed failure whose message is derived only from <paramref name="kind"/>.</summary>
    /// <param name="kind">The already-classified failure.</param>
    /// <returns>A failure result with no payload and a fixed safe message.</returns>
    internal static WebToolResult<TData> Failure(WebToolErrorKind kind) =>
        new(null, kind, WebToolErrorMessages.For(kind));

    /// <summary>A fixed, data-free marker; the payload is never formatted or logged by this type.</summary>
    public override string ToString() => IsSuccess ? nameof(WebToolResult<TData>) : DisplayMessage;
}
