using System.Text;

namespace SharpCoder.Cli;

/// <summary>
/// Splits a growing markdown buffer into complete top-level chunks. A chunk is only reported once
/// the block it contains is known to be finished — a trailing blank line, a closed code fence, or a
/// finalized single-line block — so an incomplete block never gets rendered prematurely. Rendering a
/// chunk is always safe regardless of what Markdig makes of it, because block boundaries in
/// CommonMark only ever move *earlier* with more input; never later.
/// </summary>
internal static class MarkdownBlockSplitter
{
    /// <summary>
    /// Finds the end offset (exclusive) of the first complete chunk in <paramref name="text"/>, including
    /// any blank lines that terminate it. Returns -1 when no complete chunk is available yet.
    /// </summary>
    public static int FindFirstCompleteBlock(string text)
    {
        if (string.IsNullOrEmpty(text))
            return -1;

        var lines = SplitLines(text, out var starts);
        if (lines.Count == 0)
            return -1;

        var i = 0;
        while (i < lines.Count && lines[i].Trim().Length == 0)
            i++; // leading blanks belong to this chunk

        if (i == lines.Count)
        {
            // Nothing but blank lines so far: hand them all over so the buffer does not grow unbounded.
            return text.Length;
        }

        var endLine = FindContentEnd(lines, i);
        if (endLine < 0)
            return -1;

        // Consume any blank lines that follow the block so they are not re-scanned next time.
        var j = endLine + 1;
        while (j < lines.Count && lines[j].Trim().Length == 0)
            j++;

        return LineEndWithNewline(text, starts, j - 1, lines[j - 1]);
    }

    private static int FindContentEnd(List<string> lines, int i)
    {
        var line = lines[i];
        var trimmed = line.TrimStart();

        // Fenced code block: complete only at the closing fence.
        if (TryOpenFence(trimmed, out var fenceChar, out var fenceLength))
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (IsClosingFence(lines[j].TrimStart(), fenceChar, fenceLength))
                    return j;
            }
            return -1;
        }

        // Indented code block.
        if (IsIndentedCodeLine(line))
        {
            var j = i + 1;
            while (j < lines.Count && (lines[j].Trim().Length == 0 || IsIndentedCodeLine(lines[j])))
                j++;
            if (j < lines.Count)
                return j - 1;
            return -1;
        }

        // Single-line blocks.
        if (IsAtxHeading(trimmed) || IsThematicBreak(trimmed))
            return i;

        // Setext heading: text line followed by an = or - underline.
        if (i + 1 < lines.Count && IsSetextUnderline(lines[i + 1].Trim()))
            return i + 1;

        // Everything else: scan ahead for the terminating blank line.
        for (var j = i + 1; j < lines.Count; j++)
        {
            if (lines[j].Trim().Length == 0)
                return j - 1;
        }
        return -1;
    }

    private static int LineEndWithNewline(string text, List<int> starts, int lineIndex, string line)
    {
        var end = starts[lineIndex] + line.Length;
        if (end < text.Length)
        {
            if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') return end + 2;
            if (text[end] == '\n' || text[end] == '\r') return end + 1;
        }
        return end;
    }

    private static List<string> SplitLines(string text, out List<int> starts)
    {
        var lines = new List<string>();
        starts = new List<int>();
        var i = 0;
        while (i < text.Length)
        {
            var nl = text.IndexOf('\n', i);
            int end, next;
            if (nl < 0)
            {
                end = text.Length;
                next = text.Length;
            }
            else
            {
                end = nl;
                if (end > i && text[end - 1] == '\r') end--;
                next = nl + 1;
            }
            starts.Add(i);
            lines.Add(text.Substring(i, end - i));
            i = next;
        }
        return lines;
    }

    private static bool TryOpenFence(string trimmed, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;
        if (trimmed.Length < 3) return false;
        var c = trimmed[0];
        if (c != '`' && c != '~') return false;
        var n = 0;
        while (n < trimmed.Length && trimmed[n] == c) n++;
        if (n < 3) return false;
        fenceChar = c;
        fenceLength = n;
        return true;
    }

    private static bool IsClosingFence(string trimmed, char fenceChar, int fenceLength)
    {
        if (trimmed.Length < fenceLength) return false;
        var n = 0;
        while (n < trimmed.Length && trimmed[n] == fenceChar) n++;
        if (n < fenceLength) return false;
        return trimmed[n..].Trim().Length == 0;
    }

    private static bool IsAtxHeading(string trimmed)
    {
        var i = 0;
        while (i < trimmed.Length && trimmed[i] == '#') i++;
        if (i == 0 || i > 6) return false;
        return i >= trimmed.Length || trimmed[i] == ' ' || trimmed[i] == '\t';
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char marker = '\0';
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == ' ' || c == '\t') continue;
            if (c != '-' && c != '*' && c != '_') return false;
            if (marker == '\0') marker = c;
            else if (c != marker) return false;
            count++;
        }
        return count >= 3;
    }

    private static bool IsSetextUnderline(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        if (trimmed.All(c => c == '=')) return true;
        if (trimmed.All(c => c == '-')) return true;
        return false;
    }

    private static bool IsIndentedCodeLine(string line)
        => line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal);
}
