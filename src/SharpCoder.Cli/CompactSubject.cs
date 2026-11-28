using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// The left-hand subject of a compact tool-call line: a filename, search pattern or skill
/// name. Carries the plain text separately from the markup so the renderer can measure the
/// exact visible width before padding the right-hand column. <see cref="Clip"/> elides from
/// the left for paths (so the filename stays visible) and from the right for everything else.
/// </summary>
internal readonly struct CompactSubject
{
    private readonly string _plain;
    private readonly SubjectStyle _style;

    private CompactSubject(string plain, SubjectStyle style)
    {
        _plain = plain.Replace('\\', '/').Replace("\r\n", "\n").Split('\n')[0].TrimEnd();
        _style = style;
    }

    public static CompactSubject Path(string? path) => new(path ?? "(no path)", SubjectStyle.Path);

    public static CompactSubject Bold(string text) => new(text, SubjectStyle.Bold);

    public static CompactSubject Plain(string text) => new(text, SubjectStyle.Plain);

    /// <summary>Clips to <paramref name="maxWidth"/> visible cells and returns both forms.</summary>
    public (string Plain, string Markup) Clip(int maxWidth)
    {
        var plain = ClipPlain(_plain, maxWidth);
        return (plain, ToMarkup(plain));
    }

    /// <summary>Renders an already-clipped plain string with this subject's emphasis.</summary>
    private string ToMarkup(string plain)
    {
        if (plain.Length == 0) return string.Empty;

        switch (_style)
        {
            case SubjectStyle.Bold:
                return $"[bold]{Markup.Escape(plain)}[/]";
            case SubjectStyle.Path:
                // Always keep the filename emphasised, even after left-elision: split the
                // clipped text at its last slash and dim only the directory portion.
                var slash = plain.LastIndexOf('/');
                var directory = slash >= 0 ? plain[..(slash + 1)] : string.Empty;
                var name = slash >= 0 ? plain[(slash + 1)..] : plain;
                return (directory.Length == 0 ? string.Empty : $"[dim]{Markup.Escape(directory)}[/]")
                    + $"[bold]{Markup.Escape(name)}[/]";
            default:
                return Markup.Escape(plain);
        }
    }

    private static string ClipPlain(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return string.Empty;
        if (maxWidth == 1) return "…";
        if (ToolDisplayFormatters.CellWidth(text) <= maxWidth) return text;

        // Paths elide from the LEFT with a leading ellipsis (keep the filename); everything
        // else elides from the right. Both operate on whole characters so a surrogate pair
        // is never split.
        if (text.Contains('/'))
        {
            var tail = text;
            while (tail.Length > 0 && ToolDisplayFormatters.CellWidth("…" + tail) > maxWidth)
                tail = tail[1..];
            return tail.Length == 0 ? "…" : "…" + tail;
        }

        var cut = text;
        while (cut.Length > 0 && ToolDisplayFormatters.CellWidth(cut + "…") > maxWidth)
            cut = cut[..^1];
        return cut.Length == 0 ? "…" : cut + "…";
    }

    private enum SubjectStyle { Plain, Bold, Path }
}
