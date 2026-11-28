using System.Globalization;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>Pure formatting helpers for compact tool-call lines. No console I/O, so they are unit-testable.</summary>
internal static class ToolDisplayFormatters
{
    /// <summary>
    /// Formats a duration in the most compact human-readable form: <c>820ms</c>, <c>1.4s</c>,
    /// <c>2m3s</c>, <c>1h5m</c>, <c>25h</c>. Zero-valued units are omitted entirely, so
    /// <c>1 minute</c> is <c>1m</c> rather than <c>1m00s</c>, and there are no spaces or
    /// zero-padding. Culture-invariant so the fixed-width CLI lines do not change with locale.
    /// </summary>
    public static string Duration(TimeSpan value)
    {
        // Negative / sub-millisecond durations (possible for a not-yet-measured call) clamp to 0ms.
        if (value.TotalMilliseconds < 1)
            return "0ms";

        // Sub-second: whole milliseconds, no separator.
        if (value.TotalMilliseconds < 1000)
            return $"{(long)value.TotalMilliseconds}ms";

        // Round to the ONE decimal we show, and derive the unit choice and every component
        // from that single value. This prevents boundary bleed: 59.96s becomes "1m", never
        // "60s" / "60.0s", and no rounded-up display ever disagrees with the chosen unit.
        var shownSeconds = Math.Round(value.TotalSeconds, 1, MidpointRounding.AwayFromZero);
        if (shownSeconds < 60)
        {
            var seconds = shownSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            return seconds.EndsWith(".0", StringComparison.Ordinal)
                ? seconds.Substring(0, seconds.Length - 2) + "s"
                : seconds + "s";
        }

        var total = (long)Math.Round(shownSeconds, MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;

        if (hours > 0)
            return NonZeroUnits(("h", hours), ("m", minutes));
        return NonZeroUnits(("m", minutes), ("s", secs));
    }

    /// <summary>
    /// Joins only the non-zero (unit, value) pairs with no separator, e.g. <c>1h5m</c>, <c>25h</c>,
    /// <c>2m3s</c>. The first unit is always emitted (it is non-zero by construction).
    /// </summary>
    private static string NonZeroUnits((string Unit, long Value) first, (string Unit, long Value) second) =>
        second.Value == 0
            ? $"{first.Value}{first.Unit}"
            : $"{first.Value}{first.Unit}{second.Value}{second.Unit}";

    /// <summary>Formats a millisecond timeout in the same compact style as <see cref="Duration"/>.</summary>
    public static string Timeout(long milliseconds) => Duration(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Escapes and truncates arbitrary text to a visible width, adding an ellipsis when cut.</summary>
    public static string Clip(string? text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var singleLine = text.Replace("\r\n", "\n").Split('\n')[0].TrimEnd();
        if (singleLine.Length > maxWidth)
            singleLine = singleLine[..Math.Max(1, maxWidth - 1)] + "…";
        return Markup.Escape(singleLine);
    }

    /// <summary>
    /// Clips plain text from the LEFT, keeping the tail (used for right-aligned metrics on
    /// very narrow consoles so the elapsed time stays flush right). Adds a leading ellipsis.
    /// </summary>
    public static string ClipStart(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return string.Empty;
        if (CellWidth(text) <= maxWidth) return text;

        // Prefer a clean tail with no ellipsis when the budget is very small: "8ms" is
        // far more useful than "…ms". For larger budgets keep the leading ellipsis so
        // the reader can tell content was dropped.
        if (maxWidth <= 3)
        {
            var shortTail = text;
            while (shortTail.Length > 0 && CellWidth(shortTail) > maxWidth)
                shortTail = shortTail[1..];
            return shortTail;
        }

        var tail = text;
        while (tail.Length > 0 && CellWidth("…" + tail) > maxWidth)
            tail = tail[1..];
        return tail.Length == 0 ? "…" : "…" + tail;
    }

    /// <summary>
    /// Visible cell width of plain (already-de-markup'd) text, using Spectre's own cell-width
    /// table so wide characters such as the 🔧 emoji are counted correctly. Public via
    /// <c>CharExtensions.GetCellWidth</c>; surrogate halves each count as one, which is exactly
    /// how Spectre measured them when rendering (verified against <c>TestConsole.Profile.Width</c>).
    /// </summary>
    public static int CellWidth(string text)
    {
        var width = 0;
        foreach (var c in text)
            width += c.GetCellWidth();
        return width;
    }

    /// <summary>Reads a string argument from the structured tool-call payload.</summary>
    public static string? Argument(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value?.ToString() : null;
}
