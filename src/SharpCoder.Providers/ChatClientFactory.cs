#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;

using OllamaSharp;

using OpenAI;

using Polly;

using System.ClientModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoder.Providers;

/// <summary>
/// Creates <see cref="IChatClient"/> instances for various LLM providers.
/// Shared between Worker and Orchestrator (Brain).
/// </summary>
public static class ChatClientFactory
{
    private static Func<string?>? _tokenProvider;

    /// <summary>
    /// Registers a provider that supplies the GitHub access token to use for the Copilot
    /// provider. When set and it returns a non-whitespace value, that token is used in
    /// preference to the <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment variables; a
    /// <see langword="null"/> or whitespace value is treated as absent, so the environment
    /// variables are consulted instead. This is the bridge that lets the orchestrator inject the
    /// OAuth access token stored in the database without this shared class depending on the main
    /// project.
    /// </summary>
    public static void SetTokenProvider(Func<string?> provider) => _tokenProvider = provider;

    /// <summary>
    /// Resolves the token to use for the Copilot provider: the first non-whitespace of the
    /// stored OAuth token (via <see cref="SetTokenProvider"/>) → <c>GH_TOKEN</c> →
    /// <c>GITHUB_TOKEN</c>. Whitespace is treated as absent at every level, so a whitespace
    /// value never suppresses a later valid value.
    /// </summary>
    /// <returns>The first non-whitespace token, or <see langword="null"/> when all sources are
    /// absent or whitespace.</returns>
    /// <remarks>
    /// This is the single shared resolver for the Copilot path: both
    /// <see cref="CreateCopilotClient"/> and <see cref="IsTokenAvailable"/> consult it, so the
    /// factory's token selection and the public availability report can never diverge.
    /// </remarks>
    internal static string? ResolveCopilotToken()
    {
        var oauthToken = _tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(oauthToken))
            return oauthToken;

        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(ghToken))
            return ghToken;

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        return string.IsNullOrWhiteSpace(githubToken) ? null : githubToken;
    }

    /// <summary>
    /// Resolves the token to use for the GitHub Models provider: the first non-whitespace of the
    /// <c>GH_TOKEN</c> → <c>GITHUB_TOKEN</c> environment variables. Whitespace is treated as
    /// absent, so a whitespace value never suppresses a later valid value. Unlike
    /// <see cref="ResolveCopilotToken"/>, the stored OAuth token does <b>not</b> participate.
    /// </summary>
    /// <returns>The first non-whitespace environment token, or <see langword="null"/> when both
    /// variables are absent or whitespace.</returns>
    internal static string? ResolveGitHubEnvToken()
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(ghToken))
            return ghToken;

        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        return string.IsNullOrWhiteSpace(githubToken) ? null : githubToken;
    }

    /// <summary>
    /// Reports whether a non-whitespace Copilot token is available, using the same precedence as
    /// the Copilot client factory: stored OAuth token (via <see cref="SetTokenProvider"/>) →
    /// <c>GH_TOKEN</c> → <c>GITHUB_TOKEN</c>, with whitespace treated as absent at every level.
    /// </summary>
    /// <returns><see langword="true"/> when a non-whitespace Copilot token is available;
    /// <see langword="false"/> when all sources are absent or whitespace.</returns>
    /// <remarks>
    /// This is intentionally Copilot-only: the GitHub Models branch keeps its own internal
    /// resolver (<see cref="ResolveGitHubEnvToken"/>) and is not surfaced here.
    /// </remarks>
    public static bool IsTokenAvailable() => ResolveCopilotToken() != null;

    /// <summary>
    /// The environment variable that enables request/response diagnostics logging when it holds a
    /// non-empty directory path. This is the only environment variable consulted for diagnostics.
    /// </summary>
    internal const string DiagnosticsDirectoryEnvironmentVariable = "SHARPCODER_DIAGNOSTICS_DIR";

    /// <summary>
    /// The complete, immutable diagnostics resolution state: the explicit override plus the
    /// environment-variable cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why all three values live in one object.</b> They must be published together. Keeping the
    /// override in its own field made <see cref="ResetDiagnosticsCache"/> tear across two writes:
    /// with a cached environment value <c>C</c>, an explicit override <c>O</c> and the environment
    /// since changed to <c>N</c>, a lock-free resolver could observe the already-cleared override
    /// together with the not-yet-cleared cache and return <c>C</c> — which is neither the pre-reset
    /// answer (<c>O</c>) nor the post-reset one (<c>N</c>), and could route diagnostics to a stale
    /// directory. Holding the reset lock does not help, because resolvers read without taking it.
    /// </para>
    /// <para>
    /// The same reasoning applies within the cache itself: bundling <see cref="EnvRead"/> with
    /// <see cref="EnvValue"/> stops a resolver from seeing "already read" together with a
    /// freshly-cleared <see langword="null"/> value.
    /// </para>
    /// <para>
    /// Instances are never mutated after construction, so a state a resolver has already read stays
    /// internally consistent no matter what happens to the field afterwards. Every transition
    /// publishes a brand-new instance through a single <see cref="Interlocked.Exchange(ref object, object)"/>.
    /// </para>
    /// </remarks>
    private sealed class DiagnosticsState
    {
        /// <summary>
        /// The default state, and the state every reset republishes: no override, nothing cached.
        /// </summary>
        internal static readonly DiagnosticsState Initial = new(explicitOverride: null, envRead: false, envValue: null);

        private DiagnosticsState(string? explicitOverride, bool envRead, string? envValue)
        {
            ExplicitOverride = explicitOverride;
            EnvRead = envRead;
            EnvValue = envValue;
        }

        /// <summary>
        /// The explicit override set through <see cref="SetDiagnosticsDirectory"/>.
        /// <see langword="null"/> means "no override set" (the environment variable decides), while
        /// the empty string is the sentinel for "explicitly disabled" — which must win over a set
        /// environment variable.
        /// </summary>
        internal string? ExplicitOverride { get; }

        /// <summary>Whether the environment variable has already been read into this state.</summary>
        internal bool EnvRead { get; }

        /// <summary>The cached environment value, or <see langword="null"/> when disabled.</summary>
        internal string? EnvValue { get; }

        /// <summary>Returns a copy with a different override, preserving the environment cache.</summary>
        internal DiagnosticsState WithOverride(string? explicitOverride) =>
            new(explicitOverride, EnvRead, envValue: EnvValue);

        /// <summary>Returns a copy recording a completed environment read, preserving the override.</summary>
        internal DiagnosticsState WithEnvValue(string? envValue) =>
            new(ExplicitOverride, envRead: true, envValue);
    }

    /// <summary>
    /// The current diagnostics state. Read with a single <c>Volatile.Read</c> and replaced with a
    /// single <c>Interlocked.Exchange</c>, so a resolver always observes one whole, self-consistent
    /// state and never a mixture of two.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> declared <c>volatile</c>: it is passed by reference to
    /// <c>Volatile.Read</c>/<c>Interlocked.Exchange</c>, and a volatile field would raise CS0420.
    /// </remarks>
    private static DiagnosticsState _diagnosticsState = DiagnosticsState.Initial;

    /// <summary>
    /// Guards every <em>write</em> to <see cref="_diagnosticsState"/> — the override update, the
    /// commit of a resolved environment read, and the whole of <see cref="ResetDiagnosticsCache"/> —
    /// so the read-modify-write transitions cannot lose each other's updates.
    /// </summary>
    /// <remarks>
    /// The environment variable itself is deliberately read <em>outside</em> this lock (see
    /// <see cref="ResolveDiagnosticsDirectory"/>): holding a lock across the read would let a slow
    /// read block an unrelated reset, and the epoch check below already makes such a read safe.
    /// Readers never take the lock at all, which is why each published state must be self-contained.
    /// </remarks>
    private static readonly object DiagnosticsSyncRoot = new();

    /// <summary>
    /// Incremented by every <see cref="ResetDiagnosticsCache"/> call so that a resolve which
    /// sampled the environment before the reset can detect that its result is stale and discard it.
    /// </summary>
    /// <remarks>
    /// Always read and written under <see cref="DiagnosticsSyncRoot"/> at the points where it
    /// matters, which is what makes the "validate, then commit" step atomic with respect to a reset.
    /// </remarks>
    private static int _diagnosticsEnvEpoch;

    /// <summary>
    /// Reads the raw <c>SHARPCODER_DIAGNOSTICS_DIR</c> value.
    /// </summary>
    /// <remarks>
    /// Overridable as a test seam so a test can suspend a resolve at the exact moment between
    /// reading the environment and committing the cache, and thereby drive the reset/resolve race
    /// deterministically instead of relying on timing jitter. Production always uses the default.
    /// </remarks>
    internal static Func<string?> DiagnosticsEnvironmentReader { get; set; } =
        static () => Environment.GetEnvironmentVariable(DiagnosticsDirectoryEnvironmentVariable);

    /// <summary>
    /// Invoked by <see cref="ResolveDiagnosticsDirectory"/> immediately after it has sampled the
    /// diagnostics state, before it acts on it.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> in production, so the cached fast path stays a plain reference read
    /// plus a null check. It exists purely so a test can park a resolver at exactly the point where
    /// the cached fast path used to tear, run a reset while it is parked, and then assert on the
    /// value the resolver actually returns — driving the race deterministically instead of relying
    /// on timing jitter.
    /// </remarks>
    internal static Action? DiagnosticsCacheReadObserver { get; set; }

    /// <summary>
    /// Invoked by <see cref="ResetDiagnosticsCache"/> after the epoch has been bumped but before the
    /// cleared state is published.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> in production. It exists so a test can suspend a reset midway through
    /// its transition and let a concurrent resolver run, proving that the resolver observes either
    /// the whole pre-reset state or the whole post-reset state — never a mixture of the two.
    /// </remarks>
    internal static Action? DiagnosticsResetPublishObserver { get; set; }

    /// <summary>
    /// Enables or disables request/response diagnostics logging.
    /// </summary>
    /// <param name="path">
    /// A non-empty directory path enables diagnostics and writes the exchange files there.
    /// <see langword="null"/>, empty or whitespace disables diagnostics.
    /// </param>
    /// <remarks>
    /// Diagnostics are <b>disabled by default</b> — nothing is written and no file-system access is
    /// attempted until they are switched on, either through this method or through the
    /// <c>SHARPCODER_DIAGNOSTICS_DIR</c> environment variable. This method is an explicit override
    /// and always takes precedence over the environment variable, so
    /// <c>SetDiagnosticsDirectory(null)</c> disables logging even when the environment variable is
    /// set. Call <see cref="ResetDiagnosticsCache"/> to drop the override and fall back to the
    /// environment variable again.
    /// </remarks>
    public static void SetDiagnosticsDirectory(string? path)
    {
        // The empty string is the "explicitly disabled" sentinel; null would be indistinguishable
        // from "no override set" and would let the environment variable re-enable logging.
        var explicitOverride = string.IsNullOrWhiteSpace(path) ? string.Empty : path;

        // Read-modify-write: the new state must preserve the existing environment cache, so it is
        // serialized under the lock against resets and env-read commits. The publication itself is
        // still a single atomic reference write, so lock-free readers see one whole state.
        lock (DiagnosticsSyncRoot)
        {
            Interlocked.Exchange(ref _diagnosticsState, _diagnosticsState.WithOverride(explicitOverride));
        }
    }

    /// <summary>
    /// Clears the explicit override and the cached environment read, so the next resolution
    /// re-reads <c>SHARPCODER_DIAGNOSTICS_DIR</c>.
    /// </summary>
    /// <remarks>
    /// The override and the environment cache are cleared by publishing the single immutable
    /// <see cref="DiagnosticsState.Initial"/> state in <b>one</b> reference write, so a lock-free
    /// resolver observes either the whole pre-reset state or the whole post-reset state and never a
    /// mixture of the two. The epoch bump — which happens first — additionally invalidates any
    /// resolve that is already in flight: without it, a resolve that read the environment
    /// <em>before</em> this call could republish that stale value <em>after</em> it returned, so
    /// every later resolution would keep returning the pre-reset directory.
    /// </remarks>
    internal static void ResetDiagnosticsCache()
    {
        lock (DiagnosticsSyncRoot)
        {
            // Invalidate in-flight resolves first: each one re-checks this value before committing.
            _diagnosticsEnvEpoch++;

            // Test seam: lets a test run a resolver while the reset is midway through its
            // transition, proving the resolver cannot observe a half-applied state.
            DiagnosticsResetPublishObserver?.Invoke();

            Interlocked.Exchange(ref _diagnosticsState, DiagnosticsState.Initial);
        }
    }

    /// <summary>
    /// The single resolution point for the active diagnostics directory: the explicit override when
    /// one is set, otherwise the (cached) <c>SHARPCODER_DIAGNOSTICS_DIR</c> value, or
    /// <see langword="null"/> when diagnostics are disabled.
    /// </summary>
    /// <returns>The directory to write diagnostics into, or <see langword="null"/> when disabled.</returns>
    /// <remarks>
    /// <para>
    /// The override and the environment cache are obtained from <b>one</b> atomic read of a single
    /// immutable state, so a concurrent <see cref="ResetDiagnosticsCache"/> or
    /// <see cref="SetDiagnosticsDirectory"/> can never interleave between them and make this method
    /// return a value that matches neither the pre-transition nor the post-transition state.
    /// </para>
    /// <para>
    /// The environment read is optimistic: the epoch is sampled first, the value is read outside the
    /// lock, and the result is only committed if no <see cref="ResetDiagnosticsCache"/> ran in the
    /// meantime. If one did, the read is discarded and the whole resolution is retried against the
    /// post-reset state, so a reset can never be silently undone by a resolve that started before it.
    /// </para>
    /// </remarks>
    internal static string? ResolveDiagnosticsDirectory()
    {
        while (true)
        {
            // ONE atomic read of the whole state: the override and the cache can never disagree.
            var state = Volatile.Read(ref _diagnosticsState);

            // Test seam: lets a test suspend a resolver here, holding an already-sampled state,
            // while a reset runs. The snapshot keeps this resolver's answer self-consistent.
            DiagnosticsCacheReadObserver?.Invoke();

            var explicitOverride = state.ExplicitOverride;
            if (explicitOverride is not null)
                return explicitOverride.Length == 0 ? null : explicitOverride;

            if (state.EnvRead)
                return state.EnvValue;

            // Sample the epoch BEFORE reading, so any reset that overlaps the read is detected.
            var epoch = Volatile.Read(ref _diagnosticsEnvEpoch);

            var fromEnvironment = DiagnosticsEnvironmentReader();
            var resolved = string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;

            lock (DiagnosticsSyncRoot)
            {
                // A reset ran while we were reading: this value describes the pre-reset world and
                // must not be published. Start over against the state the reset left behind.
                if (_diagnosticsEnvEpoch != epoch)
                    continue;

                // An explicit override set while we were reading takes precedence over the
                // environment variable; re-run so the override branch above returns it.
                var current = _diagnosticsState;
                if (current.ExplicitOverride is not null)
                    continue;

                Interlocked.Exchange(ref _diagnosticsState, current.WithEnvValue(resolved));
                return resolved;
            }
        }
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for the given model string.
    /// The model string may include a provider prefix (e.g. "copilot/claude-sonnet-4.6").
    /// Reasoning effort is applied at the <see cref="ChatOptions"/> level, not via the model name.
    /// </summary>
    public static IChatClient Create(string? modelOverride = null)
    {
        var (provider, model) = ParseProviderAndModel(modelOverride);

        switch (provider)
        {
            case "ollama-cloud":
                {
                    var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                    if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("OLLAMA_API_KEY is required for ollama-cloud provider");

                    var httpClient = new HttpClient(CreateOllamaHandlerChain())
                    {
                        BaseAddress = new Uri("https://ollama.com"),
                        Timeout = Timeout.InfiniteTimeSpan,
                    };
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                    model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gpt-oss:120b";
                    var ollamaClient = new OllamaApiClient(httpClient);
                    ollamaClient.SelectedModel = model;

                    // OllamaSharp collapses ExtraHigh into "high" during ChatOptions→request
                    // mapping, so the distinction must be preserved before serialization.
                    // See OllamaExtraHighReasoningClient for the investigation and mechanism.
                    //
                    // The wrapper is also given ownership of httpClient: OllamaSharp only disposes
                    // an HTTP client it created itself, so an injected one would otherwise leak its
                    // handler chain and sockets.
                    return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
                }

            case "ollama-local":
                {
                    var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
                    model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3";

                    // Converted from `new OllamaApiClient(new Uri(url))` to HttpClient-based
                    // construction so the reasoning-effort mapping handler can be injected into the
                    // transport chain (see CreateOllamaHandlerChain). OllamaApiClient(HttpClient)
                    // uses the client's BaseAddress as the API endpoint, so behaviour is otherwise
                    // identical apart from the (previously 100 s) default timeout, which is now
                    // infinite — consistent with every other provider here, since reasoning models
                    // routinely exceed the default. The conversion also moves HttpClient ownership
                    // to us (OllamaSharp only disposes clients it created itself), which is why the
                    // returned wrapper is handed the instance to dispose.
                    var httpClient = new HttpClient(CreateOllamaHandlerChain())
                    {
                        BaseAddress = new Uri(url),
                        Timeout = Timeout.InfiniteTimeSpan,
                    };

                    var ollamaClient = new OllamaApiClient(httpClient);
                    ollamaClient.SelectedModel = model;

                    // See the ollama-cloud branch / OllamaExtraHighReasoningClient for why the
                    // ExtraHigh mapping must happen at the ChatOptions level, and why the wrapper
                    // takes ownership of the injected httpClient.
                    return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
                }

            case "github":
                {
                    var token = ResolveGitHubEnvToken();
                    if (token is null) throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for github provider");

                    model ??= Environment.GetEnvironmentVariable("GITHUB_MODEL") ?? "openai/gpt-4.1";

                    var openAiClient = new OpenAIClient(
                        new ApiKeyCredential(token),
                        new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai") }
                    );

                    // GitHub Models does not accept a reasoning effort above "high"; clamp the
                    // internal ExtraHigh level at the provider boundary. The wrapper owns the
                    // inner client's disposal.
                    return new ReasoningEffortClampingClient(
                        openAiClient.GetChatClient(model).AsIChatClient(), ReasoningEffort.High);
                }

            case "copilot":
                return CreateCopilotClient(model ?? Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.6");

            default:
                throw new InvalidOperationException($"Unknown LLM provider: '{provider}'");
        }
    }

    /// <summary>
    /// Extracts an optional provider prefix from the model string.
    /// "copilot/claude-sonnet-4.6" → ("copilot", "claude-sonnet-4.6")
    /// "claude-sonnet-4.6" → (env LLM_PROVIDER, "claude-sonnet-4.6")
    /// null → (env LLM_PROVIDER, null)
    /// </summary>
    public static (string provider, string? model) ParseProviderAndModel(string? modelOverride)
    {
        var defaultProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        if (string.IsNullOrEmpty(modelOverride))
            return (defaultProvider, null);

        var slashIndex = modelOverride.IndexOf('/');
        if (slashIndex <= 0)
            return (defaultProvider, modelOverride);

        var prefix = modelOverride.Substring(0, slashIndex).ToLowerInvariant();

        // Only treat as provider prefix if it matches a known provider.
        if (prefix is "copilot" or "ollama-cloud" or "ollama-local" or "github")
            return (prefix, modelOverride.Substring(slashIndex + 1));

        return (defaultProvider, modelOverride);
    }

    /// <summary>
    /// Models that must use the /responses endpoint instead of /chat/completions.
    /// </summary>
    public static bool RequiresResponsesEndpoint(string model)
    {
        return model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The value the Ollama API uses for the highest thinking level, mapped from the internal
    /// <c>extra_high</c> wire value.
    /// </summary>
    internal const string OllamaExtraHighMapping = "max";

    /// <summary>The Ollama request property carrying the reasoning/thinking level.</summary>
    internal const string OllamaReasoningPropertyName = "think";

    /// <summary>
    /// Builds the Ollama transport chain: <c>ResilienceHandler → ReasoningEffortMappingHandler →
    /// HttpClientHandler</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is defence-in-depth only. The load-bearing ExtraHigh mechanism for Ollama is
    /// <see cref="OllamaExtraHighReasoningClient"/>, which acts at the <see cref="ChatOptions"/>
    /// level — see that type for the full investigation. The handler still runs so that a
    /// hand-built body (or a future OllamaSharp that emits the canonical <c>extra_high</c> value,
    /// or a <c>reasoning_effort</c>/<c>reasoning.effort</c> property) is normalized too. It never
    /// rewrites <c>"high"</c>, which is indistinguishable from a genuine
    /// <see cref="ReasoningEffort.High"/> request.
    /// </para>
    /// </remarks>
    private static HttpMessageHandler CreateOllamaHandlerChain() =>
        CreateResilientHandler(BuildHandlerChain(
            new ReasoningEffortMappingHandler(OllamaExtraHighMapping, customPropertyName: OllamaReasoningPropertyName),
            new HttpClientHandler()));

    /// <summary>
    /// Composes an <see cref="HttpMessageHandler"/> chain from outermost to innermost without
    /// disturbing links that are already wired up by the caller.
    /// </summary>
    /// <param name="handlers">
    /// The handlers in outermost→innermost order. Every element except the last must be a
    /// <see cref="DelegatingHandler"/>; the last element is the terminal handler (typically an
    /// <see cref="HttpClientHandler"/> or another handler that actually performs the transport).
    /// </param>
    /// <returns>The outermost handler of the composed chain.</returns>
    /// <remarks>
    /// This replaces the previous pattern where <see cref="CreateResilientHandler"/> assigned
    /// <c>InnerHandler</c> on a caller-supplied handler that might already have had one — silently
    /// disconnecting the existing chain. <c>BuildHandlerChain</c> links each handler to its
    /// successor exactly once, so the caller decides the full order.
    /// </remarks>
    internal static HttpMessageHandler BuildHandlerChain(params HttpMessageHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("At least one handler is required.", nameof(handlers));

        for (var i = 0; i < handlers.Length - 1; i++)
        {
            if (handlers[i] is not DelegatingHandler delegating)
                throw new ArgumentException(
                    $"Handler at index {i} ({handlers[i].GetType().Name}) must be a DelegatingHandler because it is not the terminal handler.",
                    nameof(handlers));

            delegating.InnerHandler = handlers[i + 1];
        }

        return handlers[0];
    }

    /// <summary>
    /// Creates a resilient <see cref="HttpMessageHandler"/> chain with per-attempt timeout
    /// and retry policy.
    /// </summary>
    /// <param name="innerHandler">
    /// Optional handler placed <em>beneath</em> the resilience handler. When <see langword="null"/>,
    /// a fresh <see cref="HttpClientHandler"/> is used. The supplied handler's own
    /// <c>InnerHandler</c> is never overwritten, so callers can pass an already-composed chain
    /// (e.g. built with <see cref="BuildHandlerChain"/>).
    /// </param>
    /// <param name="retryDelay">
    /// Optional override for the base retry delay. Production uses the default 5 s; tests pass a
    /// near-zero value so retry behaviour can be exercised without slowing the suite. The retry
    /// count, backoff type and timeout are identical either way, so tests still exercise the real
    /// strategy.
    /// </param>
    internal static HttpMessageHandler CreateResilientHandler(
        HttpMessageHandler? innerHandler = null, TimeSpan? retryDelay = null)
    {
        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = retryDelay ?? TimeSpan.FromSeconds(5),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = static args =>
                {
                    Console.Error.WriteLine(
                        $"[Resilience] HTTP retry #{args.AttemptNumber} after {args.RetryDelay.TotalSeconds:F0}s — " +
                        (args.Outcome.Exception?.Message ?? $"HTTP {(int?)args.Outcome.Result?.StatusCode}"));
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMinutes(20))
            .Build();

        return BuildHandlerChain(
            new ResilienceHandler(retryPipeline),
            innerHandler ?? new HttpClientHandler());
    }

    /// <summary>
    /// The value the GitHub Copilot API expects for the internal <c>extra_high</c> reasoning effort.
    /// The OpenAI SDK serializes <see cref="ReasoningEffort.ExtraHigh"/> as <c>"xhigh"</c> already,
    /// but our own wire format uses <c>extra_high</c>; the mapping handler normalizes both.
    /// </summary>
    internal const string CopilotExtraHighMapping = "xhigh";

    private static IChatClient CreateCopilotClient(string model)
    {
        var ghToken = ResolveCopilotToken();
        if (ghToken is null) throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN is required for copilot provider");

        var useResponsesApi = RequiresResponsesEndpoint(model);

        // The OpenAIClient is built inside the factory closure so it can be handed the HttpClient
        // created by the core. The core's wrapper owns that HttpClient's disposal: the OpenAI SDK
        // does not dispose an injected transport (mirroring the Ollama ownership rationale), so
        // without it the resilience/mapping handler chain and its sockets would leak.
        return CreateCopilotClientCore(
            useResponsesApi, CopilotExtraHighMapping, new HttpClientHandler(),
            httpClient =>
            {
                var openAiClient = new OpenAIClient(
                    new ApiKeyCredential(ghToken),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri("https://api.githubcopilot.com"),
                        Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient),
                        NetworkTimeout = TimeSpan.FromMinutes(30)
                    }
                );

                return useResponsesApi
                    ? openAiClient.GetResponsesClient().AsIChatClient(model)
                    : openAiClient.GetChatClient(model).AsIChatClient();
            });
    }

    /// <summary>
    /// Shared construction core for the Copilot provider: builds the <see cref="HttpClient"/> over
    /// the production handler chain, lets <paramref name="clientFactory"/> build the inner
    /// <see cref="IChatClient"/> over it, and wraps the result in an
    /// <see cref="OwnedCopilotChatClient"/> that takes ownership of the <see cref="HttpClient"/>'s
    /// disposal.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="extraHighMapping">The provider value <c>extra_high</c> maps to.</param>
    /// <param name="terminalHandler">The innermost handler that performs the actual transport.</param>
    /// <param name="clientFactory">Builds the inner client over the constructed <see cref="HttpClient"/>.</param>
    /// <remarks>
    /// On any construction failure (factory throw or null result) the <see cref="HttpClient"/> is
    /// disposed best-effort — without masking the original exception — before the failure rethrows,
    /// so a partially built handler chain and its sockets never leak.
    /// </remarks>
    private static IChatClient CreateCopilotClientCore(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler,
        Func<HttpClient, IChatClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(terminalHandler);
        ArgumentNullException.ThrowIfNull(clientFactory);

        var httpClient = new HttpClient(CreateCopilotHandlerChain(
            useResponsesApi, extraHighMapping, terminalHandler))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var inner = clientFactory(httpClient);
            ArgumentNullException.ThrowIfNull(inner);
            return new OwnedCopilotChatClient(inner, httpClient);
        }
        catch
        {
            // Best-effort cleanup of the transport on construction failure. A throwing disposal
            // must not replace the original construction error.
            try
            {
                httpClient.Dispose();
            }
            catch
            {
                // Preserve the original exception.
            }

            throw;
        }
    }

    /// <summary>
    /// Test seam: builds the FULL production Copilot client stack — the owned
    /// <see cref="HttpClient"/> over the production handler chain (resilience → Copilot handler →
    /// reasoning-effort mapping with <see cref="CopilotExtraHighMapping"/>) with an injectable
    /// terminal handler and an injectable inner-client factory, exactly as the production
    /// <see cref="CreateCopilotClient"/> does apart from the token/endpoint wiring. Has no token
    /// dependency.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    /// <param name="clientFactory">Builds the inner client over the constructed <see cref="HttpClient"/>.</param>
    internal static IChatClient CreateCopilotClientForTestFull(
        bool useResponsesApi, HttpMessageHandler terminalHandler,
        Func<HttpClient, IChatClient> clientFactory)
        => CreateCopilotClientCore(useResponsesApi, CopilotExtraHighMapping, terminalHandler, clientFactory);

    /// <summary>
    /// An <see cref="IChatClient"/> decorator that wraps an inner client together with an
    /// <see cref="HttpClient"/> this wrapper owns, and disposes both on <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this class exists:</b> the OpenAI SDK does not dispose an <see cref="HttpClient"/>
    /// transport injected via <c>HttpClientPipelineTransport</c> (it only disposes a client it
    /// created itself), so the factory-built <see cref="HttpClient"/> — and with it the whole
    /// resilience/mapping handler chain and its sockets — would otherwise leak. This wrapper is the
    /// only object the factory hands back, so it takes ownership of the transport, mirroring the
    /// <see cref="OllamaExtraHighReasoningClient"/> ownership model.
    /// </para>
    /// <para>
    /// <b>Dispose semantics:</b> the inner client is disposed first, then the owned
    /// <see cref="HttpClient"/>. Both cleanups are always attempted. A plain <c>try/finally</c>
    /// would not do: if both throw, the transport's exception would replace — and thereby hide —
    /// the inner client's. The failures are captured instead, so a single failure propagates as-is
    /// (original stack preserved via <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>) and a double failure
    /// surfaces as an <see cref="AggregateException"/> that still carries the primary error first.
    /// Disposal is idempotent: an <see cref="System.Threading.Interlocked.Exchange(ref int, int)"/> guard ensures concurrent or
    /// repeated <see cref="Dispose"/> calls run the cleanup exactly once.
    /// </para>
    /// </remarks>
    internal sealed class OwnedCopilotChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private readonly HttpClient _ownedHttpClient;
        private int _disposed;

        /// <summary>
        /// Creates a wrapper around <paramref name="inner"/> that owns
        /// <paramref name="ownedHttpClient"/>'s disposal.
        /// </summary>
        /// <param name="inner">The inner client. This wrapper owns its disposal.</param>
        /// <param name="ownedHttpClient">The <see cref="HttpClient"/> this wrapper owns.</param>
        public OwnedCopilotChatClient(IChatClient inner, HttpClient ownedHttpClient)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(ownedHttpClient);
            _inner = inner;
            _ownedHttpClient = ownedHttpClient;
        }

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.GetResponseAsync(messages, options, cancellationToken);

        /// <inheritdoc />
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        /// <summary>
        /// Disposes the inner client first, then the owned <see cref="HttpClient"/>.
        /// Idempotent: concurrent and repeated calls run the cleanup exactly once.
        /// </summary>
        /// <remarks>
        /// Both cleanups are always attempted. A single failure propagates as-is (original stack
        /// preserved via <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>); if both throw, an
        /// <see cref="AggregateException"/> carrying both is thrown, with the inner client's failure
        /// first so the original/primary error is never hidden.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Exception? innerFailure = null;
            Exception? httpClientFailure = null;

            try
            {
                _inner.Dispose();
            }
            catch (Exception ex)
            {
                innerFailure = ex;
            }

            try
            {
                _ownedHttpClient.Dispose();
            }
            catch (Exception ex)
            {
                httpClientFailure = ex;
            }

            if (innerFailure is not null && httpClientFailure is not null)
                throw new AggregateException(
                    "Both the inner Copilot client and its owned HttpClient failed to dispose.",
                    innerFailure, httpClientFailure);

            if (innerFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(innerFailure).Throw();

            if (httpClientFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(httpClientFailure).Throw();
        }
    }

    /// <summary>
    /// Builds the outbound handler chain used for the Copilot provider:
    /// <c>ResilienceHandler → CopilotResponsesHandler/CopilotChoiceMergingHandler →
    /// ReasoningEffortMappingHandler → terminalHandler</c>.
    /// </summary>
    /// <remarks>
    /// Order matters: the Copilot handler rewrites the request body first (tool-call argument
    /// fix-ups / responses-API input reconstruction), then the mapping handler translates
    /// <c>extra_high</c> into the provider spelling on the final body, then the terminal handler
    /// transmits it.
    /// </remarks>
    private static HttpMessageHandler CreateCopilotHandlerChain(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler)
        => CreateCopilotHandlerChain(useResponsesApi, extraHighMapping, terminalHandler, out _);

    /// <summary>
    /// Builds the Copilot chain and also hands back the Copilot handler instance, so tests can
    /// assert on its accumulated conversation state.
    /// </summary>
    private static HttpMessageHandler CreateCopilotHandlerChain(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler,
        out DelegatingHandler copilotHandler, TimeSpan? retryDelay = null)
    {
        copilotHandler = useResponsesApi
            ? new CopilotResponsesHandler()
            : new CopilotChoiceMergingHandler();

        return CreateResilientHandler(
            BuildHandlerChain(
                copilotHandler,
                new ReasoningEffortMappingHandler(extraHighMapping),
                terminalHandler),
            retryDelay);
    }

    /// <summary>
    /// Test seam: builds an <see cref="HttpClient"/> over the FULL production Copilot handler
    /// chain (resilience → Copilot handler → reasoning-effort mapping) but with an injectable
    /// terminal handler, so the chain can be exercised without any network access.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="extraHighMapping">The provider value <c>extra_high</c> maps to.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    internal static HttpClient CreateCopilotClientForTest(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler)
        => CreateCopilotClientForTest(useResponsesApi, extraHighMapping, terminalHandler, out _);

    /// <summary>
    /// Test seam overload that also exposes the Copilot handler instance, so retry tests can assert
    /// that its conversation state was not corrupted or duplicated.
    /// </summary>
    /// <param name="useResponsesApi">Whether to use the /responses branch of the chain.</param>
    /// <param name="extraHighMapping">The provider value <c>extra_high</c> maps to.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    /// <param name="copilotHandler">The Copilot handler instance inside the chain.</param>
    /// <param name="retryDelay">Optional retry-delay override so retry tests run fast.</param>
    internal static HttpClient CreateCopilotClientForTest(
        bool useResponsesApi, string extraHighMapping, HttpMessageHandler terminalHandler,
        out DelegatingHandler copilotHandler, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(terminalHandler);
        var chain = CreateCopilotHandlerChain(
            useResponsesApi, extraHighMapping, terminalHandler, out copilotHandler, retryDelay);
        return new HttpClient(chain)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Test seam: builds the FULL production Ollama client stack —
    /// <see cref="OllamaExtraHighReasoningClient"/> over an <see cref="OllamaApiClient"/> whose
    /// transport is the production handler chain (resilience → reasoning-effort mapping) — but with
    /// an injectable terminal handler, so the real
    /// <c>ChatOptions → OllamaApiClient → handler → terminal</c> path can be exercised offline.
    /// </summary>
    /// <param name="model">The model to select on the underlying client.</param>
    /// <param name="terminalHandler">The innermost handler that replaces the real transport.</param>
    internal static IChatClient CreateOllamaClientForTest(string model, HttpMessageHandler terminalHandler)
    {
        ArgumentNullException.ThrowIfNull(terminalHandler);

        var httpClient = new HttpClient(CreateResilientHandler(BuildHandlerChain(
            new ReasoningEffortMappingHandler(OllamaExtraHighMapping, customPropertyName: OllamaReasoningPropertyName),
            terminalHandler)))
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ollamaClient = new OllamaApiClient(httpClient);
        ollamaClient.SelectedModel = model;

        // Mirror production ownership so disposal behaviour is exercised by tests too.
        return new OllamaExtraHighReasoningClient(ollamaClient, httpClient);
    }

    /// <summary>
    /// The GitHub Copilot API splits tool_calls and text content into separate choices.
    /// The OpenAI SDK only reads choices[0], losing the tool_calls. This handler
    /// merges all choices into a single choice so the SDK sees both text and tool_calls.
    /// </summary>
    internal sealed class CopilotChoiceMergingHandler : DelegatingHandler
    {
        private int _requestCount;

        public CopilotChoiceMergingHandler() { }
        public CopilotChoiceMergingHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            if (request.Content != null)
            {
                var reqBody = await request.Content.ReadAsStringAsync();
                LogCompletionsExchange(seq, "request", reqBody);
                // Fix tool_call arguments that the Copilot API proxy can't parse.
                // Claude streaming returns empty arguments ("") for parameterless calls,
                // which the SDK accumulates as the string "null". The Copilot→Anthropic
                // proxy needs a valid JSON object string for tool_use.input.
                reqBody = FixToolCallArguments(reqBody);
                request.Content = new StringContent(reqBody, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                LogCompletionsExchange(seq, "error", $"HTTP {(int)response.StatusCode}: {errBody}");
                response.Content = new StringContent(errBody, System.Text.Encoding.UTF8, "application/json");
                return response;
            }

            var body = await response.Content.ReadAsStringAsync();
            LogCompletionsExchange(seq, "response", body);

            try
            {
                var json = JsonNode.Parse(body);
                var choices = json?["choices"]?.AsArray();

                // Empty choices array: Copilot API sometimes returns this after the final
                // tool result is sent back. Synthesize a minimal stop choice so the SDK
                // doesn't crash with "Index was out of range" on choices[0].
                if (choices is { Count: 0 } && json is not null)
                {
                    json["choices"] = new JsonArray(
                        JsonNode.Parse("""{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}"""));
                    body = json.ToJsonString();
                    return ReplaceContent(response, body);
                }

                if (choices == null || choices.Count <= 1) return ReplaceContent(response, body);

                JsonObject? toolChoice = null;
                string? textContent = null;

                foreach (var c in choices)
                {
                    if (c == null) continue;
                    var msg = c["message"];
                    if (msg == null) continue;

                    if (msg["tool_calls"] is JsonArray { Count: > 0 })
                        toolChoice = c.AsObject();
                    else if (msg["content"] is JsonValue val && val.TryGetValue<string>(out var text) && text.Length > 0)
                        textContent = text;
                }

                if (toolChoice != null)
                {
                    if (textContent != null && toolChoice["message"] is JsonObject merged)
                    {
                        merged["content"] = textContent;
                    }

                    toolChoice.Parent?.AsArray().Remove(toolChoice);
                    json!["choices"] = new JsonArray(toolChoice);
                    body = json.ToJsonString();
                }
            }
            catch (Exception)
            {
                // If merging fails, return the original response unchanged
            }

            return ReplaceContent(response, body);
        }

        private static HttpResponseMessage ReplaceContent(HttpResponseMessage response, string body)
        {
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return response;
        }

        /// <summary>
        /// Writes one side of a chat-completions exchange to the diagnostics directory.
        /// </summary>
        /// <remarks>
        /// Diagnostics are opt-in: when no directory is configured (see
        /// <see cref="SetDiagnosticsDirectory"/> and <c>SHARPCODER_DIAGNOSTICS_DIR</c>) this method
        /// performs no file-system access at all. When enabled, logging stays best-effort and any
        /// I/O failure is swallowed so it can never break a request.
        /// </remarks>
        private static void LogCompletionsExchange(int seq, string phase, string content)
        {
            var root = ResolveDiagnosticsDirectory();
            if (root is null) return;

            try
            {
                var dir = Path.Combine(root, "chat-completions");
                Directory.CreateDirectory(dir);
                var fileName = $"{seq:D4}_{phase}.json";
                File.WriteAllText(Path.Combine(dir, fileName), content);
            }
            catch { /* best-effort logging */ }
        }

        /// <summary>
        /// Fixes tool_call arguments in outgoing request messages.
        /// When Claude streams a tool call with no arguments, the SDK accumulates
        /// the empty argument chunks as the literal string "null". The Copilot API
        /// proxy expects a valid JSON object for Anthropic's tool_use.input field.
        /// </summary>
        internal static string FixToolCallArguments(string requestBody)
        {
            try
            {
                var json = JsonNode.Parse(requestBody);
                var messages = json?["messages"]?.AsArray();
                if (messages is null) return requestBody;

                bool modified = false;
                foreach (var msg in messages)
                {
                    var toolCalls = msg?["tool_calls"]?.AsArray();
                    if (toolCalls is null) continue;

                    foreach (var tc in toolCalls)
                    {
                        var func = tc?["function"];
                        if (func is null) continue;

                        var argsNode = func["arguments"];
                        var argsStr = argsNode?.GetValue<string>();
                        if (argsStr is null or "" or "null")
                        {
                            func["arguments"] = "{}";
                            modified = true;
                        }
                    }
                }

                return modified ? json!.ToJsonString() : requestBody;
            }
            catch
            {
                return requestBody;
            }
        }
    }

    /// <summary>
    /// Handles Responses API requests for Copilot, which doesn't support
    /// <c>previous_response_id</c>. Strips the id and reconstructs the full conversation in the
    /// request body by carrying forward the originating request's input messages (system + user)
    /// plus all accumulated turn history (previous outputs + tool results).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single handler instance serves <b>many</b> conversations — the chain is built once per
    /// client and every request flows through it — so the conversation state is keyed per
    /// conversation instead of living in instance fields. Each successful non-streaming response
    /// writes an entry under its own response <c>id</c>; a follow-up naming that id in
    /// <c>previous_response_id</c> resolves it and continues from there. Entries are handed out as
    /// deep clones, so two follow-ups branching from the same parent can never observe each
    /// other's additions.
    /// </para>
    /// <para>
    /// DEGRADED MODE: a <c>previous_response_id</c> that is missing, malformed or no longer in the
    /// store (evicted by the <see cref="MaxEntries"/> bound) simply resolves to nothing, and the
    /// request is transformed exactly like a first request — the id stripped and only the current
    /// input inlined. Likewise, a successful response carrying no usable <c>id</c> writes no entry
    /// at all, so the next follow-up naming it degrades in the same way. Degrading loses context,
    /// never correctness: no state from another conversation is ever mixed in.
    /// </para>
    /// <para>
    /// STREAMING IS FULLY ISOLATED TOO. A successful <c>text/event-stream</c> response is handed
    /// back untouched, but every byte the SDK's parser reads also flows through a transport tee
    /// (<see cref="TeeStreamContent"/>) into an incremental SSE parser
    /// (<see cref="StreamingResponseParser"/>). The parser lifts the real response id out of the
    /// FIRST <c>response.created</c> event and commits the staged state under that id — exactly
    /// like a non-streaming exchange — so concurrent streaming conversations no longer share
    /// anything. It then buffers the <c>response.output_item.done</c> items and amends the entry
    /// with them on <c>response.completed</c>. When no valid id is ever seen, NO entry is written
    /// under any key (there is no fallback slot), and the next follow-up degrades exactly like a
    /// non-streaming one.
    /// </para>
    /// <para>
    /// RACING FOLLOW-UP LIMITATION: the commit happens when the id arrives and the output
    /// amendment only when the stream completes, so a follow-up that raced in between would read
    /// the entry WITHOUT its output items. This is theoretical rather than practical: the SDK
    /// consumes the whole stream before the caller can issue the follow-up that names the response
    /// id, so the amendment has always landed by then. Likewise a stream that ends WITHOUT a
    /// <c>response.completed</c> event drops its buffered items — the entry keeps its committed
    /// base and history, degrading context but never correctness.
    /// </para>
    /// </remarks>
    internal sealed class CopilotResponsesHandler : DelegatingHandler
    {
        /// <summary>
        /// The durable state of one conversation: the originating request's input plus every turn
        /// accumulated since. Instances in the store are never handed out directly — readers get a
        /// deep clone — so an entry is effectively immutable once committed.
        /// </summary>
        private sealed class ConversationState
        {
            public required JsonArray BaseInput { get; init; }
            public required List<JsonNode> TurnHistory { get; init; }

            /// <summary>
            /// A monotonically increasing stamp assigned under the store lock by
            /// <see cref="StoreConversationState"/> when this instance is written.
            /// </summary>
            /// <remarks>
            /// It exists solely so the streaming output amendment can tell "the entry I committed"
            /// from "a different entry that happens to sit under the same key now" — a
            /// re-commit, an eviction plus a re-insert, or another conversation reusing the id all
            /// produce a NEW generation, and an amendment carrying a stale one is silently dropped.
            /// Clones handed out by <see cref="TryResolveConversationState"/> deliberately do NOT
            /// carry it (they default to 0): the non-streaming paths and the test seams neither
            /// see nor depend on it.
            /// </remarks>
            public long Generation { get; init; }
        }

        /// <summary>Guards <see cref="_store"/> and <see cref="_insertionOrder"/> bookkeeping.</summary>
        /// <remarks>
        /// The lock covers store bookkeeping and cloning only — never any I/O. Nothing awaited is
        /// ever executed while it is held.
        /// </remarks>
        private readonly object _storeLock = new();

        /// <summary>Conversation state keyed by the response id that produced it.</summary>
        private readonly Dictionary<string, ConversationState> _store = new();

        /// <summary>Key insertion order, used to evict the oldest entry once the bound is hit.</summary>
        private readonly Queue<string> _insertionOrder = new();

        /// <summary>
        /// Upper bound on retained conversations. Long-lived clients would otherwise accumulate one
        /// entry per response for the lifetime of the process; evicting the oldest entry degrades
        /// that conversation's next follow-up rather than leaking memory.
        /// </summary>
        private const int MaxEntries = 50;

        /// <summary>
        /// Source of <see cref="ConversationState.Generation"/> stamps. Only ever read and
        /// incremented while <see cref="_storeLock"/> is held.
        /// </summary>
        private long _generationCounter;

        private int _requestCount;

        /// <summary>
        /// Conversation state produced by the request transformation, staged on the request itself
        /// so it survives across resilience retry attempts and is committed by whichever attempt
        /// finally receives an authoritative response.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="BaseInput"/> and <see cref="TurnHistory"/> are always either both set or both
        /// <see langword="null"/>. Both <see langword="null"/> means NOTHING was staged (the
        /// request's <c>input</c> was not a JSON array), which is deliberately distinct from a
        /// staged-but-empty history: the streaming commit is a no-op for the former and writes an
        /// entry under the streamed response id for the latter.
        /// </para>
        /// <para>
        /// Internal rather than private only so <see cref="StreamingResponseParser"/> — which
        /// commits this state from inside the transport tee — can name it in its constructor
        /// signature. It stays an implementation detail of this handler.
        /// </para>
        /// </remarks>
        internal sealed class PendingConversationState
        {
            public JsonArray? BaseInput { get; init; }
            public List<JsonNode>? TurnHistory { get; init; }

            /// <summary>Whether this request staged any state at all.</summary>
            public bool HasStagedState => BaseInput is not null && TurnHistory is not null;

            /// <summary>Guards against committing the same staged state more than once.</summary>
            public bool Committed { get; set; }
        }

        /// <summary>
        /// Carries the transformation result across retry attempts of the same request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This handler sits <em>beneath</em> the <c>ResilienceHandler</c>, so a retry re-enters
        /// <see cref="SendAsync"/> with the <b>same</b> <see cref="HttpRequestMessage"/> instance
        /// carrying the <b>already-transformed</b> body (verified with a forced-transient-failure
        /// probe: Polly reuses the request instance and preserves both <c>request.Options</c> and any
        /// replaced <c>Content</c> across attempts). Each attempt is a fresh <see cref="SendAsync"/>
        /// stack frame, so anything staged in a local would be lost by the attempt that actually
        /// succeeds — the staged state therefore has to live on the request.
        /// </para>
        /// <para>
        /// The presence of this entry also serves as the idempotence marker. Without it a retry
        /// would see a body that no longer contains <c>previous_response_id</c> — because the first
        /// attempt removed it — and would fall into the "first request" branch, staging the fully
        /// expanded conversation as the base input and corrupting every later reconstruction built
        /// on the entry this exchange commits. <see cref="HttpRequestOptions"/> travels with the
        /// request instance and is never sent over the wire, which makes it the right place for
        /// this attempt-scoped state.
        /// </para>
        /// </remarks>
        private static readonly HttpRequestOptionsKey<PendingConversationState> PendingStateKey =
            new("SharpCoder.Providers.CopilotResponsesHandler.PendingState");

        public CopilotResponsesHandler() { }
        public CopilotResponsesHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var seq = Interlocked.Increment(ref _requestCount);

            var isResponsesRequest = request.Content != null
                && request.RequestUri?.AbsolutePath?.Contains("responses") == true;

            // Idempotence guard: on a retry the body is already transformed and the resulting
            // conversation state is already staged on the request, so re-running the transformation
            // would mutate the body a second time. Reuse the staged state instead.
            var hasPendingState = request.Options.TryGetValue(PendingStateKey, out var pending);

            if (isResponsesRequest && !hasPendingState)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var json = JsonNode.Parse(body);

                if (json is JsonObject obj)
                {
                    // STATE SELECTION — identical for streaming and non-streaming requests:
                    // 1. A present, valid, found previous_response_id resolves that conversation.
                    // 2. Otherwise → degraded/fresh: first-request transformation.
                    ConversationState? parent = null;
                    var bodyWasRewritten = false;
                    if (obj.ContainsKey("previous_response_id"))
                    {
                        // The Copilot endpoint rejects the property outright, so it is always
                        // stripped — whether or not it resolves to anything here.
                        var previousId = ReadIdString(obj["previous_response_id"]);
                        obj.Remove("previous_response_id");
                        bodyWasRewritten = true;
                        parent = TryResolveConversationState(previousId);
                    }

                    var currentInput = obj["input"] as JsonArray;

                    if (parent is not null)
                    {
                        // CONTINUATION: rebuild the conversation the API can no longer track for
                        // us — the originating input, every accumulated turn, then this request's
                        // input. The parent is already a private deep clone.
                        var combined = new JsonArray();

                        // 1. Original system + user messages from the originating request.
                        foreach (var item in parent.BaseInput)
                            combined.Add(item!.DeepClone());

                        // 2. All accumulated turn history (previous outputs + tool results).
                        foreach (var item in parent.TurnHistory)
                            combined.Add(item.DeepClone());

                        // 3. Current input (new tool results from FunctionInvokingChatClient).
                        if (currentInput is not null)
                            foreach (var item in currentInput)
                                combined.Add(item!.DeepClone());

                        obj["input"] = combined;
                        bodyWasRewritten = true;
                    }

                    if (bodyWasRewritten)
                    {
                        // DEGRADED MODE: when the id resolved to nothing, this rewrite is only the
                        // strip — the body keeps just the current input, exactly like a first
                        // request. The property must go either way: the endpoint rejects it.
                        body = obj.ToJsonString();
                        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    }

                    if (currentInput is not null)
                    {
                        // Staged, not committed: only an authoritative response promotes this.
                        // CONTINUATION stages the parent's base plus the parent's history with the
                        // current input APPENDED — never added to the base, so the input is stored
                        // exactly once. FRESH stages the current input as the base with an EMPTY
                        // (but present) history.
                        var stagedBase = parent?.BaseInput ?? (JsonArray)currentInput.DeepClone();
                        var stagedHistory = parent?.TurnHistory ?? new List<JsonNode>();

                        if (parent is not null)
                            foreach (var item in currentInput)
                                stagedHistory.Add(item!.DeepClone());

                        pending = new PendingConversationState
                        {
                            BaseInput = stagedBase,
                            TurnHistory = stagedHistory,
                        };
                    }
                    else
                    {
                        // NON-ARRAY INPUT: nothing is staged at all — neither base nor history,
                        // even when a parent resolved for the transformation above. There is no
                        // input array to record, and inventing one would corrupt the conversation.
                        // "Nothing staged" is distinct from "staged with an empty history": the
                        // streaming commit is a no-op for the former.
                        pending = new PendingConversationState();
                    }

                    // Always staged, even when empty: its presence is the idempotence marker that
                    // stops a retry from re-running the transformation on an already-rewritten body.
                    request.Options.Set(PendingStateKey, pending);
                }

                LogResponsesExchange(seq, "request", body);
            }

            var response = await base.SendAsync(request, ct);

            var contentType = response.Content?.Headers?.ContentType?.MediaType;

            // HTTP media types are case-insensitive, and the media type may be accompanied by
            // parameters (e.g. "Text/Event-Stream; charset=utf-8"). ContentType.MediaType already
            // strips parameters but preserves the sender's casing, so the comparison must ignore
            // case. Getting this wrong is not cosmetic: a failed response whose casing differs would
            // fall through to ReadAsStringAsync below and consume an unbounded event stream,
            // blocking until timeout and preventing the resilience layer from retrying.
            var isStreaming = string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

            if (!response.IsSuccessStatusCode)
            {
                // Failure — streaming or not — must never commit conversation state. The resilience
                // handler above may retry, which re-enters this method with the same request
                // instance; the staged state makes that attempt skip the transformation and lets it
                // commit exactly once if it succeeds. If the retries are exhausted the caller sees
                // the error and no partial conversation state was ever recorded.
                //
                // This branch deliberately precedes the streaming check: an error response can
                // carry a text/event-stream content type (the Copilot API echoes the requested
                // stream mode on some 429/5xx replies), and committing on it would leave phantom
                // base input / turn history behind.
                if (isStreaming)
                {
                    // Never read a failed event stream to completion: a real SSE error body can stay
                    // open indefinitely, which would block here until the timeout fires and prevent
                    // the resilience handler above from ever observing the failure status and
                    // retrying. Log the status only and hand the response straight back.
                    LogResponsesExchange(seq, "error",
                        $"HTTP {(int)response.StatusCode}: <streaming error body not read>");
                }
                else if (response.Content is not null)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    LogResponsesExchange(seq, "error", $"HTTP {(int)response.StatusCode}: {errBody}");
                }
                else
                {
                    LogResponsesExchange(seq, "error", $"HTTP {(int)response.StatusCode}: <no content>");
                }

                return response;
            }

            // Successful streaming response: the SSE stream must be consumed directly by the
            // OpenAI SDK's streaming parser, so nothing may be read here. The conversation state
            // is committed by the SSE parser instead, from inside the transport tee, the moment
            // the stream's own response id becomes observable.
            if (isStreaming)
            {
                // TRANSPORT TEE: the content is wrapped so that every byte the SDK's streaming
                // parser reads is also observed here, WITHOUT changing what the SDK sees. The
                // wrapper acquires the underlying stream lazily — on the first ACTUAL read, not
                // when the stream handle is requested — so the response still leaves this handler
                // completely unconsumed and unacquired.
                //
                // ONE PARSER PER RESPONSE: the parser owns this exchange's staged state and
                // commits it under the real id from the first response.created event, then amends
                // the entry with the streamed output items on response.completed. No id → no
                // entry, ever: the staged state is simply abandoned.
                //
                // BOTH SEAMS ARE WIRED. Chunks feed the incremental parse; the tee's END-OF-INPUT
                // signal completes it, so a final line the server left unterminated is processed
                // exactly once, before the reader is told the body is over. Disposal is
                // deliberately NOT wired: a response abandoned mid-stream never reached its end,
                // and completing its parse would act on a line the server had not finished.
                if (response.Content is not null)
                {
                    var parser = new StreamingResponseParser(this, pending);
                    response.Content = new TeeStreamContent(
                        response.Content, parser.Append, parser.CompleteInput);
                }

                return response;
            }

            // Successful non-streaming response: read and parse the authoritative body BEFORE
            // committing. A 2xx carrying a missing, truncated or malformed body means the operation
            // did not actually complete, and committing first would poison every later
            // reconstruction with state from a request whose result the caller never received.
            if (response.Content is null)
                throw new InvalidOperationException(
                    $"Copilot responses endpoint returned HTTP {(int)response.StatusCode} with no content.");

            var respBody = await response.Content.ReadAsStringAsync(ct);

            JsonNode? respJson;
            try
            {
                respJson = JsonNode.Parse(respBody);
            }
            catch (JsonException ex)
            {
                LogResponsesExchange(seq, "error",
                    $"HTTP {(int)response.StatusCode}: unparseable response body: {respBody}");
                throw new InvalidOperationException(
                    "Copilot responses endpoint returned a successful status with a malformed JSON body.", ex);
            }

            // The body parsed syntactically, but that is not enough to call the exchange complete:
            // the response output must also be structurally sound. Materialize every output item
            // into a fully-detached clone FIRST, so any structural failure (e.g. a null element in
            // the output array) throws before a single piece of durable state has been touched.
            // Only once everything is validated is the state committed, atomically.
            List<JsonNode>? responseOutput = null;
            if (respJson?["output"] is JsonArray outputArray)
            {
                responseOutput = new List<JsonNode>(outputArray.Count);
                for (var i = 0; i < outputArray.Count; i++)
                {
                    var item = outputArray[i];
                    if (item is null)
                    {
                        LogResponsesExchange(seq, "error",
                            $"HTTP {(int)response.StatusCode}: output[{i}] is null: {respBody}");
                        throw new InvalidOperationException(
                            $"Copilot responses endpoint returned a successful status with a structurally invalid " +
                            $"response: output[{i}] is null.");
                    }

                    responseOutput.Add(item.DeepClone());
                }
            }

            // Response-content replacement comes next, still before any durable state is touched.
            // If building the replacement fails, the exchange has not been fully processed, so the
            // caller must see the error with the conversation state left exactly as it was.
            response.Content = ResponseContentFactory(respBody);

            LogResponsesExchange(seq, "response", respBody);

            // COMMIT — the final step. Everything fallible is done: the body is read, parsed,
            // structurally validated, materialized into detached clones, and the response content is
            // replaced. Only now is durable state mutated. The entry is composed request-side first
            // so turn history stays in request→response order, then the already-materialized
            // response output. Neither can fail part-way, and nothing after this point can throw.
            //
            // A stream:true request whose response is NOT text/event-stream lands here too: the
            // response's content-type signal governs the commit, so it takes this normal path,
            // empty-base normalization and output append included.
            CommitConversationState(pending, ReadIdString((respJson as JsonObject)?["id"]), responseOutput);

            return response;
        }

        /// <summary>
        /// Builds the replacement content for a successful non-streaming response.
        /// </summary>
        /// <remarks>
        /// Overridable as a test seam so tests can observe the exact moment of response-content
        /// replacement, and force it to fail, in order to prove that the conversation-state commit
        /// happens strictly afterwards. It is an instance member so each handler — and therefore
        /// each test — is independent, with no shared static state to reset.
        /// </remarks>
        internal Func<string, HttpContent> ResponseContentFactory { get; set; } =
            static body => new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        /// <summary>
        /// Promotes state staged for this request into the durable per-conversation store, under
        /// the response's own id. Called only once an authoritative non-streaming response has been
        /// received, and at most once per staged state.
        /// </summary>
        /// <param name="pending">The state staged during the request transformation.</param>
        /// <param name="responseId">
        /// The response's top-level <c>id</c> when it is a non-empty, non-whitespace JSON string;
        /// otherwise <see langword="null"/>, in which case NO entry is written and any follow-up
        /// naming that response degrades to a first-request transformation.
        /// </param>
        /// <param name="responseOutput">
        /// The already-materialized, fully detached clones of the response's <c>output</c> items,
        /// appended after the staged turn history so ordering stays request→response.
        /// </param>
        private void CommitConversationState(
            PendingConversationState? pending, string? responseId, List<JsonNode>? responseOutput)
        {
            if (pending is null || pending.Committed) return;
            pending.Committed = true;

            if (responseId is null) return;

            // A request that staged nothing (non-array input) still produced an authoritative
            // response, so the conversation exists and its output has to be recorded — with an
            // empty base, since there was no input array to carry forward.
            var baseInput = pending.BaseInput ?? new JsonArray();
            var turnHistory = pending.TurnHistory ?? new List<JsonNode>();

            if (responseOutput is not null)
                turnHistory.AddRange(responseOutput);

            StoreConversationState(responseId, baseInput, turnHistory);
        }

        /// <summary>
        /// Writes one conversation entry, bounding the store to <see cref="MaxEntries"/> by
        /// evicting the oldest key. A key that is already present is updated in place, without
        /// adding a second insertion-order entry.
        /// </summary>
        /// <returns>
        /// The <see cref="ConversationState.Generation"/> stamp of the entry just written, so a
        /// later amendment can prove it is amending THAT entry and not a replacement.
        /// </returns>
        private long StoreConversationState(string key, JsonArray baseInput, List<JsonNode> turnHistory)
        {
            lock (_storeLock)
            {
                if (!_store.ContainsKey(key))
                {
                    // Bounded FIFO: make room before adding a genuinely new key.
                    while (_insertionOrder.Count >= MaxEntries)
                    {
                        var evicted = _insertionOrder.Dequeue();
                        _store.Remove(evicted);
                    }

                    _insertionOrder.Enqueue(key);
                }

                var generation = ++_generationCounter;

                _store[key] = new ConversationState
                {
                    BaseInput = baseInput,
                    TurnHistory = turnHistory,
                    Generation = generation,
                };

                return generation;
            }
        }

        /// <summary>
        /// Appends streamed output items to the entry under <paramref name="key"/>, as ONE batch,
        /// but only when that entry is still the exact one identified by
        /// <paramref name="generation"/>.
        /// </summary>
        /// <remarks>
        /// A generation mismatch means the entry was replaced (re-committed, or evicted and
        /// re-inserted) between the streaming commit and this amendment, so the buffered items
        /// belong to a state that no longer exists: the amendment is SILENTLY DROPPED rather than
        /// grafted onto an unrelated conversation. The amended entry keeps its generation — this
        /// is a completion of the same write, not a new one — and the insertion order is untouched.
        /// </remarks>
        private void AmendConversationState(string key, long generation, List<JsonNode> outputItems)
        {
            if (outputItems.Count == 0) return;

            lock (_storeLock)
            {
                if (!_store.TryGetValue(key, out var state)) return;
                if (state.Generation != generation) return;

                var turnHistory = new List<JsonNode>(state.TurnHistory.Count + outputItems.Count);
                turnHistory.AddRange(state.TurnHistory);
                turnHistory.AddRange(outputItems);

                _store[key] = new ConversationState
                {
                    BaseInput = state.BaseInput,
                    TurnHistory = turnHistory,
                    Generation = generation,
                };

                // Test seam: reports the write that just happened, from INSIDE the lock, so a
                // test can prove an amendment is ONE batch rather than a per-item drip. No
                // observer (the default) means no behaviour of any kind.
                OnAmendmentForTest?.Invoke(key, turnHistory.Count);
            }
        }

        /// <summary>
        /// Test seam: invoked once per APPLIED amendment, inside the store lock, with the entry's
        /// key and the resulting turn-history count.
        /// </summary>
        /// <remarks>
        /// It exists so a test can count amendment OPERATIONS rather than merely inspect the final
        /// state: an implementation that amended item-by-item under separate lock acquisitions
        /// produces one invocation per item (and a growing count), while the one-batch contract
        /// produces exactly one invocation carrying the full count. Because it fires inside the
        /// lock, every intermediate state an implementation could publish is observed — a batch
        /// that is never partially visible is one that never reports a partial count.
        /// <see langword="null"/> by default, so it is invisible to all production behaviour.
        /// </remarks>
        internal Action<string, int>? OnAmendmentForTest { get; set; }

        /// <summary>
        /// Resolves the conversation state recorded under <paramref name="key"/>, as a private deep
        /// clone so branching follow-ups can never observe each other's additions.
        /// </summary>
        /// <returns>
        /// The cloned state, or <see langword="null"/> when the key is absent, empty or whitespace,
        /// or simply not present in the store (never recorded, or already evicted) — the degraded
        /// case, which falls back to a first-request transformation.
        /// </returns>
        private ConversationState? TryResolveConversationState(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            lock (_storeLock)
            {
                if (!_store.TryGetValue(key!, out var state)) return null;

                var baseInput = (JsonArray)state.BaseInput.DeepClone();
                var turnHistory = new List<JsonNode>(state.TurnHistory.Count);
                foreach (var item in state.TurnHistory)
                    turnHistory.Add(item.DeepClone());

                return new ConversationState { BaseInput = baseInput, TurnHistory = turnHistory };
            }
        }

        /// <summary>
        /// THE UNIFORM ID RULE: an id is usable only when it is a non-empty, non-whitespace JSON
        /// <b>string</b>. <see langword="null"/>, numbers, objects and arrays are all rejected —
        /// never coerced, and never a throw.
        /// </summary>
        private static string? ReadIdString(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (!value.TryGetValue<string>(out var text)) return null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// Test accessor: resolves the conversation state recorded under <paramref name="responseId"/>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> with deep clones of the entry on a hit; on a miss (a null, empty,
        /// whitespace, never-recorded or evicted id) <see langword="false"/> with a
        /// <see langword="null"/> <paramref name="baseInput"/> and an EMPTY
        /// <paramref name="turnHistory"/>. Never throws.
        /// </returns>
        internal bool TryGetConversationStateForTest(
            string? responseId, out JsonArray? baseInput, out IReadOnlyList<JsonNode> turnHistory)
        {
            var state = TryResolveConversationState(responseId);
            if (state is null)
            {
                baseInput = null;
                turnHistory = Array.Empty<JsonNode>();
                return false;
            }

            baseInput = state.BaseInput;
            turnHistory = state.TurnHistory;
            return true;
        }

        /// <summary>Test accessor: the number of conversations currently retained.</summary>
        internal int StoreCountForTest
        {
            get { lock (_storeLock) { return _store.Count; } }
        }

        /// <summary>Test accessor: the retained keys in insertion (eviction) order.</summary>
        internal IReadOnlyList<string> InsertionOrderForTest
        {
            get { lock (_storeLock) { return _insertionOrder.ToArray(); } }
        }

        /// <summary>
        /// Test accessor: builds a parser wired to this handler over freshly staged state, exactly
        /// as the streaming path does, so the SSE algorithm can be driven byte-by-byte without an
        /// HTTP exchange.
        /// </summary>
        /// <param name="stagedBase">
        /// The staged base input, or <see langword="null"/> together with
        /// <paramref name="stagedHistory"/> to model a request that staged NOTHING (non-array input).
        /// </param>
        /// <param name="stagedHistory">The staged turn history; see <paramref name="stagedBase"/>.</param>
        internal StreamingResponseParser CreateStreamingParserForTest(
            JsonArray? stagedBase, List<JsonNode>? stagedHistory)
            => new(this, new PendingConversationState
            {
                BaseInput = stagedBase,
                TurnHistory = stagedHistory,
            });

        /// <summary>
        /// Incremental SSE parser for ONE successful streaming response, fed the response's bytes
        /// through <see cref="TeeStreamContent.OnChunk"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WHAT IT DOES. It lifts the conversation's real response id out of the FIRST
        /// <c>event: response.created</c> block (<c>$.response.id</c>, under the same uniform id
        /// rule as the non-streaming path) and commits the request's staged state under it, then
        /// buffers every <c>response.output_item.done</c> item (<c>$.item</c>) and amends the
        /// committed entry with them, as one batch, on <c>response.completed</c>. The result is an
        /// entry composed exactly like a non-streaming one: staged base + staged history + the
        /// response's output items.
        /// </para>
        /// <para>
        /// PASSTHROUGH IS SACRED, AND SO IS NOT THROWING. This is a pure side channel: it never
        /// touches the bytes and never throws. The tee's observer containment is the backstop, not
        /// the excuse — every entry point absorbs its own failures and simply goes inert.
        /// </para>
        /// <para>
        /// PARSING. Chunk boundaries are irrelevant: lines are assembled from RAW BYTES, so a
        /// UTF-8 multi-byte sequence split across two chunks is reassembled before it is ever
        /// decoded (a newline byte can never occur inside a multi-byte sequence, which is what
        /// makes byte-level line splitting safe). LF and CRLF both terminate a line; multiple
        /// <c>data:</c> lines in one block are concatenated with <c>\n</c> per the SSE spec;
        /// <c>event:</c> and <c>data:</c> may appear in any order; comment lines (<c>:…</c>) and
        /// unknown fields are ignored; a blank line dispatches the block; malformed events are
        /// skipped.
        /// </para>
        /// <para>
        /// END OF INPUT. The chunk seam delivers only non-empty reads, so the stream's END is
        /// signalled through a second seam — <see cref="TeeStreamContent.OnEof"/>, raised on the
        /// first zero-length read — which drives <see cref="CompleteInput"/>. That is what makes a
        /// FINAL UNTERMINATED line (and the block it completes) processable at all: a zero-length
        /// read is the transport's own statement that every byte the server sent has arrived, so
        /// the line can no longer grow.
        /// </para>
        /// <para>
        /// EOF, NOT DISPOSAL, COMPLETES THE PARSE. Disposal carries no such statement: a response
        /// abandoned mid-stream (cancellation, an early break out of the SDK's enumeration, a
        /// transport failure) is disposed with the body still in flight, and completing the parse
        /// there would act on a line the server had not finished writing. There is therefore no
        /// disposal hook, and the disposal semantics fall out for free: because the commit happens
        /// BEFORE the chunk carrying <c>response.created</c> is returned to the reader, a stream
        /// disposed or abandoned before that point committed nothing (the staged state is simply
        /// abandoned), and one disposed after it keeps the entry that was already written. A
        /// stream that ends without <c>response.completed</c> keeps its committed entry but DROPS
        /// the buffered output items — degraded context, never incorrect context.
        /// </para>
        /// <para>
        /// BOUNDED MEMORY. Three limits keep an adversarial or runaway stream from accumulating:
        /// a single line over <see cref="MaxLineBytes"/> is discarded at the boundary (the next
        /// terminator resets the line), the id search gives up after
        /// <see cref="IdSearchByteCap"/> retained event-block bytes, and buffered output payloads
        /// stop at <see cref="OutputByteCap"/>. Every limit is INCLUSIVE and applies per complete
        /// event: a block that fits at-or-below its cap is accepted whole, one that straddles the
        /// boundary is dropped whole. Breaching a cap only ever stops RETENTION — the passthrough
        /// is untouched.
        /// </para>
        /// </remarks>
        internal sealed class StreamingResponseParser
        {
            /// <summary>
            /// Maximum retained length, in bytes and excluding the line terminator, of a single
            /// SSE line. A longer line is discarded at the boundary rather than buffered.
            /// </summary>
            internal const int MaxLineBytes = 8 * 1024;

            /// <summary>
            /// Inclusive cap on the event-block bytes RETAINED while searching for the response
            /// id. Exceeding it abandons the id search (and with it this response's entry).
            /// </summary>
            internal const int IdSearchByteCap = 64 * 1024;

            /// <summary>
            /// Inclusive cap on the raw <c>data:</c> payload bytes of buffered
            /// <c>response.output_item.done</c> events.
            /// </summary>
            internal const int OutputByteCap = 2 * 1024 * 1024;

            private const string EventResponseCreated = "response.created";
            private const string EventOutputItemDone = "response.output_item.done";
            private const string EventResponseCompleted = "response.completed";

            private readonly CopilotResponsesHandler _owner;
            private readonly PendingConversationState? _pending;

            /// <summary>
            /// Serializes the parser's state. The SDK consumes a response stream one read at a
            /// time, but nothing guarantees those reads happen on the same thread.
            /// </summary>
            private readonly object _gate = new();

            // ── Line assembly ────────────────────────────────────────────────
            private byte[] _line = new byte[256];
            private int _lineLength;

            /// <summary>
            /// A carriage return has arrived and is being held back until the next byte reveals
            /// whether it was the first half of a CRLF terminator (dropped) or ordinary content
            /// (retained then, and only then charged against the line's content limit).
            /// </summary>
            private bool _pendingCr;

            /// <summary>
            /// Set once the current line has passed <see cref="MaxLineBytes"/>: its bytes are
            /// dropped from here on, and the next terminator clears it.
            /// </summary>
            private bool _lineDiscarded;

            // ── Current event block ──────────────────────────────────────────
            private string? _eventName;
            private readonly StringBuilder _blockData = new();
            private bool _blockHasData;
            private int _blockDataBytes;

            /// <summary>Set when this block's data was dropped whole by the output cap.</summary>
            private bool _blockDataDropped;

            private bool _blockHasFields;

            // ── Phase state ──────────────────────────────────────────────────
            private bool _idSearchActive = true;
            private int _idSearchBytes;
            private bool _idSearchCapBreached;
            private bool _createdSeen;

            private string? _committedId;
            private long _committedGeneration;

            private readonly List<JsonNode> _outputItems = new();
            private int _outputBytes;
            private bool _outputCapBreached;

            private bool _terminated;

            /// <summary>
            /// Set once this parser can no longer affect any state — it then retains nothing and
            /// does nothing but let the bytes flow past.
            /// </summary>
            private bool _inert;

            internal StreamingResponseParser(
                CopilotResponsesHandler owner, PendingConversationState? pending)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _pending = pending;
            }

            /// <summary>
            /// The chunk observer handed to <see cref="TeeStreamContent"/>: consumes one chunk of
            /// response bytes. Called synchronously BEFORE the chunk is returned to the reader, so
            /// a commit triggered by this chunk is durable before the SDK ever sees those bytes.
            /// </summary>
            internal void Append(ReadOnlyMemory<byte> chunk)
            {
                lock (_gate)
                {
                    if (_inert) return;

                    try
                    {
                        Consume(chunk.Span);
                    }
                    catch
                    {
                        // A side channel that fails must never disturb the response: give up on
                        // this stream's state entirely rather than propagate. Deliberately broad —
                        // "never throws" is this class's contract, not an aspiration.
                        GoInert();
                    }
                }
            }

            /// <summary>
            /// Signals end of input: processes a FINAL UNTERMINATED line, then dispatches the
            /// block it completes, because no further bytes can arrive to terminate it.
            /// </summary>
            /// <remarks>
            /// Wired in production to <see cref="TeeStreamContent.OnEof"/>, which raises it on the
            /// first zero-length read — the transport's own statement that the body is complete.
            /// It is deliberately NOT wired to disposal: a response abandoned mid-stream never
            /// reached its end, so its partial line must not be treated as finished. Idempotent
            /// either way: a second call has nothing left to process.
            /// </remarks>
            internal void CompleteInput()
            {
                lock (_gate)
                {
                    if (_inert) return;

                    try
                    {
                        // A held-back CR that nothing followed was never a terminator's first
                        // half, so it is content on this final line — retain it before closing.
                        if (_pendingCr)
                        {
                            _pendingCr = false;
                            AppendLineByte((byte)'\r');
                        }

                        if (_lineLength > 0 || _lineDiscarded) EndLine();
                        if (!_inert && _blockHasFields) DispatchBlock();
                    }
                    catch
                    {
                        GoInert();
                    }
                }
            }

            /// <summary>Feeds one chunk through the line splitter.</summary>
            /// <remarks>
            /// THE TERMINATOR IS NEVER CONTENT. A carriage return is held back rather than
            /// retained: if the next byte is the line feed it completes, the CR was the first half
            /// of a CRLF terminator and is dropped; if anything else follows, the CR was ordinary
            /// content after all and is retained then — and only then does it count against the
            /// line's content limit. Deferring it is what keeps an exactly-<see cref="MaxLineBytes"/>
            /// line legal under CRLF as well as LF: the limit measures FIELD CONTENT, and a
            /// terminator byte must never be able to push a line over it.
            /// </remarks>
            private void Consume(ReadOnlySpan<byte> chunk)
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    var value = chunk[i];

                    if (value == (byte)'\n')
                    {
                        // The held-back CR (if any) was this terminator's first half: drop it.
                        _pendingCr = false;
                        EndLine();
                        if (_inert) return;
                        continue;
                    }

                    if (_pendingCr)
                    {
                        // No line feed followed, so the held-back CR was content. Retain it now,
                        // charged against the content limit like any other byte.
                        _pendingCr = false;
                        AppendLineByte((byte)'\r');
                    }

                    if (value == (byte)'\r')
                    {
                        // Possibly the first half of a CRLF terminator — decided by the next byte.
                        _pendingCr = true;
                        continue;
                    }

                    AppendLineByte(value);
                }
            }

            /// <summary>
            /// Retains one byte of the current line's CONTENT, enforcing the per-line bound. A
            /// line is discarded only once its content EXCEEDS <see cref="MaxLineBytes"/>: the
            /// byte that would make it longer than the bound discards the line, nothing more of it
            /// is retained, and the terminator that follows resets the buffer. Line terminators
            /// never reach here (see <see cref="Consume"/>), so they can never trip the bound.
            /// </summary>
            private void AppendLineByte(byte value)
            {
                if (_lineDiscarded) return;

                if (_lineLength >= MaxLineBytes)
                {
                    _lineDiscarded = true;
                    _lineLength = 0;
                    return;
                }

                if (_lineLength == _line.Length)
                    Array.Resize(ref _line, Math.Clamp(_line.Length * 2, 256, MaxLineBytes));

                _line[_lineLength++] = value;
            }

            /// <summary>
            /// Completes the current line at a terminator: the buffer holds CONTENT only — the
            /// terminator's bytes were never retained — and a discarded (over-long) line is
            /// skipped entirely, its terminator resetting the buffer for the next line.
            /// </summary>
            private void EndLine()
            {
                if (_lineDiscarded)
                {
                    _lineDiscarded = false;
                    _lineLength = 0;
                    return;
                }

                var length = _lineLength;
                _lineLength = 0;

                ProcessLine(_line.AsSpan(0, length));
            }

            /// <summary>Applies one complete SSE line to the current block.</summary>
            private void ProcessLine(ReadOnlySpan<byte> line)
            {
                if (line.Length == 0)
                {
                    // The blank line is the block boundary. It is never counted against any cap.
                    DispatchBlock();
                    return;
                }

                // Comments are ignored and retained not at all, so they cost nothing.
                if (line[0] == (byte)':') return;

                var colon = line.IndexOf((byte)':');
                var field = colon < 0 ? line : line.Slice(0, colon);
                var value = colon < 0 ? ReadOnlySpan<byte>.Empty : line.Slice(colon + 1);

                // Per the SSE spec exactly one leading space of the value is removed.
                if (value.Length > 0 && value[0] == (byte)' ') value = value.Slice(1);

                var isEvent = FieldIs(field, "event");
                var isData = !isEvent && FieldIs(field, "data");

                // Only RETAINED lines are accounted for; unknown fields are dropped like comments.
                if (!isEvent && !isData) return;

                if (!ChargeIdSearch(line.Length)) return;

                _blockHasFields = true;

                if (isEvent)
                {
                    _eventName = Encoding.UTF8.GetString(value);
                    return;
                }

                AppendBlockData(value);
            }

            /// <summary>
            /// Charges a retained line against the id-search accumulator while the search is still
            /// running. Breaching the cap ends the search — and with it this response's entry,
            /// since without an id there is nothing to write.
            /// </summary>
            /// <returns>
            /// <see langword="false"/> when the parser went inert and the caller must stop.
            /// </returns>
            private bool ChargeIdSearch(int lineBytes)
            {
                if (!_idSearchActive) return true;

                _idSearchBytes += lineBytes;
                if (_idSearchBytes <= IdSearchByteCap) return true;

                // NO FALLBACK KEY: the id can no longer be found, so nothing is ever written for
                // this response and everything retained so far is released.
                _idSearchActive = false;
                _idSearchCapBreached = true;
                GoInert();
                return false;
            }

            /// <summary>
            /// Buffers one <c>data:</c> payload for the current block, charging its RAW bytes
            /// against the output cap. An item that would straddle the cap is dropped WHOLE — its
            /// payload is released immediately rather than half-retained — and once the cap has
            /// been breached no block's data is retained at all.
            /// </summary>
            /// <remarks>
            /// The cap is enforced while the payload arrives, before the block's event name is
            /// necessarily known (SSE fields may come in any order), so it provisionally bounds
            /// EVERY block's data. Only the payloads of accepted
            /// <c>response.output_item.done</c> items are charged permanently.
            /// </remarks>
            private void AppendBlockData(ReadOnlySpan<byte> value)
            {
                if (_blockDataDropped) return;

                if (_outputCapBreached
                    || (long)_outputBytes + _blockDataBytes + value.Length > OutputByteCap)
                {
                    _blockDataDropped = true;
                    _blockData.Clear();
                    _blockDataBytes = 0;
                    return;
                }

                // Multi-line data payloads are concatenated with a newline; the synthesized
                // separator is not part of the raw payload and is not charged.
                if (_blockHasData) _blockData.Append('\n');
                _blockData.Append(Encoding.UTF8.GetString(value));
                _blockHasData = true;
                _blockDataBytes += value.Length;
            }

            /// <summary>Acts on a complete event block, then resets the block state.</summary>
            private void DispatchBlock()
            {
                var eventName = _eventName;
                var hadFields = _blockHasFields;
                var data = _blockHasData ? _blockData.ToString() : null;
                var dataBytes = _blockDataBytes;
                var dataDropped = _blockDataDropped;

                ResetBlock();

                if (!hadFields) return;

                // response.completed is a TERMINAL MARKER: everything after it is ignored.
                if (_terminated) return;

                switch (eventName)
                {
                    case EventResponseCreated:
                        HandleResponseCreated(data);
                        break;

                    case EventOutputItemDone:
                        HandleOutputItemDone(data, dataBytes, dataDropped);
                        break;

                    case EventResponseCompleted:
                        // NEVER parsed for items — the sole authoritative output source is
                        // response.output_item.done.
                        _terminated = true;
                        FlushOutputItems();
                        GoInert();
                        break;
                }
            }

            /// <summary>
            /// Handles the FIRST <c>response.created</c> block, which permanently decides this
            /// response's fate: a valid <c>$.response.id</c> commits the staged state under it,
            /// while malformed JSON or an unusable id abandons the state for good. No later
            /// <c>response.created</c> is ever considered.
            /// </summary>
            private void HandleResponseCreated(string? data)
            {
                if (_createdSeen) return;
                _createdSeen = true;

                // Whatever the outcome, the id search is over: its accumulator is released.
                _idSearchActive = false;

                var id = ReadIdString(SelectProperty(SelectProperty(TryParse(data), "response"), "id"));
                if (id is null)
                {
                    // NO FALLBACK KEY: no entry is written under any key, ever, so the next
                    // follow-up naming this response degrades exactly like an unknown parent.
                    GoInert();
                    return;
                }

                Commit(id);
            }

            /// <summary>
            /// Buffers one streamed output item, in arrival order, as a fully detached clone.
            /// </summary>
            private void HandleOutputItemDone(string? data, int dataBytes, bool dataDropped)
            {
                if (dataDropped)
                {
                    // The item straddled the output cap: it is dropped whole and nothing further
                    // is buffered for this stream.
                    _outputCapBreached = true;
                    return;
                }

                if (_outputCapBreached) return;

                var item = SelectProperty(TryParse(data), "item");
                if (item is null) return;

                _outputItems.Add(item.DeepClone());
                _outputBytes += dataBytes;
            }

            /// <summary>
            /// Promotes the staged state under the stream's real response id, remembering the
            /// generation of the entry written so the later amendment can prove it is amending
            /// that exact entry.
            /// </summary>
            private void Commit(string responseId)
            {
                var pending = _pending;

                // Nothing staged (non-array input) or already committed elsewhere: this response
                // gets NO entry at all, and the staged state is abandoned.
                if (pending is null || pending.Committed || !pending.HasStagedState)
                {
                    if (pending is not null) pending.Committed = true;
                    GoInert();
                    return;
                }

                pending.Committed = true;

                _committedGeneration = _owner.StoreConversationState(
                    responseId, pending.BaseInput!, pending.TurnHistory!);
                _committedId = responseId;
            }

            /// <summary>
            /// Amends the committed entry with every buffered output item, as ONE batch. Dropped
            /// silently when nothing was committed, or when the entry has since been replaced.
            /// </summary>
            private void FlushOutputItems()
            {
                if (_committedId is null || _outputItems.Count == 0) return;

                _owner.AmendConversationState(_committedId, _committedGeneration, _outputItems);
            }

            /// <summary>Parses a data payload, treating a malformed one as absent.</summary>
            private static JsonNode? TryParse(string? data)
            {
                if (string.IsNullOrWhiteSpace(data)) return null;

                try
                {
                    return JsonNode.Parse(data!);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            /// <summary>
            /// Reads a property off a node when — and only when — that node is a JSON object.
            /// Indexing a non-object <see cref="JsonNode"/> throws, and this parser must not.
            /// </summary>
            private static JsonNode? SelectProperty(JsonNode? node, string name)
                => node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

            private void ResetBlock()
            {
                _eventName = null;
                _blockData.Clear();
                _blockHasData = false;
                _blockDataBytes = 0;
                _blockDataDropped = false;
                _blockHasFields = false;
            }

            /// <summary>
            /// Releases everything and stops retaining: the parser can no longer change any state,
            /// so from here on it only lets the bytes flow past.
            /// </summary>
            private void GoInert()
            {
                _inert = true;
                _idSearchActive = false;
                _lineLength = 0;
                _lineDiscarded = false;
                _pendingCr = false;
                _line = Array.Empty<byte>();
                ResetBlock();
                _outputItems.Clear();
            }

            // ── Test seams ───────────────────────────────────────────────────

            /// <summary>Test accessor: the id the staged state was committed under, if any.</summary>
            internal string? CommittedIdForTest
            {
                get { lock (_gate) { return _committedId; } }
            }

            /// <summary>Test accessor: the generation captured at commit time (0 when uncommitted).</summary>
            internal long CommittedGenerationForTest
            {
                get { lock (_gate) { return _committedGeneration; } }
            }

            /// <summary>Test accessor: how many output items are buffered, awaiting the flush.</summary>
            internal int BufferedOutputCountForTest
            {
                get { lock (_gate) { return _outputItems.Count; } }
            }

            /// <summary>Test accessor: retained bytes charged to the id search so far.</summary>
            internal int IdSearchBytesForTest
            {
                get { lock (_gate) { return _idSearchBytes; } }
            }

            /// <summary>Test accessor: raw payload bytes of the buffered output items.</summary>
            internal int OutputBytesForTest
            {
                get { lock (_gate) { return _outputBytes; } }
            }

            /// <summary>Test accessor: whether the id-search cap was breached.</summary>
            internal bool IdSearchCapBreachedForTest
            {
                get { lock (_gate) { return _idSearchCapBreached; } }
            }

            /// <summary>Test accessor: whether the output cap was breached.</summary>
            internal bool OutputCapBreachedForTest
            {
                get { lock (_gate) { return _outputCapBreached; } }
            }

            /// <summary>Test accessor: whether <c>response.completed</c> has been seen.</summary>
            internal bool TerminatedForTest
            {
                get { lock (_gate) { return _terminated; } }
            }

            /// <summary>Test accessor: whether the parser has stopped retaining anything.</summary>
            internal bool InertForTest
            {
                get { lock (_gate) { return _inert; } }
            }
        }

        /// <summary>Compares an SSE field name against an ASCII literal.</summary>
        private static bool FieldIs(ReadOnlySpan<byte> field, string name)
        {
            if (field.Length != name.Length) return false;

            for (var i = 0; i < name.Length; i++)
                if (field[i] != (byte)name[i])
                    return false;

            return true;
        }

        /// <summary>
        /// Writes one side of a responses-API exchange to the diagnostics directory.
        /// </summary>
        /// <remarks>
        /// Diagnostics are opt-in: when no directory is configured (see
        /// <see cref="SetDiagnosticsDirectory"/> and <c>SHARPCODER_DIAGNOSTICS_DIR</c>) this method
        /// performs no file-system access at all. When enabled, logging stays best-effort and any
        /// I/O failure is swallowed so it can never break a request.
        /// </remarks>
        private static void LogResponsesExchange(int seq, string phase, string content)
        {
            var root = ResolveDiagnosticsDirectory();
            if (root is null) return;

            try
            {
                var dir = Path.Combine(root, "responses-api");
                Directory.CreateDirectory(dir);
                var fileName = $"{seq:D4}_{phase}.json";
                File.WriteAllText(Path.Combine(dir, fileName), content);
            }
            catch { /* best-effort logging */ }
        }

        /// <summary>
        /// Transport-level tee for a successful <c>text/event-stream</c> response: wraps the
        /// original <see cref="HttpContent"/> and hands out a stream that passes every byte
        /// through verbatim while offering each chunk to an optional observer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// PASSTHROUGH IS SACRED. The SDK's streaming parser must see exactly the bytes the server
        /// sent, in exactly the chunks the transport produced — nothing is coalesced, split,
        /// buffered ahead or rewritten. The observer is a pure side channel.
        /// </para>
        /// <para>
        /// LAZY ACQUISITION. The underlying stream is obtained on the first ACTUAL READ of the tee
        /// stream — not in the constructor, and not when the stream handle is handed out by
        /// <c>ReadAsStream</c>/<c>ReadAsStreamAsync</c>/<c>SerializeToStreamAsync</c>. Wrapping the
        /// content inside the handler therefore leaves the response completely unconsumed AND
        /// unacquired when it is handed back — which is exactly what an eager wrap would destroy: a
        /// stream pulled, or drained, before the caller ever saw it.
        /// </para>
        /// <para>
        /// ALL content reads observe the tee: the SDK's <c>ReadAsStream</c>/<c>ReadAsStreamAsync</c>
        /// path goes through <see cref="CreateContentReadStream(CancellationToken)"/>, and
        /// buffering consumption (<c>CopyToAsync</c>, <c>ReadAsByteArrayAsync</c>,
        /// <c>ReadAsStringAsync</c>) is routed through the same tee stream by
        /// <see cref="SerializeToStreamAsync(Stream, System.Net.TransportContext?, CancellationToken)"/>.
        /// </para>
        /// <para>
        /// DISPOSAL. <see cref="HttpContent"/> exposes only the synchronous
        /// <see cref="IDisposable"/> contract — there is no <c>DisposeAsync</c> to override — so
        /// disposal flows linearly: disposing this content disposes the captured ORIGINAL content
        /// exactly once, and the original owns (and therefore disposes) the underlying stream.
        /// </para>
        /// </remarks>
        internal sealed class TeeStreamContent : HttpContent
        {
            /// <summary>The captured original content; the sole owner of the underlying stream.</summary>
            private readonly HttpContent _original;

            /// <summary>Guards lazy tee creation and the once-only disposal of the original.</summary>
            private readonly object _gate = new();

            /// <summary>The tee stream, created on the first read — never at construction.</summary>
            private TeeStream? _tee;

            private bool _disposed;

            /// <summary>
            /// Parser seam: invoked synchronously with each chunk BEFORE that chunk is returned to
            /// the reader. <see langword="null"/> (the default) means no observer at all — a pure
            /// passthrough.
            /// </summary>
            /// <remarks>
            /// The observer never throws out of the tee: an exception from it is caught and the
            /// observer is disabled, so a broken parser can never break the response stream.
            /// </remarks>
            internal Action<ReadOnlyMemory<byte>>? OnChunk { get; set; }

            /// <summary>
            /// End-of-input seam: invoked synchronously EXACTLY ONCE, BEFORE the first zero-length
            /// read is returned to the reader. <see langword="null"/> (the default) means no
            /// observer at all, leaving every byte-exact passthrough guarantee untouched.
            /// </summary>
            /// <remarks>
            /// <para>
            /// WHY EOF AND NOT DISPOSAL. A zero-length read is the transport's own statement that
            /// the response body is COMPLETE — every byte the server sent has been handed over — so
            /// it is the only point at which a final unterminated line may safely be treated as a
            /// finished line. Disposal says nothing of the sort: a caller that abandons a response
            /// mid-stream (cancellation, an early <c>break</c> out of the SDK's enumeration, a
            /// failure) disposes it with the body still in flight, and completing the parse there
            /// would act on a line the server had not finished writing. Disposal therefore has NO
            /// hook, deliberately: a stream abandoned before EOF simply never completes its parse.
            /// </para>
            /// <para>
            /// The signal fires at most once per content, however many times the reader reads at
            /// EOF, and — exactly like <see cref="OnChunk"/> — a throwing observer is caught and
            /// disabled rather than propagated: the passthrough must never break.
            /// </para>
            /// </remarks>
            internal Action? OnEof { get; set; }

            /// <summary>
            /// Once-only latch for <see cref="OnEof"/>: 0 until the signal has been raised, 1
            /// after. Flipped with <see cref="Interlocked"/> so repeated — including concurrent —
            /// reads at EOF still raise it exactly once.
            /// </summary>
            private int _eofSignalled;

            public TeeStreamContent(
                HttpContent original,
                Action<ReadOnlyMemory<byte>>? onChunk = null,
                Action? onEof = null)
            {
                _original = original ?? throw new ArgumentNullException(nameof(original));
                OnChunk = onChunk;
                OnEof = onEof;

                // Every original content header is carried over unvalidated, so the response the
                // caller sees is indistinguishable from the one the server sent (content type and
                // charset included — the SSE media type must survive this wrap).
                foreach (var header in original.Headers)
                    Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            /// <summary>The original content this instance wraps. Test/diagnostic accessor.</summary>
            internal HttpContent OriginalContent => _original;

            /// <summary>
            /// Offers a chunk to the observer, absorbing any failure. A throwing observer is
            /// disabled rather than propagated: the passthrough must never break.
            /// </summary>
            internal void NotifyChunk(ReadOnlyMemory<byte> chunk)
            {
                var observer = OnChunk;
                if (observer is null) return;

                try
                {
                    observer(chunk);
                }
                catch
                {
                    // The side channel failed; drop it and keep streaming.
                    OnChunk = null;
                }
            }

            /// <summary>
            /// Raises the end-of-input signal, at most once, absorbing any failure.
            /// </summary>
            /// <remarks>
            /// The latch is claimed BEFORE the observer runs, so an observer that throws — or one
            /// that re-enters through another read — still cannot produce a second signal. See
            /// <see cref="OnEof"/> for why this is driven by a zero-length read rather than by
            /// disposal.
            /// </remarks>
            internal void NotifyEof()
            {
                if (Interlocked.Exchange(ref _eofSignalled, 1) != 0) return;

                var observer = OnEof;
                if (observer is null) return;

                try
                {
                    observer();
                }
                catch
                {
                    // The side channel failed; drop it and keep streaming.
                    OnEof = null;
                }
            }

            /// <summary>
            /// Returns the tee stream, creating it on first use. Creating the tee performs NO
            /// access at all on the original content: the wrapped stream is pulled by the tee
            /// itself, on its first actual read.
            /// </summary>
            private TeeStream EnsureTee()
            {
                lock (_gate)
                {
                    return _tee ??= new TeeStream(this);
                }
            }

            /// <summary>
            /// Acquires the original content's stream synchronously. Called by the tee, from its
            /// first actual read, and never before.
            /// </summary>
            internal Stream AcquireSourceStream(CancellationToken ct) => _original.ReadAsStream(ct);

            /// <summary>
            /// Acquires the original content's stream asynchronously. Called by the tee, from its
            /// first actual read, and never before.
            /// </summary>
            internal Task<Stream> AcquireSourceStreamAsync(CancellationToken ct)
                => _original.ReadAsStreamAsync(ct);

            protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
                => EnsureTee();

            protected override Task<Stream> CreateContentReadStreamAsync()
                => CreateContentReadStreamAsync(CancellationToken.None);

            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
                => Task.FromResult<Stream>(EnsureTee());

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
                => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override Task SerializeToStreamAsync(
                Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
                => EnsureTee().CopyToAsync(stream, 81920, cancellationToken);

            protected override void SerializeToStream(
                Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
                => EnsureTee().CopyTo(stream, 81920);

            protected override bool TryComputeLength(out long length)
            {
                var contentLength = _original.Headers.ContentLength;
                if (contentLength.HasValue)
                {
                    length = contentLength.Value;
                    return true;
                }

                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    bool alreadyDisposed;
                    TeeStream? tee;
                    lock (_gate)
                    {
                        alreadyDisposed = _disposed;
                        _disposed = true;
                        tee = _tee;
                    }

                    if (!alreadyDisposed)
                    {
                        // The tee only finalizes its own state; the underlying stream belongs to
                        // the original content, which is disposed exactly once, right here.
                        tee?.Dispose();
                        _original.Dispose();
                    }
                }

                base.Dispose(disposing);
            }

            /// <summary>
            /// The tee itself: a read-only passthrough over the response's content stream that
            /// offers every chunk it returns to <see cref="TeeStreamContent.OnChunk"/> first, and
            /// raises <see cref="TeeStreamContent.OnEof"/> when the wrapped stream reports its end.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Byte-exact and chunk-lazy: every read overload forwards a single read to the
            /// wrapped stream, offers exactly what that read produced, and returns it verbatim.
            /// Nothing is read ahead, coalesced or split.
            /// </para>
            /// <para>
            /// END OF INPUT. All four read overloads treat a zero-length read as end of input and
            /// raise the owner's EOF signal BEFORE returning that 0 — the mirror of the chunk
            /// seam's "observe before the reader sees it" rule, so an observer that finalizes
            /// state has done so by the time the reader learns the body is over. The signal is
            /// raised at most once per content, however many times a reader reads at EOF.
            /// </para>
            /// <para>
            /// DISPOSAL IS NOT END OF INPUT. This stream does NOT dispose the wrapped stream — its
            /// ownership stays with the original <see cref="HttpContent"/>. Disposal only
            /// finalizes this wrapper's own state, is idempotent in both the synchronous and
            /// asynchronous form, and deliberately raises NO signal: a response abandoned
            /// mid-stream never reached its end, so nothing about it may be treated as complete.
            /// See <see cref="TeeStreamContent.OnEof"/>.
            /// </para>
            /// <para>
            /// ACQUISITION IS DEFERRED TO THE FIRST ACTUAL READ. Constructing this stream — and
            /// handing it out from <c>ReadAsStream</c>/<c>ReadAsStreamAsync</c> — touches the
            /// original content not at all. The wrapped stream is pulled exactly once, by
            /// whichever of the four read overloads runs first, and cached for every read after
            /// that; a sync-first caller and an async-first caller both work.
            /// </para>
            /// </remarks>
            internal sealed class TeeStream : Stream, IAsyncDisposable
            {
                private readonly TeeStreamContent _owner;

                /// <summary>
                /// THE SINGLE ACQUISITION AUTHORITY: the one and only attempt to pull the original
                /// content's stream, or <see langword="null"/> while no attempt is in flight.
                /// </summary>
                /// <remarks>
                /// <para>
                /// Published exactly once with <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>,
                /// BEFORE the pull begins, so every racing reader — synchronous and asynchronous
                /// alike — converges on the winner's single attempt instead of starting its own.
                /// A design that merely re-checked a field after awaiting would let two first
                /// readers both observe "not acquired yet" and pull the original twice, retaining
                /// one stream and leaking the other.
                /// </para>
                /// <para>
                /// This deliberately does NOT assume that concurrent
                /// <see cref="HttpContent.ReadAsStreamAsync(CancellationToken)"/> calls hand back
                /// the same instance: custom content is under no obligation to do so, which is
                /// precisely why the second pull has to be prevented rather than tolerated.
                /// </para>
                /// <para>
                /// FAILURE SEMANTICS — RETRY, NOT POISON. Everyone waiting on a failed attempt
                /// (including one cancelled through the acquiring caller's token) observes that
                /// failure, but the slot is cleared first, so a LATER read starts a fresh attempt.
                /// A single cancelled reader therefore cannot permanently break the response for
                /// everyone else, and a failed attempt can never leave a half-published stream
                /// behind: the slot is only ever completed with a stream that was actually
                /// acquired. Whether that fresh attempt then SUCCEEDS is the original content's
                /// business — <see cref="HttpContent"/> caches its own content-read task, so a
                /// content that faulted once may well fault the same way again. The guarantee
                /// here is that the tee keeps asking rather than latching a failure of its own.
                /// </para>
                /// </remarks>
                private Task<Stream>? _acquisition;

                private volatile bool _disposed;

                internal TeeStream(TeeStreamContent owner)
                {
                    _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                }

                /// <summary>
                /// The wrapped stream if it has already been acquired SUCCESSFULLY, else
                /// <see langword="null"/>.
                /// </summary>
                /// <remarks>
                /// Never acquires and never blocks — an acquisition still in flight reads as "not
                /// acquired", so the delegation members answer their documented pre-acquisition
                /// defaults rather than waiting on (or deadlocking behind) another reader's pull.
                /// </remarks>
                private Stream? AcquiredStream
                {
                    get
                    {
                        var acquisition = Volatile.Read(ref _acquisition);
                        return acquisition is { IsCompletedSuccessfully: true } ? acquisition.Result : null;
                    }
                }

                /// <summary>
                /// Publishes this caller as the acquisition authority, or returns the attempt that
                /// is already in flight (or already finished).
                /// </summary>
                /// <param name="attempt">
                /// The caller's own slot to complete when it wins; only meaningful when this
                /// method returns <see langword="null"/>.
                /// </param>
                /// <returns>
                /// <see langword="null"/> when the caller WON and must therefore perform the one
                /// pull, otherwise the winning attempt to converge on.
                /// </returns>
                private Task<Stream>? TryBecomeAcquisitionAuthority(out TaskCompletionSource<Stream> attempt)
                {
                    // Continuations run asynchronously so that completing the attempt can never
                    // inline a waiter's continuation onto the acquiring thread.
                    attempt = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return Interlocked.CompareExchange(ref _acquisition, attempt.Task, null);
                }

                /// <summary>Completes the winning attempt with the stream it pulled.</summary>
                private Stream PublishAcquired(TaskCompletionSource<Stream> attempt, Stream source)
                {
                    attempt.SetResult(source);
                    return source;
                }

                /// <summary>
                /// Retires a failed attempt: the slot is cleared FIRST — so the next read starts a
                /// clean attempt rather than re-observing the failure — and only then is the
                /// failure handed to whoever is waiting on it.
                /// </summary>
                private void FailAcquisition(TaskCompletionSource<Stream> attempt, Exception error)
                {
                    Interlocked.CompareExchange(ref _acquisition, null, attempt.Task);
                    attempt.SetException(error);

                    // The winner rethrows the original exception itself, so nothing necessarily
                    // awaits this task; reading Exception marks it observed and keeps a failed
                    // acquisition from surfacing as an unobserved task exception.
                    _ = attempt.Task.Exception;
                }

                /// <summary>
                /// Returns the wrapped stream, acquiring it synchronously on first use. The
                /// acquisition happens exactly once: a second caller — of any overload, sync or
                /// async — converges on the first caller's single pull.
                /// </summary>
                /// <remarks>
                /// When an asynchronous reader is already acquiring, this synchronous caller
                /// blocks on that one attempt rather than starting a competing pull of its own.
                /// </remarks>
                private Stream EnsureAcquired(CancellationToken ct)
                {
                    var inFlight = Volatile.Read(ref _acquisition);
                    if (inFlight is not null) return inFlight.GetAwaiter().GetResult();

                    var winner = TryBecomeAcquisitionAuthority(out var attempt);
                    if (winner is not null) return winner.GetAwaiter().GetResult();

                    try
                    {
                        return PublishAcquired(attempt, _owner.AcquireSourceStream(ct));
                    }
                    catch (Exception ex)
                    {
                        FailAcquisition(attempt, ex);
                        throw;
                    }
                }

                /// <summary>
                /// Returns the wrapped stream, acquiring it asynchronously on first use — under
                /// the same single-authority rule as <see cref="EnsureAcquired"/>, so competing
                /// first reads of any mix perform exactly one pull between them.
                /// </summary>
                private ValueTask<Stream> EnsureAcquiredAsync(CancellationToken ct)
                {
                    // The overwhelmingly common case — already acquired — stays allocation-free
                    // and completes synchronously.
                    var acquired = AcquiredStream;
                    return acquired is not null
                        ? new ValueTask<Stream>(acquired)
                        : new ValueTask<Stream>(AcquireAsync(ct));
                }

                private async Task<Stream> AcquireAsync(CancellationToken ct)
                {
                    var inFlight = Volatile.Read(ref _acquisition);
                    if (inFlight is not null) return await inFlight.ConfigureAwait(false);

                    var winner = TryBecomeAcquisitionAuthority(out var attempt);
                    if (winner is not null) return await winner.ConfigureAwait(false);

                    try
                    {
                        return PublishAcquired(
                            attempt, await _owner.AcquireSourceStreamAsync(ct).ConfigureAwait(false));
                    }
                    catch (Exception ex)
                    {
                        FailAcquisition(attempt, ex);
                        throw;
                    }
                }

                // A disposed tee reports no capability at all; an alive one mirrors the source,
                // except on the write side, which an SSE response stream never supports.
                //
                // PRE-ACQUISITION SEMANTICS: querying a capability must never pull the wrapped
                // stream — that would defeat the deferral this class exists to provide. Before the
                // first read the answers are therefore the deliberate, deterministic defaults
                // below: CanRead is TRUE (this is a readable stream; its data is simply not
                // fetched yet), and CanSeek is FALSE (an unacquired SSE stream is not seekable,
                // and claiming otherwise would invite a Seek that has nothing to seek on).
                public override bool CanRead => !_disposed && (AcquiredStream?.CanRead ?? true);
                public override bool CanSeek => !_disposed && (AcquiredStream?.CanSeek ?? false);
                public override bool CanWrite => false;

                /// <summary>
                /// The wrapped stream's length once acquired.
                /// </summary>
                /// <remarks>
                /// PRE-ACQUISITION: throws <see cref="NotSupportedException"/> rather than pulling
                /// the stream — the length of a response body nobody has started reading is not
                /// knowable here, and reporting a fabricated 0 would be worse than refusing.
                /// </remarks>
                public override long Length
                {
                    get
                    {
                        ThrowIfDisposed();
                        var inner = AcquiredStream
                            ?? throw new NotSupportedException(
                                "The SSE response tee stream has no length before its first read.");
                        return inner.Length;
                    }
                }

                /// <summary>
                /// The wrapped stream's position once acquired.
                /// </summary>
                /// <remarks>
                /// PRE-ACQUISITION: the getter reports 0 — nothing has been read, so nothing has
                /// been consumed — and the setter throws <see cref="NotSupportedException"/>,
                /// since there is no stream to position and acquiring one to satisfy a seek would
                /// break the deferral.
                /// </remarks>
                public override long Position
                {
                    get
                    {
                        ThrowIfDisposed();
                        return AcquiredStream?.Position ?? 0L;
                    }
                    set
                    {
                        ThrowIfDisposed();
                        var inner = AcquiredStream
                            ?? throw new NotSupportedException(
                                "The SSE response tee stream cannot be positioned before its first read.");
                        inner.Position = value;
                    }
                }

                /// <summary>
                /// Seeks the wrapped stream once acquired; before that, throws
                /// <see cref="NotSupportedException"/> — see <see cref="Position"/>.
                /// </summary>
                public override long Seek(long offset, SeekOrigin origin)
                {
                    ThrowIfDisposed();
                    var inner = AcquiredStream
                        ?? throw new NotSupportedException(
                            "The SSE response tee stream cannot seek before its first read.");
                    return inner.Seek(offset, origin);
                }

                public override void Flush()
                {
                    // Nothing is buffered here, and the write side is unsupported.
                }

                public override void SetLength(long value)
                    => throw new NotSupportedException("The SSE response tee stream is read-only.");

                public override void Write(byte[] buffer, int offset, int count)
                    => throw new NotSupportedException("The SSE response tee stream is read-only.");

                public override void Write(ReadOnlySpan<byte> buffer)
                    => throw new NotSupportedException("The SSE response tee stream is read-only.");

                public override Task WriteAsync(
                    byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                    => throw new NotSupportedException("The SSE response tee stream is read-only.");

                public override ValueTask WriteAsync(
                    ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
                    => throw new NotSupportedException("The SSE response tee stream is read-only.");

                public override int Read(byte[] buffer, int offset, int count)
                {
                    ThrowIfDisposed();
                    var inner = EnsureAcquired(CancellationToken.None);
                    var read = inner.Read(buffer, offset, count);
                    if (read > 0) _owner.NotifyChunk(new ReadOnlyMemory<byte>(buffer, offset, read));
                    else _owner.NotifyEof();
                    return read;
                }

                public override int Read(Span<byte> buffer)
                {
                    ThrowIfDisposed();
                    var inner = EnsureAcquired(CancellationToken.None);
                    var read = inner.Read(buffer);
                    if (read > 0) NotifySpan(buffer.Slice(0, read));
                    else _owner.NotifyEof();
                    return read;
                }

                public override async Task<int> ReadAsync(
                    byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                {
                    ThrowIfDisposed();
                    var inner = await EnsureAcquiredAsync(cancellationToken).ConfigureAwait(false);
                    var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                    if (read > 0) _owner.NotifyChunk(new ReadOnlyMemory<byte>(buffer, offset, read));
                    else _owner.NotifyEof();
                    return read;
                }

                public override async ValueTask<int> ReadAsync(
                    Memory<byte> buffer, CancellationToken cancellationToken = default)
                {
                    ThrowIfDisposed();
                    var inner = await EnsureAcquiredAsync(cancellationToken).ConfigureAwait(false);
                    var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read > 0) _owner.NotifyChunk(buffer.Slice(0, read));
                    else _owner.NotifyEof();
                    return read;
                }

                public override void CopyTo(Stream destination, int bufferSize)
                {
                    ThrowIfDisposed();
                    base.CopyTo(destination, bufferSize);
                }

                public override Task CopyToAsync(
                    Stream destination, int bufferSize, CancellationToken cancellationToken)
                {
                    ThrowIfDisposed();
                    return base.CopyToAsync(destination, bufferSize, cancellationToken);
                }

                /// <summary>
                /// Offers a span-shaped chunk to the observer. A span cannot be handed over as
                /// memory, so — and only when an observer is actually attached — the chunk is
                /// copied into a pooled buffer for the duration of the synchronous call.
                /// </summary>
                private void NotifySpan(ReadOnlySpan<byte> chunk)
                {
                    if (_owner.OnChunk is null) return;

                    var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(chunk.Length);
                    try
                    {
                        chunk.CopyTo(rented);
                        _owner.NotifyChunk(new ReadOnlyMemory<byte>(rented, 0, chunk.Length));
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                private void ThrowIfDisposed()
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(TeeStream));
                }

                protected override void Dispose(bool disposing)
                {
                    // Idempotent, and deliberately NOT a disposal of the wrapped stream: that one
                    // belongs to the original HttpContent.
                    _disposed = true;
                    base.Dispose(disposing);
                }

                public override ValueTask DisposeAsync()
                {
                    _disposed = true;
                    GC.SuppressFinalize(this);
                    return default;
                }
            }
        }
    }
}
