using System.Text;
using Spectre.Console;

namespace SharpCoder.Cli;

/// <summary>
/// Streaming renderer for one agent turn. Assistant markdown is buffered and rendered as complete
/// top-level blocks as they finish. The narration the agent injects for each tool call — a
/// <c>`🔧 name(args)`</c> line and, optionally, a <c>&gt; result</c> line right after it — is lifted
/// out of the markdown stream and rendered as a dim annotation, so the result line can never
/// swallow the following prose into a blockquote.
/// </summary>
internal sealed class StreamingMarkdownWriter
{
    private readonly IAnsiConsole _console;
    private readonly MarkdownMode _mode;

    /// <summary>Text received so far that has not yet been classified into complete lines.</summary>
    private readonly StringBuilder _pending = new();

    /// <summary>Complete markdown lines waiting for enough context to render them as blocks.</summary>
    private readonly StringBuilder _markdown = new();

    private bool _wroteAnything;
    private bool _expectingToolResult;

    public StreamingMarkdownWriter(IAnsiConsole console, MarkdownMode mode)
    {
        _console = console;
        _mode = mode == MarkdownMode.Auto
            ? (console.Profile.Capabilities.Interactive ? MarkdownMode.Live : MarkdownMode.Raw)
            : mode;
    }

    /// <summary>Effective mode after resolving <see cref="MarkdownMode.Auto"/>.</summary>
    public MarkdownMode EffectiveMode => _mode;

    /// <summary>True once any text has been appended this turn.</summary>
    public bool AnyContent { get; private set; }

    /// <summary>Feeds a raw text delta from the agent stream.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        AnyContent = true;

        if (_mode == MarkdownMode.Raw)
        {
            _console.Write(text);
            return;
        }

        _pending.Append(text);
        if (_mode == MarkdownMode.Live)
            DrainLines(flushAll: false);
    }

    /// <summary>Flushes everything still buffered. Call at the end of the turn.</summary>
    public void Flush()
    {
        DrainLines(flushAll: true);
        FlushMarkdown();
    }

    /// <summary>
    /// Flushes all prose received so far without ending the turn. Call before writing any
    /// out-of-band annotation (for example a structured tool-call line) so the annotation
    /// appears <em>after</em> the text that preceded it rather than before it.
    /// </summary>
    public void FlushPending()
    {
        DrainLines(flushAll: true);
        FlushMarkdown();
    }

    private void DrainLines(bool flushAll)
    {
        while (true)
        {
            var newline = IndexOfNewline(_pending);
            if (newline < 0)
            {
                if (flushAll && _pending.Length > 0)
                {
                    var tail = _pending.ToString();
                    _pending.Clear();
                    ProcessLine(tail);
                }
                return;
            }

            var line = _pending.ToString(0, newline + NewlineLength(_pending, newline));
            _pending.Remove(0, line.Length);
            ProcessLine(line);
        }
    }

    private void ProcessLine(string line)
    {
        if (IsToolCallLine(line))
        {
            // Keep stream order: prose buffered before the call must be rendered first.
            FlushMarkdown();
            WriteAnnotation(line);
            _expectingToolResult = true;
            return;
        }

        if (_expectingToolResult)
        {
            _expectingToolResult = false;
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                WriteAnnotation(line);
                return;
            }
            // Not a result line after all — fall through and treat it as markdown.
        }

        _markdown.Append(line);
        if (_mode == MarkdownMode.Live)
            FlushCompleteBlocks();
    }

    private void FlushCompleteBlocks()
    {
        while (true)
        {
            var text = _markdown.ToString();
            var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
            if (end <= 0)
                return;
            var chunk = text[..end];
            _markdown.Remove(0, end);
            WriteMarkdown(chunk);
        }
    }

    private void FlushMarkdown()
    {
        var markdown = _markdown.ToString();
        _markdown.Clear();
        WriteMarkdown(markdown);
    }

    private void WriteMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        foreach (var block in Markdig.Markdown.Parse(markdown, MarkdownRenderer.Pipeline))
        {
            var renderable = MarkdownRenderer.RenderBlock(block, 0);
            if (renderable is null)
                continue;
            Gap();
            _console.Write(renderable);
            _console.WriteLine();
            _wroteAnything = true;
        }
    }

    private void WriteAnnotation(string line)
    {
        var text = line.TrimEnd('\r', '\n').Trim();
        if (text.Length == 0)
            return;
        Gap();
        if (text.Length >= 2 && text[0] == '`' && text[^1] == '`')
            _console.MarkupLine($"[grey]  {Markup.Escape(text[1..^1])}[/]");
        else if (text.StartsWith("> ", StringComparison.Ordinal))
            _console.MarkupLine($"[grey]  › {Markup.Escape(text[2..])}[/]");
        else
            _console.MarkupLine($"[grey]  {Markup.Escape(text)}[/]");
        _wroteAnything = true;
    }

    /// <summary>Writes one blank line between consecutive blocks, but never before the first.</summary>
    private void Gap()
    {
        if (_wroteAnything)
            _console.WriteLine();
    }

    private static bool IsToolCallLine(string line)
    {
        var t = line.TrimEnd('\r', '\n');
        if (t.Length < 5 || !t.StartsWith("`🔧 ", StringComparison.Ordinal))
            return false;
        return t.EndsWith('`') && t.IndexOf('(') > 4;
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n' || sb[i] == '\r')
                return i;
        }
        return -1;
    }

    private static int NewlineLength(StringBuilder sb, int index)
    {
        if (sb[index] == '\r' && index + 1 < sb.Length && sb[index + 1] == '\n')
            return 2;
        return 1;
    }
}
