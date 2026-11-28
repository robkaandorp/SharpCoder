using SharpCoder;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// Renders structured tool-call updates. Two modes:
/// <list type="bullet">
/// <item><b>Compact</b> (default): one line per call (two for bash), driven entirely by
/// <see cref="ToolCallFacts"/> — never by parsing <see cref="ToolCallUpdate.Result"/> text.</item>
/// <item><b>Verbose</b>: a Spectre panel per call with arguments, the full result and status.</item>
/// </list>
/// Raw mode (redirected output) writes nothing, so stdout stays plain model text.
/// </summary>
internal sealed class StructuredToolCallRenderer
{
    private readonly IAnsiConsole _console;
    private readonly ToolDisplayMode _mode;
    private readonly bool _raw;
    private readonly int _width;

    /// <summary>Number of completed tool calls rendered this turn.</summary>
    public int CompletedCount { get; private set; }

    public StructuredToolCallRenderer(IAnsiConsole console, ToolDisplayMode mode, bool raw)
    {
        _console = console;
        _mode = mode;
        _raw = raw;
        _width = Math.Max(40, console.Profile.Width);
    }

    /// <summary>Compact mode emits nothing at start; verbose shows an in-flight panel shell.</summary>
    public void Started(ToolCallUpdate detail)
    {
        if (_raw || _mode == ToolDisplayMode.Compact)
            return;

        _console.MarkupLine($"[dim]⏳ {Markup.Escape(detail.Name)} — running…[/]");
    }

    public void Finished(ToolCallUpdate detail)
    {
        CompletedCount++;
        if (_raw)
            return;

        if (_mode == ToolDisplayMode.Verbose)
        {
            RenderPanel(detail);
            return;
        }

        RenderCompact(detail);
    }

    /// <summary>Round progress is suppressed in compact mode.</summary>
    public void Progress(RoundProgressUpdate progress)
    {
        if (_raw || _mode == ToolDisplayMode.Compact)
            return;
        _console.MarkupLine($"[dim]  … round {progress.Round + 1}, {progress.ToolCallsSoFar}/{progress.MaxSteps} steps[/]");
    }

    // ---------------------------------------------------------------- compact

    private void RenderCompact(ToolCallUpdate detail)
    {
        var status = detail.IsError ? "[red]✗[/]" : "[green]✓[/]";
        var label = Label(detail.Name);
        var facts = detail.Facts;
        var args = detail.Arguments;
        var elapsed = ToolDisplayFormatters.Duration(detail.Elapsed);

        if (detail.Name == "execute_bash_command")
        {
            RenderBash(status, label, facts, args, elapsed);
            return;
        }

        // Compact layout is two columns: icon + label + the primary subject (a filename,
        // pattern or skill name) on the LEFT, and metrics + elapsed time flush RIGHT.
        // Padding is computed against the console width so the right column always ends
        // at the same margin, which makes a stream of tool calls easy to scan.
        var (subject, metrics) = detail.Name switch
        {
            "read_file" => ReadFileParts(args, facts),
            "edit_file" => EditFileParts(args, facts),
            "write_file" => WriteFileParts(args, facts),
            "load_skill" => SkillParts(args, facts),
            "list_skills" => SkillsParts(facts),
            "glob" => GlobParts(args, facts),
            "grep" => GrepParts(args, facts),
            _ => GenericParts(detail),
        };

        var right = string.IsNullOrEmpty(metrics) ? elapsed : $"{metrics} · {elapsed}";
        RenderColumns(status, label, subject, right);
    }

    /// <summary>
    /// Writes one compact line: the icon, label and subject flush LEFT, and the metrics
    /// flush RIGHT at the terminal margin. Widths are measured on the plain fragments
    /// (markup tags are zero-width), so the padding is exact and the line never wraps —
    /// even with the wide 🔧 emoji in the prefix.
    /// </summary>
    private void RenderColumns(string status, string label, CompactSubject subject,
        string right, string? rightMarkup = null)
    {
        var glyph = status.Contains("✗") ? "✗" : "✓";
        var prefixWidth = ToolDisplayFormatters.CellWidth($"  🔧 {glyph} {label}  ");

        // Give the subject at least a short visible slice (the tail of a filename) before
        // sacrificing anything else. On very narrow consoles drop whole leading metric
        // segments at the " · " boundaries, keeping at least the elapsed time; that avoids
        // nonsense like "…2 chars" while still landing the right column flush right.
        const int MinSubjectWidth = 12;
        var maxRightWidth = Math.Max(1, _width - prefixWidth - MinSubjectWidth - 1);
        var rightText = TrimMetrics(right, maxRightWidth);
        var rightWidth = ToolDisplayFormatters.CellWidth(rightText);

        var budget = Math.Max(0, _width - prefixWidth - rightWidth - 1);
        var clipped = subject.Clip(budget);

        var leftWidth = prefixWidth + ToolDisplayFormatters.CellWidth(clipped.Plain);
        var gap = Math.Max(0, _width - leftWidth - rightWidth);

        // Use the caller's coloured markup only when nothing was clipped; otherwise escape
        // the (possibly shortened) plain text so the visible width stays exact.
        var renderedRight = rightMarkup is not null && rightText == right
            ? rightMarkup
            : Markup.Escape(rightText);
        var prefix = $"  🔧 {status} [cyan]{label}[/]  ";
        _console.MarkupLine(prefix + clipped.Markup + new string(' ', gap) + renderedRight);
    }

    /// <summary>
    /// Drops whole leading "segment · " chunks until the metrics fit; if even a single
    /// segment is too wide, it is clipped from the left as a last resort. Never returns
    /// an empty string, so the right column always has something to render.
    /// </summary>
    private static string TrimMetrics(string right, int maxWidth)
    {
        if (ToolDisplayFormatters.CellWidth(right) <= maxWidth) return right;

        var text = right;
        while (ToolDisplayFormatters.CellWidth(text) > maxWidth)
        {
            var separator = text.IndexOf(" · ", StringComparison.Ordinal);
            if (separator < 0) break;
            var next = text[(separator + 3)..];
            if (next.Length == 0) break;
            text = next;
        }

        return ToolDisplayFormatters.CellWidth(text) <= maxWidth
            ? text
            : ToolDisplayFormatters.ClipStart(text, maxWidth);
    }

    private void RenderBash(string status, string label,
        ToolCallFacts? facts, IReadOnlyDictionary<string, object?> args, string elapsed)
    {
        var timeout = facts?.TimeoutMs is { } ms
            ? ToolDisplayFormatters.Timeout(ms) + (facts.TimeoutIsDefault == true ? " (default)" : string.Empty)
            : "unknown";
        var exit = facts?.TimedOut == true
            ? "[red]timed out[/]"
            : facts?.ExitCode is { } code
                ? (code == 0 ? "[green]exit 0[/]" : $"[red]exit {code}[/]")
                : "[dim]exit ?[/]";
        var exitPlain = facts?.TimedOut == true
            ? "timed out"
            : facts?.ExitCode is { } exitCode ? $"exit {exitCode}" : "exit ?";
        var command = ToolDisplayFormatters.Argument(args, "command");
        var commandWidth = Math.Max(20, _width - 14);

        // Same two columns as the other tools: the effective timeout on the left, the
        // elapsed time and colour-coded exit status flush right.
        RenderColumns(status, label, CompactSubject.Plain($"⏱ {timeout} timeout"),
            $"{elapsed} · {exitPlain}", $"{Markup.Escape(elapsed)} · {exit}");
        if (!string.IsNullOrWhiteSpace(command))
            _console.MarkupLine($"     [dim]{ToolDisplayFormatters.Clip(command, commandWidth)}[/]");
    }

    /// <summary>File read/edit/write share the same subject: an emphasised, compact file path.</summary>
    private (CompactSubject, string) ReadFileParts(IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var range = facts?.StartLine is { } start && facts.EndLine is { } end
            ? $"{start}–{end}"
            : "?";
        var chars = facts?.CharactersRead is { } n ? $"{n:n0} chars" : "? chars";
        return (CompactSubject.Path(ToolDisplayFormatters.Argument(args, "filePath")), $"{range} · {chars}");
    }

    private (CompactSubject, string) EditFileParts(IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var occurrences = facts?.OccurrencesReplaced is { } n ? $"{n}×" : "?×";
        var removed = facts?.CharactersRemoved is { } r ? $"−{r:n0}" : "−?";
        var added = facts?.CharactersAdded is { } a ? $"+{a:n0}" : "+?";
        return (CompactSubject.Path(ToolDisplayFormatters.Argument(args, "filePath")),
            $"{occurrences} · {removed}/{added} chars");
    }

    private (CompactSubject, string) WriteFileParts(IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var chars = facts?.CharactersWritten is { } n ? $"{n:n0} chars" : "? chars";
        return (CompactSubject.Path(ToolDisplayFormatters.Argument(args, "filePath")), chars);
    }

    private static (CompactSubject, string) SkillParts(
        IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var name = ToolDisplayFormatters.Argument(args, "name") ?? facts?.SkillName ?? "?";
        return (CompactSubject.Bold(name), string.Empty);
    }

    private static (CompactSubject, string) SkillsParts(ToolCallFacts? facts) =>
        (CompactSubject.Plain(string.Empty), facts?.SkillCount is { } n ? $"{n} available" : "? available");

    private static (CompactSubject, string) GlobParts(
        IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var count = facts?.MatchCount is { } n ? $"{n:n0} matches" : "? matches";
        return (CompactSubject.Bold(ToolDisplayFormatters.Argument(args, "pattern") ?? "?"), count);
    }

    private static (CompactSubject, string) GrepParts(
        IReadOnlyDictionary<string, object?> args, ToolCallFacts? facts)
    {
        var pattern = ToolDisplayFormatters.Argument(args, "pattern") ?? "?";
        var include = ToolDisplayFormatters.Argument(args, "include");
        var subject = string.IsNullOrEmpty(include) ? pattern : $"{pattern}  {include}";
        var count = facts?.MatchCount is { } m ? $"{m:n0} matches" : "? matches";
        var files = facts?.FileCount is { } f ? $" in {f:n0} files" : string.Empty;
        return (CompactSubject.Bold(subject), $"{count}{files}");
    }

    private static (CompactSubject, string) GenericParts(ToolCallUpdate detail)
    {
        var parts = detail.Arguments.Take(3)
            .Select(a => $"{a.Key}={a.Value?.ToString()}");
        return (CompactSubject.Plain(string.Join(", ", parts)), string.Empty);
    }

    private static string Label(string name) => name switch
    {
        "read_file" => "Read",
        "edit_file" => "Edit",
        "write_file" => "Write",
        "load_skill" => "Skill",
        "list_skills" => "Skills",
        "glob" => "Glob",
        "grep" => "Grep",
        "execute_bash_command" => "Bash",
        _ => Markup.Escape(name),
    };

    // ---------------------------------------------------------------- verbose

    private void RenderPanel(ToolCallUpdate detail)
    {
        var status = detail.IsError ? "[red]✗ error[/]" : "[green]✓ ok[/]";
        var elapsed = ToolDisplayFormatters.Duration(detail.Elapsed);
        var grid = new Grid().AddColumn();

        grid.AddRow(new Markup($"[dim]ARGUMENTS[/]"));
        if (detail.Arguments.Count == 0)
        {
            grid.AddRow(new Markup("  [dim](none)[/]"));
        }
        else
        {
            foreach (var (key, value) in detail.Arguments)
            {
                var text = value?.ToString() ?? "(null)";
                if (text.Length > 600) text = text[..599] + "…";
                foreach (var line in text.Replace("\r\n", "\n").Split('\n').Take(8))
                    grid.AddRow(new Markup($"  [dim]{Markup.Escape(key)}:[/] {Markup.Escape(line)}"));
            }
        }

        if (detail.Facts is { } facts)
        {
            var metrics = new List<string>();
            if (facts.StartLine is { } s && facts.EndLine is { } e) metrics.Add($"lines {s}–{e}");
            if (facts.CharactersRead is { } cr) metrics.Add($"{cr:n0} chars read");
            if (facts.OccurrencesReplaced is { } o) metrics.Add($"{o}× replaced");
            if (facts.CharactersRemoved is { } r) metrics.Add($"−{r:n0} chars");
            if (facts.CharactersAdded is { } a) metrics.Add($"+{a:n0} chars");
            if (facts.CharactersWritten is { } w) metrics.Add($"{w:n0} chars written");
            if (facts.MatchCount is { } m) metrics.Add($"{m:n0} matches");
            if (facts.FileCount is { } f) metrics.Add($"{f:n0} files");
            if (facts.SkillCount is { } sc) metrics.Add($"{sc:n0} skills");
            if (facts.TimeoutMs is { } t) metrics.Add($"timeout {ToolDisplayFormatters.Timeout(t)}{(facts.TimeoutIsDefault == true ? " (default)" : string.Empty)}");
            if (facts.ExitCode is { } ec) metrics.Add($"exit {ec}");
            if (metrics.Count > 0)
                grid.AddRow(new Markup($"  [dim]{Markup.Escape(string.Join(" · ", metrics))}[/]"));
        }

        grid.AddRow(new Markup("[dim]RESULT[/]"));
        var result = detail.Result ?? "(no result)";
        var resultLines = result.Replace("\r\n", "\n").Split('\n');
        foreach (var line in resultLines.Take(40))
            grid.AddRow(new Markup("  " + Markup.Escape(line)));
        if (resultLines.Length > 40)
            grid.AddRow(new Markup($"  [dim]… {resultLines.Length - 40} more lines[/]"));

        var panel = new Panel(grid)
        {
            Header = new PanelHeader($" 🔧 {Label(detail.Name)}  {status}  {Markup.Escape(elapsed)} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(detail.IsError ? Color.Red : Color.Cyan1),
            Expand = true,
        };
        _console.Write(panel);
    }
}
