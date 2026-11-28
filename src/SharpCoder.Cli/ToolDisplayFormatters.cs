using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>Pure formatting helpers for compact tool-call lines. No console I/O, so they are unit-testable.</summary>
internal static class ToolDisplayFormatters
{
    /// <summary>
    /// Formats a duration in a compact, human-readable form: <c>820 ms</c>, <c>1.4 s</c>,
    /// <c>2 m 03 s</c>, <c>1 h 05 m</c>.
    /// </summary>
    public static string Duration(TimeSpan value)
    {
        var totalSeconds = value.TotalSeconds;
        if (totalSeconds < 1)
            return $"{Math.Max(0, value.TotalMilliseconds):n0} ms";
        if (totalSeconds < 60)
            return $"{totalSeconds:0.0} s";
        if (value.TotalHours >= 1)
            return $"{(long)value.TotalHours} h {value.Minutes:00} m";
        return $"{(long)value.TotalMinutes} m {value.Seconds:00} s";
    }

    /// <summary>Formats a millisecond timeout in the same human-readable style as <see cref="Duration"/>.</summary>
    public static string Timeout(long milliseconds) => Duration(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Produces Spectre markup for a file path that keeps the filename emphasised when the whole
    /// path does not fit: the directory part is dim and elided from the left with a leading ellipsis,
    /// while the filename is bold. Both parts are escaped so brackets in paths cannot inject markup.
    /// </summary>
    /// <param name="path">Path as supplied by the model.</param>
    /// <param name="maxWidth">Available visible characters.</param>
    public static string CompactPath(string? path, int maxWidth)
    {
        if (string.IsNullOrEmpty(path))
            return "[dim](no path)[/]";

        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // Always keep the filename. Shrink the directory from the left until the visible length fits.
        var plain = directory + name;
        if (plain.Length > maxWidth)
        {
            var keepForName = Math.Min(name.Length, Math.Max(4, maxWidth));
            var budget = Math.Max(0, maxWidth - keepForName - 1); // 1 for the leading "…"
            if (directory.Length > budget)
                directory = "…" + directory[^budget..];
            else
                directory = "…" + directory;
            name = name.Length > keepForName ? "…" + name[^(keepForName - 1)..] : name;
        }

        var dirMarkup = directory.Length == 0 ? string.Empty : $"[dim]{Markup.Escape(directory)}[/]";
        return dirMarkup + $"[bold]{Markup.Escape(name)}[/]";
    }

    /// <summary>Escapes and truncates arbitrary text to a visible width, adding an ellipsis when cut.</summary>
    public static string Clip(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var singleLine = text.Replace("\r\n", "\n").Split('\n')[0].TrimEnd();
        if (singleLine.Length > maxWidth)
            singleLine = singleLine[..Math.Max(1, maxWidth - 1)] + "…";
        return Markup.Escape(singleLine);
    }

    /// <summary>Reads a string argument from the structured tool-call payload.</summary>
    public static string? Argument(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value?.ToString() : null;
}
