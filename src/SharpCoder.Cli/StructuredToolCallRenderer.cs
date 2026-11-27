using SharpCoder;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// Renders the structured tool-call updates emitted by <c>CodingAgent</c> as real terminal widgets
/// (rather than dim one-liners sniffed out of the markdown stream).
/// </summary>
internal sealed class StructuredToolCallRenderer
{
    private readonly IAnsiConsole _console;
    private readonly bool _verbose;
    private readonly bool _raw;

    /// <summary>Number of completed tool calls rendered this turn.</summary>
    public int CompletedCount { get; private set; }

    /// <summary>Creates a renderer.</summary>
    /// <param name="console">Console to write to.</param>
    /// <param name="verbose">When true, print full (untruncated, multi-line) tool results.</param>
    /// <param name="raw">When true, keep output free of ANSI styling (redirected output).</param>
    public StructuredToolCallRenderer(IAnsiConsole console, bool verbose, bool raw)
    {
        _console = console;
        _verbose = verbose;
        _raw = raw;
    }

    public void Started(ToolCallUpdate detail)
    {
        var args = FormatArguments(detail.Arguments);
        Write($"  🔧 {detail.Name}({args})", started: true);
    }

    public void Finished(ToolCallUpdate detail)
    {
        CompletedCount++;
        var result = detail.Result ?? "(no result)";
        if (_verbose)
        {
            Write("  ↳ result:", started: false);
            foreach (var line in result.Replace("\r\n", "\n").Split('\n'))
                Write("    " + line, started: false, plain: true);
        }
        else
        {
            var firstLine = result.Replace("\r\n", "\n").Split('\n')[0];
            if (firstLine.Length > 160) firstLine = firstLine[..159] + "…";
            Write($"  ↳ {firstLine}", started: false, error: detail.IsError);
        }

        if (detail.IsError && !_verbose)
            Write("    (tool reported an error)", started: false, error: true);
        Write($"  ⏱ {detail.Elapsed.TotalMilliseconds:n0} ms", started: false);
    }

    public void Progress(RoundProgressUpdate progress)
    {
        Write($"  … round {progress.Round + 1}, {progress.ToolCallsSoFar}/{progress.MaxSteps} steps", started: false);
    }

    private static string FormatArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0) return string.Empty;
        var parts = arguments.Select(a =>
        {
            var value = a.Value?.ToString() ?? "null";
            if (value.Length > 80) value = value[..79] + "…";
            return $"{a.Key}={value}";
        });
        var joined = string.Join(", ", parts);
        return joined.Length > 200 ? joined[..199] + "…" : joined;
    }

    private void Write(string text, bool started, bool error = false, bool plain = false)
    {
        if (_raw)
        {
            // In raw mode keep annotations off stdout entirely — they are not model text.
            return;
        }

        var escaped = Markup.Escape(text);
        if (plain)
            _console.MarkupLine($"[dim]{escaped}[/]");
        else if (error)
            _console.MarkupLine($"[red]{escaped}[/]");
        else if (started)
            _console.MarkupLine($"[cyan]{escaped}[/]");
        else
            _console.MarkupLine($"[dim]{escaped}[/]");
    }
}
