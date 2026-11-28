using System.Text;

namespace SharpCoder.Providers.Web;

/// <summary>Bounded logical-line renderer. All field values have already been transformed.</summary>
internal static class WebToolRenderer
{
    internal sealed record SearchRow(string Title, string Url, string Content);

    internal static string Message(string value, int displayBytes) =>
        Render(Split(value), displayBytes, int.MaxValue, omission: false);

    internal static string Search(IReadOnlyList<SearchRow> rows, bool resultsOmitted, int displayBytes)
    {
        var lines = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i != 0) lines.Add(string.Empty);
            AddField(lines, WebToolDisplayLiterals.TitlePrefix, rows[i].Title);
            AddField(lines, WebToolDisplayLiterals.UrlPrefix, rows[i].Url);
            AddField(lines, WebToolDisplayLiterals.ContentPrefix, rows[i].Content);
        }
        return Render(lines, displayBytes, int.MaxValue, resultsOmitted);
    }

    internal static string Fetch(
        string title,
        string content,
        IReadOnlyList<string> links,
        int displayBytes,
        int maxLines)
    {
        var lines = new List<string>();
        AddField(lines, WebToolDisplayLiterals.TitlePrefix, title);
        AddField(lines, WebToolDisplayLiterals.ContentPrefix, content);
        if (links.Count != 0)
        {
            lines.Add(WebToolDisplayLiterals.LinksHeading);
            foreach (var link in links) lines.AddRange(Split(link));
        }
        return Render(lines, displayBytes, maxLines, omission: false);
    }

    private static void AddField(List<string> lines, string prefix, string value)
    {
        var split = Split(value);
        lines.Add(prefix + split[0]);
        for (var i = 1; i < split.Count; i++) lines.Add(split[i]);
    }

    private static List<string> Split(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static string Render(List<string> source, int displayBytes, int maxLines, bool omission)
    {
        // First try the complete source: an exact fit must not pay for an unused notice.
        if (Fits(source, [], displayBytes, maxLines) && !omission)
            return string.Join("\n", source);

        var retained = new List<string>();
        var bytes = 0;
        var truncated = false;
        foreach (var line in source)
        {
            var nextBytes = bytes + (retained.Count == 0 ? 0 : 1) + Utf8(line);
            var candidate = new List<string>(retained) { line };
            var reserved = new List<string>();
            if (omission) reserved.Add(WebToolDisplayLiterals.ResultsOmitted);
            if (omission) reserved.Add(string.Empty);
            reserved.Add(WebToolDisplayLiterals.Truncated);
            if (candidate.Count > maxLines || nextBytes > displayBytes || !Fits(candidate, reserved, displayBytes, maxLines))
            {
                truncated = true;
                break;
            }
            retained = candidate;
            bytes = nextBytes;
        }

        var suffix = new List<string>();
        if (omission) suffix.Add(WebToolDisplayLiterals.ResultsOmitted);
        if (omission && truncated) suffix.Add(string.Empty);
        if (truncated) suffix.Add(WebToolDisplayLiterals.Truncated);
        while (retained.Count != 0 && !Fits(retained, suffix, displayBytes, maxLines))
            RemoveLast(retained, ref bytes);
        if (retained.Count != 0) Add(retained, ref bytes, string.Empty);
        foreach (var line in suffix) Add(retained, ref bytes, line);
        return string.Join("\n", retained);
    }

    private static bool Fits(List<string> retained, List<string> suffix, int displayBytes, int maxLines)
    {
        var result = new List<string>(retained);
        if (result.Count != 0 && suffix.Count != 0) result.Add(string.Empty);
        result.AddRange(suffix);
        return result.Count <= maxLines && Utf8(string.Join("\n", result)) <= displayBytes;
    }

    private static void Add(List<string> lines, ref int bytes, string line)
    {
        bytes += (lines.Count == 0 ? 0 : 1) + Utf8(line);
        lines.Add(line);
    }

    private static void RemoveLast(List<string> lines, ref int bytes)
    {
        var index = lines.Count - 1;
        bytes -= Utf8(lines[index]) + (index == 0 ? 0 : 1);
        lines.RemoveAt(index);
    }

    private static int Utf8(string value) => Encoding.UTF8.GetByteCount(value);
}
