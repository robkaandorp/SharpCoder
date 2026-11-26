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

    private IChatClient? _client;
    private CodingAgent? _agent;

    public AgentFactory(CliOptions options, ConsoleUi ui)
    {
        _options = options;
        _ui = ui;
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

        _client = ChatClientFactory.Create(_options.Model);

        var agentOptions = new AgentOptions
        {
            WorkDirectory = _options.WorkDirectory,
            MaxSteps = _options.MaxSteps,
            EnableBash = _options.EnableBash,
            ShowToolCallsInStream = true,
            ReasoningEffort = _options.ReasoningEffort,
            OnCompacting = _ui.Compacting,
            OnCompacted = _ui.Compacted,
        };

        _agent = new CodingAgent(_client, agentOptions);

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
            reasoning: _options.ReasoningEffort,
            savePath: _options.SavePath);

    public async ValueTask DisposeAsync()
    {
        if (_agent is not null)
            await _agent.DisposeAsync().ConfigureAwait(false);
        if (_client is IDisposable disposable)
            disposable.Dispose();
    }
}
