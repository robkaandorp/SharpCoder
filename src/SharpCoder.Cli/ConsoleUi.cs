using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// Spectre.Console-based renderer for the CLI: banner, configuration summary, streaming
/// responses with a "thinking" spinner, tool-call narration, and result/session summaries.
/// All agent-facing output flows through here so the interactive and one-shot paths share
/// the same look and feel.
/// </summary>
public sealed class ConsoleUi
{
    private const string Accent = "cyan";
    private const string Good = "green";
    private const string Warn = "yellow";
    private const string Bad = "red";

    // Braille spinner frames used while waiting for the first token.
    private static readonly string[] SpinnerFrames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    /// <summary>Prints the application banner.</summary>
    public void ShowBanner(string version)
    {
        var grid = new Grid().AddColumn();
        grid.AddRow(new Markup($"[bold {Accent}]SharpCoder.Cli[/] [dim]v{Markup.Escape(version)}[/]"));
        grid.AddRow(new Markup("[dim]An autonomous coding agent for your terminal[/]"));
        AnsiConsole.Write(new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(1, 0),
            Expand = false,
        });
        AnsiConsole.WriteLine();
    }

    /// <summary>Renders the resolved configuration table after startup.</summary>
    public void Configuration(
        string? model,
        string workDirectory,
        int maxSteps,
        bool enableBash,
        ReasoningEffort? reasoning,
        string? savePath)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("[dim]Setting[/]").AddColumn("[dim]Value[/]");
        table.AddRow("Model", Markup.Escape(model ?? $"default ($LLM_PROVIDER → {Environment.GetEnvironmentVariable("LLM_PROVIDER") ?? "copilot"})"));
        table.AddRow("Working directory", Markup.Escape(workDirectory));
        table.AddRow("Max steps", maxSteps.ToString());
        table.AddRow("Bash tool", enableBash
            ? $"[{Warn}]enabled[/] [dim](the agent can run arbitrary shell commands)[/]"
            : "[dim]disabled[/]");
        table.AddRow("Reasoning", reasoning?.ToString() ?? "[dim]provider default[/]");
        if (savePath is not null)
            table.AddRow("Session file", Markup.Escape(savePath));
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>Notes that a session was restored from disk.</summary>
    public void SessionRestored(AgentSession session)
    {
        AnsiConsole.MarkupLine($"[{Good}]✓[/] Restored session [dim]{Markup.Escape(session.SessionId)}[/] — " +
            $"{session.MessageHistory.Count} message(s), {session.TotalToolCalls} tool call(s), " +
            $"{session.InputTokensUsed + session.OutputTokensUsed:n0} tokens used so far.");
    }

    /// <summary>Dim note shown when context compaction starts.</summary>
    public void Compacting() => AnsiConsole.MarkupLine("[dim]↻ compacting context…[/]");

    /// <summary>Dim note shown when compaction finishes.</summary>
    public void Compacted(CompactionResult result) =>
        AnsiConsole.MarkupLine($"[dim]↻ compacted {result.MessagesBefore} → {result.MessagesAfter} messages " +
            $"({result.TokensBefore:n0} → {result.TokensAfter:n0} tokens, -{result.ReductionPercent}%)[/]");

    /// <summary>
    /// Consumes a streaming execution, rendering deltas as they arrive. A spinner is shown until the
    /// first text chunk lands. Returns the final result, or <c>null</c> if the stream ended without one.
    /// </summary>
    public async Task<AgentResult?> RenderStreamAsync(IAsyncEnumerable<StreamingUpdate> updates, CancellationToken ct)
    {
        using var spinnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var spinner = Task.Run(() => Spin(spinnerCts.Token), CancellationToken.None);

        var receivedText = false;
        AgentResult? result = null;
        try
        {
            await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false))
            {
                switch (update.Kind)
                {
                    case StreamingUpdateKind.TextDelta when update.Text is { Length: > 0 } text:
                        StopSpinner(spinnerCts, spinner);
                        receivedText = true;
                        Console.Write(text);
                        break;
                    case StreamingUpdateKind.Completed:
                        result = update.Result;
                        break;
                }
            }
        }
        finally
        {
            StopSpinner(spinnerCts, spinner);
            if (receivedText)
                Console.WriteLine();
            else
                ClearSpinnerLine();
        }

        return result;
    }

    /// <summary>Renders the per-turn outcome: status, model, usage and cumulative session totals.</summary>
    public void ShowResult(AgentResult result, AgentSession session)
    {
        var (glyph, color, note) = result.Status switch
        {
            "Success" => ("✓", Good, (string?)null),
            "MaxStepsReached" => ("!", Warn, "max steps reached — raise --max-steps to allow more tool calls"),
            _ => ("✗", Bad, (string?)null),
        };

        AnsiConsole.MarkupLine($"[{color}]{glyph} {Markup.Escape(result.Status)}[/]" +
            (result.ModelId is { Length: > 0 } m ? $"  [dim]model: {Markup.Escape(m)}[/]" : string.Empty));

        if (result.Status != "Success" && !string.IsNullOrWhiteSpace(result.Message))
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(result.Message)}[/]");
        if (note is not null)
            AnsiConsole.MarkupLine($"[{Warn}]{note}[/]");

        var turn = result.TokenUsage.Total;
        var cumulativeIn = session.InputTokensUsed;
        var cumulativeOut = session.OutputTokensUsed;
        var cached = turn.CachedInputReportedCalls > 0 ? $" (cached {turn.CachedInputTokens:n0})" : string.Empty;
        var reasoning = turn.ReasoningReportedCalls > 0 ? $" (reasoning {turn.ReasoningTokens:n0})" : string.Empty;
        AnsiConsole.MarkupLine(
            $"[dim]turn:[/] in {turn.InputTokens:n0}{cached} / out {turn.OutputTokens:n0}{reasoning} · " +
            $"{result.ToolCallCount} tool call(s)");
        AnsiConsole.MarkupLine(
            $"[dim]session:[/] in {cumulativeIn:n0} / out {cumulativeOut:n0} · " +
            $"{session.TotalToolCalls} tool call(s)");
        AnsiConsole.WriteLine();
    }

    /// <summary>Prints the result of failing to produce a response (before any agent run happened).</summary>
    public void Error(string message) => AnsiConsole.MarkupLine($"[{Bad}]error:[/] {Markup.Escape(message)}");

    /// <summary>Prints dim informational text.</summary>
    public void Info(string message) => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");

    /// <summary>Prints the in-REPL slash-command help.</summary>
    public void ShowReplHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("[dim]Command[/]").AddColumn("[dim]Description[/]");
        table.AddRow("/help", "Show this help");
        table.AddRow("/save [[path]]", "Save the session (defaults to --save path)");
        table.AddRow("/session", "Show session statistics");
        table.AddRow("/clear", "Clear the screen");
        table.AddRow("/exit, /quit", "Save (if --save) and exit");
        AnsiConsole.Write(table);
    }

    /// <summary>Prints session statistics.</summary>
    public void ShowSessionStats(AgentSession session)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("[dim]Field[/]").AddColumn("[dim]Value[/]");
        table.AddRow("Session id", Markup.Escape(session.SessionId));
        table.AddRow("Messages", session.MessageHistory.Count.ToString());
        table.AddRow("Tool calls", session.TotalToolCalls.ToString());
        table.AddRow("Tokens in / out", $"{session.InputTokensUsed:n0} / {session.OutputTokensUsed:n0}");
        table.AddRow("Last known context", session.LastKnownContextTokens > 0 ? $"{session.LastKnownContextTokens:n0} tokens" : "unknown");
        table.AddRow("Created", session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("Last activity", session.LastActivityAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AnsiConsole.Write(table);
    }

    /// <summary>Clears the terminal.</summary>
    public void Clear() => AnsiConsole.Clear();

    private static void ClearSpinnerLine()
    {
        if (Console.IsOutputRedirected)
            return;
        // Overwrite whatever the spinner drew with spaces, then return to column 0.
        Console.Write('\r');
        Console.Write(new string(' ', 40));
        Console.Write('\r');
    }

    private static async Task Spin(CancellationToken ct)
    {
        // No animation when output is redirected — raw frames would just be log noise.
        if (Console.IsOutputRedirected)
            return;
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = SpinnerFrames[i++ % SpinnerFrames.Length];
                Console.Write($"\r{frame} thinking…");
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: spinner stopped because output started or the turn ended.
        }
    }

    private static void StopSpinner(CancellationTokenSource cts, Task spinner)
    {
        // Idempotent: the spinner line is cleared exactly once — either when the first
        // delta arrives, or here in the finally block for spinner-only (no text) runs.
        if (cts.IsCancellationRequested)
            return;
        cts.Cancel();
        try { spinner.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { /* spinner faults are cosmetic */ }
        ClearSpinnerLine();
    }
}
