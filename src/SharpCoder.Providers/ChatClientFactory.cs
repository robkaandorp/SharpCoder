#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;

using OllamaSharp;

using OpenAI;

using Polly;

using System.ClientModel;
using System.Net.Http.Headers;
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
    /// TEMPORARY, DECLARED PARTIAL FIX: streaming (SSE) responses are handed back unparsed, so
    /// their response id is not available here and their state can only be committed to a single
    /// shared <see cref="StreamingLegacySlotKey"/> slot, which every streaming request falls back
    /// to when its <c>previous_response_id</c> does not resolve. Concurrent streaming
    /// conversations therefore still share one slot, and evicting the slot degrades streaming
    /// follow-ups to first-request transformations. This is knowingly only a partial resolution of
    /// the isolation problem and stays in place until the follow-up work replaces the slot with
    /// per-conversation streaming state (SSE id extraction).
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
        /// The single slot every streaming conversation shares until SSE response ids can be
        /// extracted. See the class remarks: this is a declared temporary partial fix.
        /// </summary>
        internal const string StreamingLegacySlotKey = "streaming-legacy";

        private int _requestCount;

        /// <summary>
        /// Conversation state produced by the request transformation, staged on the request itself
        /// so it survives across resilience retry attempts and is committed by whichever attempt
        /// finally receives an authoritative response.
        /// </summary>
        /// <remarks>
        /// <see cref="BaseInput"/> and <see cref="TurnHistory"/> are always either both set or both
        /// <see langword="null"/>. Both <see langword="null"/> means NOTHING was staged (the
        /// request's <c>input</c> was not a JSON array), which is deliberately distinct from a
        /// staged-but-empty history: the streaming commit is a no-op for the former and writes the
        /// slot for the latter.
        /// </remarks>
        private sealed class PendingConversationState
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
                    // A request declares itself streaming through "stream": true. This governs
                    // TRANSFORMATION and STAGING only; the commit is governed by the response's
                    // content type. When the two disagree, the response's signal wins.
                    var isStreamingRequest = obj["stream"] is JsonValue streamValue
                        && streamValue.TryGetValue<bool>(out var streamFlag)
                        && streamFlag;

                    // STATE SELECTION.
                    // 1. A present, valid, found previous_response_id resolves that conversation.
                    // 2. A streaming request otherwise falls back to the shared legacy slot (see
                    //    the class remarks: declared temporary partial fix).
                    // 3. Neither resolves → degraded/fresh: first-request transformation.
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

                    if (parent is null && isStreamingRequest)
                        parent = TryResolveConversationState(StreamingLegacySlotKey);

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
            // OpenAI SDK's streaming parser, so this is the last point at which the exchange can be
            // treated as authoritative — and the response id is never observable here. Commit the
            // staged state to the shared legacy slot, before handing the untouched stream back.
            //
            // ONE COMMIT RULE: every successful SSE response writes the slot, with NO response
            // output appended (extracting it from the stream is follow-up work). A request that
            // staged nothing (non-array input) is a no-op, so the slot's previous entry keeps.
            if (isStreaming)
            {
                CommitStreamingState(pending);
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
        /// Promotes state staged for a streaming request into the single shared legacy slot. See
        /// the class remarks: a declared temporary partial fix, because an SSE response's id is not
        /// observable here.
        /// </summary>
        /// <remarks>
        /// No response output is appended — extracting it from the event stream is follow-up work.
        /// A request that staged nothing (non-array input) leaves the slot's previous entry alone.
        /// </remarks>
        private void CommitStreamingState(PendingConversationState? pending)
        {
            if (pending is null || pending.Committed) return;
            pending.Committed = true;

            if (!pending.HasStagedState) return;

            StoreConversationState(StreamingLegacySlotKey, pending.BaseInput!, pending.TurnHistory!);
        }

        /// <summary>
        /// Writes one conversation entry, bounding the store to <see cref="MaxEntries"/> by
        /// evicting the oldest key. A key that is already present is updated in place, without
        /// adding a second insertion-order entry.
        /// </summary>
        private void StoreConversationState(string key, JsonArray baseInput, List<JsonNode> turnHistory)
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

                _store[key] = new ConversationState
                {
                    BaseInput = baseInput,
                    TurnHistory = turnHistory,
                };
            }
        }

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
    }
}
