using SharpCoder;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>The human's answer to one tool confirmation prompt.</summary>
internal enum ToolConfirmationAnswer
{
    /// <summary>Deny this one call.</summary>
    No,

    /// <summary>Allow this one call.</summary>
    Yes,

    /// <summary>Allow this call and all later guarded calls in this process.</summary>
    Always,

    /// <summary>Deny this call and all later guarded calls in this process (fail closed).</summary>
    Never,
}

/// <summary>Abstraction over the interactive prompt so authorization policy is unit-testable.</summary>
internal interface IToolConfirmationPrompter
{
    ToolConfirmationAnswer Ask(ToolAuthorizationContext context, CancellationToken ct);
}

/// <summary>
/// Implements <c>AgentOptions.AuthorizeTool</c> for the CLI. Only the side-effecting tools are
/// prompted; read/search tools are allowed immediately. Decisions are returned to SharpCoder's
/// fail-closed authorization boundary, so a denial produces a model-visible refusal without the
/// tool running.
/// </summary>
/// <remarks>
/// The callback can be invoked concurrently (parent agent plus sub-agent tools), so all shared
/// state is protected by a lock and console output is serialized.
/// </remarks>
internal sealed class ToolAuthorizationPolicy
{
    /// <summary>Tools whose calls can change the workspace or the host and therefore need consent.</summary>
    internal static readonly HashSet<string> SensitiveTools = new(StringComparer.Ordinal)
    {
        "write_file",
        "edit_file",
        "execute_bash_command",
    };

    private readonly IToolConfirmationPrompter _prompter;
    private readonly ToolConfirmationMode _mode;
    private readonly object _gate = new();
    private bool _allowAll;
    private bool _denyAll;

    /// <summary>Creates a policy.</summary>
    /// <param name="prompter">The prompt UI (injectable for tests).</param>
    /// <param name="mode">Confirmation mode.</param>
    public ToolAuthorizationPolicy(IToolConfirmationPrompter prompter, ToolConfirmationMode mode)
    {
        _prompter = prompter;
        _mode = mode;
    }

    /// <summary>The delegate to assign to <c>AgentOptions.AuthorizeTool</c>.</summary>
    public Task<ToolAuthorizationDecision> AuthorizeAsync(ToolAuthorizationContext context, CancellationToken ct) =>
        Task.FromResult(Authorize(context.ToolName, context, ct));

    /// <summary>
    /// Synchronous decision core. Split out so CLI tests can exercise the policy without needing to
    /// construct SharpCoder's internally-created <see cref="ToolAuthorizationContext"/>.
    /// </summary>
    /// <param name="toolName">Model-visible tool name.</param>
    /// <param name="promptContext">Context handed to the prompter, or null for non-interactive tests.</param>
    /// <param name="ct">Cancellation token.</param>
    internal ToolAuthorizationDecision Authorize(string toolName, ToolAuthorizationContext? promptContext, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Reads never need consent — prompting for every read_file/glob/grep would make the REPL
        // unusable. Only the three side-effecting built-ins are guarded.
        if (!SensitiveTools.Contains(toolName))
            return ToolAuthorizationDecision.Allow;
        if (_mode == ToolConfirmationMode.Never)
            return ToolAuthorizationDecision.Allow;

        lock (_gate)
        {
            if (_allowAll)
                return ToolAuthorizationDecision.Allow;
            if (_denyAll)
                return ToolAuthorizationDecision.Deny(NoFurtherCallsReason);

            // Serialize both the prompt and the state transition: two concurrent "always" answers
            // would otherwise each draw a prompt.
            var answer = promptContext is null
                ? ToolConfirmationAnswer.No // fail closed when there is nothing to show/ask
                : _prompter.Ask(promptContext, ct);
            return Apply(answer);
        }
    }

    /// <summary>
    /// Test seam: resolves an answer directly, exercising the same locked state machine as
    /// <see cref="Authorize"/> without requiring a <see cref="ToolAuthorizationContext"/> (whose
    /// constructor is internal to the SharpCoder assembly).
    /// </summary>
    internal ToolAuthorizationDecision AuthorizeWithAnswer(string toolName, ToolConfirmationAnswer answer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!SensitiveTools.Contains(toolName)) return ToolAuthorizationDecision.Allow;
        if (_mode == ToolConfirmationMode.Never) return ToolAuthorizationDecision.Allow;
        lock (_gate)
        {
            if (_allowAll) return ToolAuthorizationDecision.Allow;
            if (_denyAll) return ToolAuthorizationDecision.Deny(NoFurtherCallsReason);
            return Apply(answer);
        }
    }

    private const string NoFurtherCallsReason = "The user declined all further tool calls in this session.";

    private ToolAuthorizationDecision Apply(ToolConfirmationAnswer answer)
    {
        switch (answer)
        {
            case ToolConfirmationAnswer.Yes:
                return ToolAuthorizationDecision.Allow;
            case ToolConfirmationAnswer.Always:
                _allowAll = true;
                return ToolAuthorizationDecision.Allow;
            case ToolConfirmationAnswer.Never:
                _denyAll = true;
                return ToolAuthorizationDecision.Deny(NoFurtherCallsReason);
            case ToolConfirmationAnswer.No:
            default:
                return ToolAuthorizationDecision.Deny("The user declined this tool call. Ask what to do instead.");
        }
    }
}

/// <summary>Spectre.Console-based prompt implementation used by the real CLI.</summary>
internal sealed class SpectreToolConfirmationPrompter : IToolConfirmationPrompter
{
    private readonly IAnsiConsole _console;
    private readonly Func<CancellationToken, char?> _readKey;

    public SpectreToolConfirmationPrompter(IAnsiConsole console)
        : this(console, ReadKey)
    {
    }

    /// <summary>Test seam: allows injecting the key read so tests never block on stdin.</summary>
    internal SpectreToolConfirmationPrompter(IAnsiConsole console, Func<CancellationToken, char?> readKey)
    {
        _console = console;
        _readKey = readKey;
    }

    public ToolConfirmationAnswer Ask(ToolAuthorizationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _console.WriteLine();
        _console.Write(new Rule
        {
            Title = $"[yellow]tool authorization: {Markup.Escape(context.ToolName)}[/]",
            Justification = Justify.Left,
        });
        foreach (var line in Describe(context))
            _console.MarkupLine(line);
        _console.WriteLine();

        // Read a single key so no Enter is needed. EOF / non-interactive input fails closed.
        _console.Markup("[bold]Allow?[/] [dim][[y]]es  [[n]]o  [[a]]lways  [[s]]top-all  (default: no)[/] ");
        var key = _readKey(ct);
        var answer = key switch
        {
            'y' or 'Y' => ToolConfirmationAnswer.Yes,
            'a' or 'A' => ToolConfirmationAnswer.Always,
            's' or 'S' => ToolConfirmationAnswer.Never,
            _ => ToolConfirmationAnswer.No,
        };
        _console.MarkupLine(answer switch
        {
            ToolConfirmationAnswer.Yes => "[green]allowed[/]",
            ToolConfirmationAnswer.Always => "[green]allowed (always for this session)[/]",
            ToolConfirmationAnswer.Never => "[red]denied (all further calls)[/]",
            _ => "[red]denied[/]",
        });
        _console.WriteLine();
        return answer;
    }

    /// <summary>Returns already-escaped markup lines describing the tool arguments.</summary>
    private static IReadOnlyList<string> Describe(ToolAuthorizationContext context)
    {
        var lines = new List<string>();
        foreach (var (name, value) in context.Arguments)
        {
            var text = value?.ToString() ?? "(null)";
            if (text.Length > 600) text = text[..599] + "…";
            lines.Add($"  [dim]{Markup.Escape(name)}:[/]");
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                lines.Add($"    {Markup.Escape(line)}");
        }
        if (lines.Count == 0)
            lines.Add("  [dim](no arguments)[/]");
        return lines;
    }

    private static char? ReadKey(CancellationToken ct)
    {
        try
        {
            if (Console.IsInputRedirected)
                return null; // fail closed: no human to answer
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            return key.KeyChar;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }
}
