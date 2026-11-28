using System;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.Providers;

namespace SharpCoder.Cli;

/// <summary>
/// Builds the <see cref="CodingAgent"/> and <see cref="AgentSession"/> for a run, wiring the
/// provider client, tool configuration, and the UI callbacks that drive the console output.
/// </summary>
public sealed class AgentFactory : IAsyncDisposable
{
    private readonly CliOptions _options;
    private readonly ConsoleUi _ui;
    private readonly EnvironmentDigest _environmentDigest;
    private readonly Func<string?, IChatClient> _clientFactory;
    private readonly IToolConfirmationPrompter _confirmationPrompter;

    private IChatClient? _client;
    private CodingAgent? _agent;
    private AgentOptions? _agentOptions;

    public AgentFactory(CliOptions options, ConsoleUi ui)
        : this(options, ui, EnvironmentDigest.CreateDefault(), ChatClientFactory.Create)
    {
    }

    /// <summary>Test seam: allows injecting a deterministic digest builder and chat client factory.</summary>
    internal AgentFactory(
        CliOptions options,
        ConsoleUi ui,
        EnvironmentDigest environmentDigest,
        Func<string?, IChatClient>? clientFactory = null,
        IToolConfirmationPrompter? confirmationPrompter = null)
    {
        _options = options;
        _ui = ui;
        _environmentDigest = environmentDigest;
        _clientFactory = clientFactory ?? ChatClientFactory.Create;
        _confirmationPrompter = confirmationPrompter
            ?? new SpectreToolConfirmationPrompter(Spectre.Console.AnsiConsole.Console);
    }

    /// <summary>
    /// Creates the chat client and agent. Restores the session from <c>--session</c> when set,
    /// otherwise starts a fresh one; reports the restored session via the UI.
    /// </summary>
    public async Task<(CodingAgent Agent, AgentSession Session)> CreateAsync()
    {
        // Load the session first so a bad path fails before any provider credentials are touched.
        AgentSession session = _options.SessionPath is { Length: > 0 } path
            ? await AgentSession.LoadAsync(path).ConfigureAwait(false)
            : AgentSession.Create();

        _client = _clientFactory(_options.Model);

        // The digest is appended under CodingAgent's "Custom Instructions" heading. It is built
        // before construction, with a placeholder token for the shell; the token is substituted
        // from CodingAgent.ResolvedBashShell afterwards. BuildSystemPrompt reads these options live
        // on every turn, so the enriched value is what the model sees.
        var customInstructions = _options.NoEnvContext
            ? null
            : _environmentDigest.Build(_options, EnvironmentDigest.ShellPlaceholder);

        // In Auto mode a redirected stdin/stdout means no human can answer, so prompting would
        // hang a scripted run. --confirm-tools always overrides this by choosing Always directly.
        var effectiveMode = _options.ConfirmTools == ToolConfirmationMode.Auto
                            && (Console.IsInputRedirected || Console.IsOutputRedirected)
            ? ToolConfirmationMode.Never
            : _options.ConfirmTools;
        var authorization = new ToolAuthorizationPolicy(_confirmationPrompter, effectiveMode);

        var agentOptions = new AgentOptions
        {
            WorkDirectory = _options.WorkDirectory,
            MaxSteps = _options.MaxSteps,
            EnableBash = _options.EnableBash,
            EnableFileWrites = !_options.ReadOnly,
            BashShell = _options.Shell,
            ShowToolCallsInStream = true,
            EmitStructuredToolUpdates = true,
            ReasoningEffort = _options.ReasoningEffort,
            CustomInstructions = customInstructions,
            AuthorizeTool = authorization.AuthorizeAsync,
            ToolAuthorizationCorrelationId = "SharpCoder.Cli",
            OnCompacting = _ui.Compacting,
            OnCompacted = _ui.Compacted,
        };
        // The CLI defaults to a 1M-token window (the library keeps a conservative 100k default).
        agentOptions.MaxContextTokens = _options.ContextTokens ?? CliOptions.DefaultContextTokens;
        if (_options.RetainTokens is { } retainTokens)
            agentOptions.CompactionRetainTokens = retainTokens;
        if (_options.BashTimeoutMs is { } bashTimeoutMs)
            agentOptions.BashTimeoutMs = bashTimeoutMs;

        _agentOptions = agentOptions;
        _agent = new CodingAgent(_client, agentOptions);

        if (customInstructions is not null)
        {
            // Substitute the shell the agent actually resolved, so the model knows which command
            // syntax to emit for execute_bash_command. Falls back to the preference if bash is off.
            var resolved = _agent.ResolvedBashShell ?? _options.Shell.ToString();
            agentOptions.CustomInstructions = customInstructions.Replace(
                EnvironmentDigest.ShellPlaceholder, resolved, StringComparison.Ordinal);
        }

        if (_options.SessionPath is not null)
            _ui.SessionRestored(session);

        return (_agent, session);
    }

    /// <summary>Prints the resolved configuration table.</summary>
    public void ShowConfiguration() =>
        _ui.Configuration(
            model: _options.Model,
            workDirectory: _options.WorkDirectory,
            maxSteps: _options.MaxSteps,
            enableBash: _options.EnableBash,
            readOnly: _options.ReadOnly,
            noEnvContext: _options.NoEnvContext,
            confirmTools: _options.ConfirmTools,
            shell: _options.Shell,
            reasoning: _options.ReasoningEffort,
            contextTokens: _options.ContextTokens,
            savePath: _options.SavePath,
            resolvedShell: _agent?.ResolvedBashShell,
            compactionThreshold: _agentOptions?.CompactionThreshold,
            bashTimeoutMs: _options.BashTimeoutMs);

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null)
            await _agent.DisposeAsync().ConfigureAwait(false);
        if (_client is IDisposable disposable)
            disposable.Dispose();
    }
}
