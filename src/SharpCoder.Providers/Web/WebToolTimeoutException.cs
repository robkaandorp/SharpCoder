namespace SharpCoder.Providers.Web;

/// <summary>
/// The explicit marker a host raises when its own deadline (for example a timeout inside a custom
/// <see cref="HttpMessageHandler"/> or a response stream) expires.
/// </summary>
/// <remarks>
/// <para>
/// A host that implements its own timeout should throw this type — or wrap it as an inner
/// exception — from the handler/read operation, and must preserve the type through any
/// wrapping. <see cref="OllamaWebClient"/> classifies it as
/// <see cref="WebToolErrorKind.Timeout"/>. Plain, unrelated
/// <see cref="OperationCanceledException"/> instances classify as
/// <see cref="WebToolErrorKind.TransportFailure"/>.
/// </para>
/// <para>
/// THE DYNAMIC MESSAGE AND INNER EXCEPTION ARE NEVER SURFACED. This type may carry host-authored
/// diagnostics (for example correlation IDs or exception chains), but the typed client discards all
/// of it: wire text, model output and library logs only ever see the fixed timeout message. Caller
/// cancellation always takes precedence over this marker.
/// </para>
/// </remarks>
public sealed class WebToolTimeoutException : OperationCanceledException
{
    /// <summary>Creates a marker with no host diagnostics.</summary>
    public WebToolTimeoutException()
        : base()
    {
    }

    /// <summary>Creates a marker with host diagnostics that are never surfaced by the library.</summary>
    /// <param name="message">Host-only diagnostic text; never model-visible or logged by the library.</param>
    public WebToolTimeoutException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates a marker with host diagnostics that are never surfaced by the library.</summary>
    /// <param name="message">Host-only diagnostic text; never model-visible or logged by the library.</param>
    /// <param name="innerException">Host-only exception chain; never surfaced by the library.</param>
    public WebToolTimeoutException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
