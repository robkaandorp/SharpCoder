namespace SharpCoder.Providers.Web;

/// <summary>
/// Host-supplied configuration for <see cref="OllamaWebClient"/>. Every value comes from the host:
/// this library performs no environment-variable, configuration-file, or other global credential
/// reads and installs no default HTTP client, handler, or endpoint.
/// </summary>
/// <remarks>
/// The supplied <see cref="HttpClient"/> keeps its ownership and lifetime with the host; the
/// client never disposes it. The host also owns TLS validation, logging, redirects, egress policy,
/// and any buffering performed inside its own handlers or <see cref="HttpContent"/>.
/// </remarks>
public sealed class OllamaWebClientOptions
{
    /// <summary>Default transport response byte cap: 1 MiB.</summary>
    public const int DefaultMaxResponseBytes = 1024 * 1024;

    /// <summary>Default library deadline: 15 seconds.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Smallest allowed finite library deadline.</summary>
    public static readonly TimeSpan MinimumDeadline = TimeSpan.FromSeconds(1);

    /// <summary>Largest allowed finite library deadline.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The absolute HTTPS base URI of the configured backend. An optional path prefix is allowed;
    /// it is normalized to exactly one trailing slash and the two API paths are resolved beneath it.
    /// </summary>
    public Uri BaseUri { get; set; } = null!;

    /// <summary>
    /// The host-owned credential accessor. It is invoked once per call, before any transport, and may
    /// return <see langword="null"/>, a blank value, or throw: each of those produces
    /// <see cref="WebToolErrorKind.CredentialUnavailable"/> without sending a request, and none of
    /// the exception's text ever escapes. Only boundary ASCII space/tab are trimmed; the remainder
    /// must be an RFC 6750 <c>b64token</c>.
    /// </summary>
    public Func<string?> CredentialAccessor { get; set; } = null!;

    /// <summary>
    /// The host-supplied HTTP client. One client per <see cref="OllamaWebClient"/>. Its ownership,
    /// lifetime, TLS validation, redirect policy and logging remain entirely with the host.
    /// </summary>
    public HttpClient HttpClient { get; set; } = null!;

    /// <summary>
    /// The maximum number of response bytes this library reads and retains from a success body.
    /// Defaults to <see cref="DefaultMaxResponseBytes"/> (1 MiB); must be positive. This bounds only
    /// the bytes Providers reads, not any prior buffering in host handlers, proxies, or
    /// <see cref="HttpContent"/>.
    /// </summary>
    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary>
    /// The library transport deadline. Defaults to <see cref="DefaultDeadline"/> (15 seconds).
    /// Finite values must be in the inclusive range 1–300 seconds;
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables ONLY this library deadline (caller
    /// cancellation and the host's own <see cref="HttpClient.Timeout"/> remain in force).
    /// </summary>
    public TimeSpan Deadline { get; set; } = DefaultDeadline;

    /// <summary>
    /// Optional host-owned, testable clock used for the library deadline. When
    /// <see langword="null"/>, <see cref="TimeProvider.System"/> is used. The host supplies this;
    /// the library never reads global configuration to choose one.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }
}
