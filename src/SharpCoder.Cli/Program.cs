using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// Entry point for the SharpCoder.Cli command-line tool. Two modes:
/// <list type="bullet">
/// <item><c>run &lt;task&gt;</c> — execute one task, print the result, exit with a status code.</item>
/// <item><c>chat</c> (default) — an interactive session with slash commands (<c>/help</c>).</item>
/// </list>
/// </summary>
internal static class Program
{
    // CTS of the turn currently in flight, or null when idle. Ctrl+C cancels the active turn,
    // or exits the process when at the prompt.
    private static CancellationTokenSource? _activeTurn;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CliOptions options;
        try
        {
            var parsed = CliOptions.Parse(args);
            if (parsed is null)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }
            options = parsed;
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Try 'SharpCoder.Cli --help'.[/]");
            return 2;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"SharpCoder.Cli {Version}");
            return 0;
        }

        var ui = new ConsoleUi { MarkdownMode = options.Markdown };
        ui.ShowBanner(Version);

        await using var factory = new AgentFactory(options, ui);

        CodingAgent agent;
        AgentSession session;
        try
        {
            (agent, session) = await factory.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ui.Error(ex.Message);
            return 1;
        }

        factory.ShowConfiguration();

        // Ctrl+C cancels the in-flight turn; at an idle prompt it saves (if --save) and exits.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // never let the runtime kill us mid-write
            var active = _activeTurn;
            if (active is { IsCancellationRequested: false })
            {
                ui.Info("cancelling…");
                active.Cancel();
            }
            else
            {
                SaveSessionToDisk(ui, session, options.SavePath);
                Environment.Exit(0);
            }
        };

        return options.Command == Command.Run
            ? await RunOnceAsync(agent, session, options, ui).ConfigureAwait(false)
            : await ChatAsync(agent, session, options, ui).ConfigureAwait(false);
    }

    private static readonly string Version = (
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0").Split('+')[0];

    /// <summary>Executes a single task (the <c>run</c> command). Returns the process exit code.</summary>
    private static async Task<int> RunOnceAsync(CodingAgent agent, AgentSession session, CliOptions options, ConsoleUi ui)
    {
        var exitCode = await ExecuteTurnAsync(agent, session, options.Task!, ui).ConfigureAwait(false);
        await SaveSessionAsync(ui, session, options.SavePath).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>The interactive REPL.</summary>
    private static async Task<int> ChatAsync(CodingAgent agent, AgentSession session, CliOptions options, ConsoleUi ui)
    {
        // A bare `chat` with an initial task runs it first, then drops into the prompt.
        if (options.Task is { Length: > 0 } initial)
        {
            await ExecuteTurnAsync(agent, session, initial, ui).ConfigureAwait(false);
            await SaveSessionAsync(ui, session, options.SavePath).ConfigureAwait(false);
        }

        ui.Info("Interactive session — /help for commands, /exit to quit.");
        AnsiConsole.WriteLine();

        while (true)
        {
            AnsiConsole.Markup("[green]❯[/] ");
            var line = Console.ReadLine();
            if (line is null) // Ctrl+D / end of redirected input
            {
                AnsiConsole.WriteLine();
                break;
            }

            var input = line.Trim();
            if (input.Length == 0)
                continue;

            if (input.StartsWith('/'))
            {
                if (await HandleSlashCommandAsync(input, ui, session, options).ConfigureAwait(false))
                    break;
                continue;
            }

            await ExecuteTurnAsync(agent, session, input, ui).ConfigureAwait(false);
            await SaveSessionAsync(ui, session, options.SavePath).ConfigureAwait(false);
        }

        await SaveSessionAsync(ui, session, options.SavePath).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Runs one agent turn with streaming output. Returns a process exit code (0 success, 1 error, 2 max steps, 130 cancelled).</summary>
    private static async Task<int> ExecuteTurnAsync(CodingAgent agent, AgentSession session, string input, ConsoleUi ui)
    {
        using var turnCts = new CancellationTokenSource();
        _activeTurn = turnCts;
        try
        {
            var stream = agent.ExecuteStreamingAsync(session, input, turnCts.Token);
            var result = await ui.RenderStreamAsync(stream, turnCts.Token).ConfigureAwait(false);
            if (result is null)
            {
                ui.Error("the agent produced no result.");
                return 1;
            }

            ui.ShowResult(result, session);
            return result.Status switch
            {
                "Success" => 0,
                "MaxStepsReached" => 2,
                _ => 1,
            };
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine();
            ui.Info("cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            ui.Error(ex.Message);
            return 1;
        }
        finally
        {
            _activeTurn = null;
        }
    }

    /// <summary>Handles a /command. Returns true when the REPL should exit.</summary>
    private static async Task<bool> HandleSlashCommandAsync(string input, ConsoleUi ui, AgentSession session, CliOptions options)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        switch (cmd)
        {
            case "/exit":
            case "/quit":
            case "/q":
                return true;

            case "/help":
                ui.ShowReplHelp();
                return false;

            case "/clear":
                ui.Clear();
                return false;

            case "/session":
                ui.ShowSessionStats(session, options.ContextTokens);
                return false;

            case "/markdown":
                return HandleMarkdownCommand(arg, ui);

            case "/save":
                var target = arg ?? options.SavePath;
                if (target is null)
                {
                    ui.Error("no path given — usage: /save <path> (or pass --save at startup)");
                    return false;
                }
                await SaveSessionAsync(ui, session, target).ConfigureAwait(false);
                return false;

            default:
                ui.Error($"unknown command '{cmd}' — /help lists available commands");
                return false;
        }
    }

    /// <summary>Handles /markdown [auto|live|full|raw].</summary>
    private static bool HandleMarkdownCommand(string? arg, ConsoleUi ui)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            ui.Info($"markdown mode: {ui.MarkdownMode.ToString().ToLowerInvariant()} (auto|live|full|raw)");
            return false;
        }

        switch (arg.Trim().ToLowerInvariant())
        {
            case "auto": ui.MarkdownMode = MarkdownMode.Auto; break;
            case "live": ui.MarkdownMode = MarkdownMode.Live; break;
            case "full": ui.MarkdownMode = MarkdownMode.Full; break;
            case "raw":
            case "off":
            case "none": ui.MarkdownMode = MarkdownMode.Raw; break;
            default:
                ui.Error($"unknown markdown mode '{arg}' — use auto, live, full or raw");
                return false;
        }

        ui.Info($"markdown mode: {ui.MarkdownMode.ToString().ToLowerInvariant()}");
        return false;
    }

    private static async Task SaveSessionAsync(ConsoleUi ui, AgentSession session, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            await session.SaveAsync(path).ConfigureAwait(false);
            ui.Info($"session saved to {path}");
        }
        catch (Exception ex)
        {
            ui.Error($"failed to save session: {ex.Message}");
        }
    }

    private static void SaveSessionToDisk(ConsoleUi ui, AgentSession session, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            session.SaveAsync(path).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ui.Error($"failed to save session: {ex.Message}");
        }
    }
}
