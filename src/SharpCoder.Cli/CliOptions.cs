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
    /// <summary>The command to run. Defaults to <see cref="Command.Chat"/>.</summary>
    public Command Command { get; init; } = Command.Chat;

    /// <summary>The task text for <c>run</c> and for seeding <c>chat</c>; <c>null</c> for a bare interactive session.</summary>
    public string? Task { get; init; }

    /// <summary>Provider-prefixed model string (e.g. <c>ollama-local/llama3</c>), or a bare model id.</summary>
    public string? Model { get; init; }

    /// <summary>Working directory the agent operates in. Defaults to the current directory.</summary>
    public string WorkDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Maximum tool-call iterations per execution.</summary>
    public int MaxSteps { get; init; } = 25;

    /// <summary>Enables the bash tool. Off by default; see the security notes in the README.</summary>
    public bool EnableBash { get; init; }

    /// <summary>Reasoning effort for models with extended thinking; <c>null</c> leaves the provider default.</summary>
    public ReasoningEffort? ReasoningEffort { get; init; }

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
                --reasoning <level>   none | low | medium | high | extra-high (aliases: off, xhigh).
                --bash                Enable the bash tool (off by default; see README security notes).
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
        ReasoningEffortLevel? reasoning = null;
        string? sessionPath = null;
        string? savePath = null;
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
                case "--reasoning":
                    reasoning = ParseReasoning(RequireValue(args, ref i, arg));
                    break;
                case "--bash":
                    enableBash = true;
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
            ReasoningEffort = reasoning,
            SessionPath = sessionPath,
            SavePath = savePath,
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
