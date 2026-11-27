using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using ReasoningEffortLevel = Microsoft.Extensions.AI.ReasoningEffort;

namespace SharpCoder.Cli;

/// <summary>The command the user asked for: an interactive session or a one-shot task.</summary>
public enum Command
{
    /// <summary>Start (or continue) an interactive chat session. The default when no task is given.</summary>
    Chat,

    /// <summary>Run a single task, print the result, and exit.</summary>
    Run,
}

/// <summary>
/// Parsed command-line options. Providers are selected through a provider-prefixed model string
/// (<c>copilot/...</c>, <c>github/...</c>, <c>ollama-cloud/...</c>, <c>ollama-local/...</c>);
/// see <see cref="SharpCoder.Providers.ChatClientFactory"/>. Credentials are read from the
/// environment by the factory (<c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c>, <c>OLLAMA_API_KEY</c>,
/// <c>OLLAMA_URL</c>, <c>LLM_PROVIDER</c>).
/// </summary>
public sealed class CliOptions
{
    /// <summary>
    /// Context window the CLI assumes when <c>--context-tokens</c> is not passed. The library
    /// default is intentionally conservative (100k); the CLI is tuned for current frontier models,
    /// so it defaults to 1,000,000 instead. Set <c>--context-tokens</c> for smaller-window models.
    /// </summary>
    public const int DefaultContextTokens = 1_000_000;

    /// <summary>The command to run. Defaults to <see cref="Command.Chat"/>.</summary>
    public Command Command { get; init; } = Command.Chat;

    /// <summary>The task text for <c>run</c> and for seeding <c>chat</c>; <c>null</c> for a bare interactive session.</summary>
    public string? Task { get; init; }

    /// <summary>How the CLI renders assistant markdown. Defaults to <see cref="MarkdownMode.Auto"/>.</summary>
    public MarkdownMode Markdown { get; init; } = MarkdownMode.Auto;

    /// <summary>Provider-prefixed model string (e.g. <c>ollama-local/llama3</c>), or a bare model id.</summary>
    public string? Model { get; init; }

    /// <summary>Working directory the agent operates in. Defaults to the current directory.</summary>
    public string WorkDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Maximum tool-call iterations per execution.</summary>
    public int MaxSteps { get; init; } = 25;

    /// <summary>Enables the bash tool. Off by default; see the security notes in the README.</summary>
    public bool EnableBash { get; init; }

    /// <summary>
    /// Which shell <c>execute_bash_command</c> launches. Defaults to <see cref="SharpCoder.ShellPreference.Auto"/>
    /// (pwsh on Windows when installed, else Windows PowerShell, then cmd.exe; /bin/bash on Unix).
    /// </summary>
    public SharpCoder.ShellPreference Shell { get; init; } = SharpCoder.ShellPreference.Auto;

    /// <summary>Reasoning effort for models with extended thinking; <c>null</c> leaves the provider default.</summary>
    public ReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Context window size in tokens for the model, from <c>--context-tokens</c>; <c>null</c> keeps the library default.
    /// Used for compaction decisions (automatic compaction triggers at 80% of this value).
    /// </summary>
    public int? ContextTokens { get; init; }

    /// <summary>
    /// Token budget for the verbatim tail kept during compaction, from <c>--retain-tokens</c>;
    /// <c>null</c> keeps the library default (16,000). Older messages beyond this budget are compacted.
    /// </summary>
    public int? RetainTokens { get; init; }

    /// <summary>Path of a session JSON file to resume.</summary>
    public string? SessionPath { get; init; }

    /// <summary>Path to save the session JSON to (written after every turn and on exit).</summary>
    public string? SavePath { get; init; }

    /// <summary>True when <c>-v</c>/<c>--version</c> was passed; the caller should print the version and exit.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>Usage text printed by <c>--help</c>.</summary>
    public const string HelpText =
        """
        SharpCoder.Cli — an interactive coding assistant for your terminal

        USAGE
            SharpCoder.Cli [command] [options] [task...]

        COMMANDS
            chat [task]   Start an interactive session (default). Optionally seeds the
                          session with an initial task before the prompt appears.
            run <task>    Execute a single task, print the result, and exit.

        OPTIONS
            -m, --model <model>       Provider-prefixed model, e.g. copilot/<model>,
                                      github/<model>, ollama-cloud/<model>, ollama-local/<model>.
                                      Defaults to $LLM_PROVIDER (provider default 'copilot').
            -w, --workdir <dir>       Working directory for the agent (default: current directory).
            -n, --max-steps <n>       Max tool-call iterations per execution (default: 25).
                --context-tokens <n>  Context window of the model, in tokens. Accepts a plain
                                      number or a k/m suffix (e.g. 200000, 200k, 1m).
                                      Default: 1000000. Compaction triggers at 80% of this.
                --retain-tokens <n>   Tokens of recent context kept verbatim when compacting.
                                      Plain number or k/m suffix. Default: 16000.
                --reasoning <level>   none | low | medium | high | extra-high (aliases: off, xhigh).
                --markdown <mode>     auto | live | full | raw. Render assistant output as
                                      markdown (default: auto — live on a terminal, raw when piped).
                --bash                Enable the bash tool (off by default; see README security notes).
                --shell <shell>       auto | pwsh | powershell | cmd | bash. Which shell the
                                      bash tool launches. Default: auto (pwsh → powershell.exe →
                                      cmd.exe on Windows; /bin/bash elsewhere).
                --session <file>      Resume a saved session (JSON).
                --save <file>         Save the session to a file after every turn and on exit.
            -v, --version             Print the version and exit.
            -h, --help                Show this help and exit.

        ENVIRONMENT
            LLM_PROVIDER              Default provider when --model is absent (default: copilot).
            GITHUB_TOKEN / GH_TOKEN   Token for the copilot and github providers.
            OLLAMA_API_KEY            Required for the ollama-cloud provider.
            OLLAMA_URL                Base URL for ollama-local (default: http://localhost:11434).

        EXAMPLES
            SharpCoder.Cli                              # interactive session
            SharpCoder.Cli "Summarize this repo"        # one-shot task (implies run)
            SharpCoder.Cli run "Fix the failing test"
            SharpCoder.Cli chat "Start by reading README.md"
            SharpCoder.Cli --model ollama-local/llama3 --bash
        """;

    /// <summary>
    /// Parses a command line. Returns <c>null</c> for <c>-h</c>/<c>--help</c> (caller should print
    /// <see cref="HelpText"/> and exit). Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public static CliOptions? Parse(string[] args)
    {
        var command = Command.Chat;
        var commandSeen = false;
        var model = (string?)null;
        var workDirectory = Directory.GetCurrentDirectory();
        var maxSteps = 25;
        var enableBash = false;
        var shell = SharpCoder.ShellPreference.Auto;
        ReasoningEffortLevel? reasoning = null;
        int? contextTokens = null;
        int? retainTokens = null;
        string? sessionPath = null;
        string? savePath = null;
        var markdown = MarkdownMode.Auto;
        var showVersion = false;
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    return null;
                case "chat" when !commandSeen && positionals.Count == 0:
                    command = Command.Chat;
                    commandSeen = true;
                    break;
                case "run" when !commandSeen && positionals.Count == 0:
                    command = Command.Run;
                    commandSeen = true;
                    break;
                case "-m":
                case "--model":
                    model = RequireValue(args, ref i, arg);
                    break;
                case "-w":
                case "--workdir":
                    workDirectory = RequireValue(args, ref i, arg);
                    break;
                case "-n":
                case "--max-steps":
                    if (!int.TryParse(RequireValue(args, ref i, arg), out var steps) || steps <= 0)
                        throw new ArgumentException($"--max-steps expects a positive integer, got '{args[i]}'.");
                    maxSteps = steps;
                    break;
                case "--context-tokens":
                    contextTokens = ParseContextTokens(RequireValue(args, ref i, arg), "--context-tokens");
                    break;
                case "--retain-tokens":
                    retainTokens = ParseContextTokens(RequireValue(args, ref i, arg), "--retain-tokens");
                    break;
                case "--reasoning":
                    reasoning = ParseReasoning(RequireValue(args, ref i, arg));
                    break;
                case "--markdown":
                    markdown = ParseMarkdownMode(RequireValue(args, ref i, arg));
                    break;
                case "--bash":
                    enableBash = true;
                    break;
                case "--shell":
                    shell = ParseShell(RequireValue(args, ref i, arg));
                    break;
                case "--session":
                    sessionPath = RequireValue(args, ref i, arg);
                    break;
                case "--save":
                    savePath = RequireValue(args, ref i, arg);
                    break;
                case "-v":
                case "--version":
                    showVersion = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        throw new ArgumentException($"Unknown option: {arg}");
                    positionals.Add(arg);
                    break;
            }
        }

        if (showVersion)
            return new CliOptions { ShowVersion = true };

        var task = positionals.Count > 0 ? string.Join(' ', positionals).Trim() : null;
        if (task is { Length: 0 })
            task = null;

        // A bare task with no subcommand means "run it", matching common agent CLIs.
        if (!commandSeen && task is not null)
            command = Command.Run;

        if (command == Command.Run && task is null)
            throw new ArgumentException("The 'run' command requires a task. Example: SharpCoder.Cli run \"Describe this repo\"");

        return new CliOptions
        {
            Command = command,
            Task = task,
            Model = model,
            WorkDirectory = workDirectory,
            MaxSteps = maxSteps,
            EnableBash = enableBash,
            Shell = shell,
            ReasoningEffort = reasoning,
            Markdown = markdown,
            ContextTokens = contextTokens,
            RetainTokens = retainTokens,
            SessionPath = sessionPath,
            SavePath = savePath,
        };
    }

    private static int ParseContextTokens(string value, string flagName)
    {
        var text = value.Trim();
        long multiplier = 1;
        if (text.Length > 1)
        {
            var suffix = char.ToLowerInvariant(text[^1]);
            if (suffix == 'k') { multiplier = 1_000L; text = text[..^1]; }
            else if (suffix == 'm') { multiplier = 1_000_000L; text = text[..^1]; }
        }

        if (!long.TryParse(text, out var parsed) || parsed <= 0 || parsed > int.MaxValue / multiplier)
            throw new ArgumentException($"{flagName} expects a positive token count (e.g. 200000, 200k, or 1m), got '{value}'.");
        return (int)(parsed * multiplier);
    }

    private static SharpCoder.ShellPreference ParseShell(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => SharpCoder.ShellPreference.Auto,
            "pwsh" or "pwsh7" or "powershell7" => SharpCoder.ShellPreference.Pwsh,
            "powershell" or "powershell5" or "winps" => SharpCoder.ShellPreference.PowerShell,
            "cmd" or "cmd.exe" => SharpCoder.ShellPreference.Cmd,
            "bash" or "sh" => SharpCoder.ShellPreference.Bash,
            _ => throw new ArgumentException($"Unknown --shell '{value}'. Use auto, pwsh, powershell, cmd or bash."),
        };
    }

    private static MarkdownMode ParseMarkdownMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => MarkdownMode.Auto,
            "live" => MarkdownMode.Live,
            "full" => MarkdownMode.Full,
            "raw" or "none" or "off" => MarkdownMode.Raw,
            _ => throw new ArgumentException($"Unknown --markdown mode '{value}'. Use auto, live, full or raw."),
        };
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {flag}.");
        return args[++i];
    }

    private static ReasoningEffortLevel ParseReasoning(string value)
    {
        var normalized = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return normalized switch
        {
            "off" or "none" => ReasoningEffortLevel.None,
            "low" => ReasoningEffortLevel.Low,
            "medium" => ReasoningEffortLevel.Medium,
            "high" => ReasoningEffortLevel.High,
            "xhigh" or "extrahigh" or "extra" => ReasoningEffortLevel.ExtraHigh,
            _ => throw new ArgumentException($"Unknown reasoning effort '{value}'. " +
                "Use none, low, medium, high, or extra-high."),
        };
    }
}
