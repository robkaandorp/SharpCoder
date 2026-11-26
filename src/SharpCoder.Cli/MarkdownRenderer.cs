using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SharpCoder.Cli;

/// <summary>
/// Renders CommonMark/GFM markdown to Spectre renderables. The same code path is used for the
/// streaming (block-at-a-time) and one-shot rendering modes.
/// </summary>
internal static class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly Style DimGrey = new(Color.Grey);
    private static readonly Style CodeStyle = new(Color.Yellow);
    private static readonly Style LinkStyle = new(Color.Cyan1, decoration: Decoration.Underline);
    private static readonly Style Heading1 = new(Color.Cyan1, decoration: Decoration.Bold | Decoration.Underline);
    private static readonly Style Heading2 = new(Color.Cyan1, decoration: Decoration.Bold);
    private static readonly Style Heading3 = new(Color.White, decoration: Decoration.Bold);
    private static readonly Style HeadingOther = new(Color.Grey85, decoration: Decoration.Bold);
    private static readonly Style HeaderCellStyle = new(Color.Cyan1, decoration: Decoration.Bold);

    /// <summary>Parses <paramref name="markdown"/> and writes every top-level block to <paramref name="console"/>.</summary>
    public static void RenderMarkdown(IAnsiConsole console, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        var first = true;
        foreach (var block in Markdown.Parse(markdown, Pipeline))
        {
            var renderable = RenderBlock(block, 0);
            if (renderable is null)
                continue;
            if (!first)
                console.WriteLine();
            first = false;
            console.Write(renderable);
            console.WriteLine();
        }
    }

    internal static IRenderable? RenderBlock(Block block, int depth) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderParagraph(paragraph.Inline),
        FencedCodeBlock fenced => RenderCodeBlock(fenced.Lines.Lines.Select(l => l.ToString()), fenced.Info),
        CodeBlock indented => RenderCodeBlock(indented.Lines.Lines.Select(l => l.ToString()), null),
        QuoteBlock quote => RenderQuote(quote, depth),
        ListBlock list => RenderList(list, depth),
        Markdig.Extensions.Tables.Table table => RenderTable(table),
        ThematicBreakBlock => new Rule { Style = new Style(Color.Grey) },
        HtmlBlock html => new Text(string.Join('\n', html.Lines.Lines.Select(l => l.ToString())), DimGrey),
        LinkReferenceDefinitionGroup => null,
        _ => PlainFallback(block),
    };

    private static IRenderable? PlainFallback(Block block)
    {
        var plain = PlainText(block);
        return plain.Length == 0 ? null : new Text(plain);
    }

    // ── Blocks ─────────────────────────────────────────────────────────────

    private static IRenderable RenderHeading(HeadingBlock block)
    {
        var style = block.Level switch
        {
            1 => Heading1,
            2 => Heading2,
            3 => Heading3,
            _ => HeadingOther,
        };
        var paragraph = new Paragraph();
        AppendInlines(paragraph, block.Inline, new InlineStyle(style));
        return paragraph;
    }

    private static IRenderable RenderParagraph(ContainerInline? inline)
    {
        var paragraph = new Paragraph();
        AppendInlines(paragraph, inline, InlineStyle.None);
        return paragraph;
    }

    private static IRenderable RenderCodeBlock(IEnumerable<string> lines, string? info)
    {
        var language = info?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var content = lines.Select(l => (IRenderable)new Text(l, new Style(Color.Green))).ToList();
        if (content.Count == 0)
            content.Add(new Text(string.Empty));

        return new Panel(new Rows(content))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Header = language is { Length: > 0 } ? new PanelHeader(language, Justify.Right) : null,
            Padding = new Padding(1, 0),
            Expand = true,
        };
    }

    private static IRenderable RenderQuote(QuoteBlock quote, int depth)
    {
        var inner = RenderChildren(quote, depth + 1);
        if (inner.Count == 0)
            inner.Add(new Text(string.Empty));
        var bar = new Segment("│ ", new Style(Color.Grey));
        return new Prefixed(new Rows(inner), bar, bar.Clone());
    }

    private static IRenderable RenderList(ListBlock list, int depth)
    {
        var ordered = list.IsOrdered;
        var ordinal = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var rows = new List<IRenderable>();

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.Descendants<TaskList>().FirstOrDefault();
            string marker;
            Style markerStyle;
            if (task is not null)
            {
                marker = task.Checked ? "[x]" : "[ ]";
                markerStyle = new Style(task.Checked ? Color.Green : Color.Yellow, decoration: Decoration.Bold);
            }
            else if (ordered)
            {
                marker = ordinal + ".";
                markerStyle = new Style(Color.Yellow, decoration: Decoration.Bold);
            }
            else
            {
                marker = depth switch { 0 => "•", 1 => "◦", _ => "▪" };
                markerStyle = new Style(Color.Cyan1, decoration: Decoration.Bold);
            }

            var body = RenderChildren(item, depth + 1);
            if (body.Count == 0)
                body.Add(new Text(string.Empty));
            var indent = new string(' ', marker.Length + 1);
            rows.Add(new Prefixed(new Rows(body), new Segment(marker + " ", markerStyle), new Segment(indent)));
            ordinal++;
        }

        var rendered = new Rows(rows);
        return depth > 0 ? new Padder(rendered, new Padding(2, 0, 0, 0)) : rendered;
    }

    private static List<IRenderable> RenderChildren(ContainerBlock container, int depth)
    {
        var result = new List<IRenderable>();
        foreach (var child in container)
        {
            var rendered = RenderBlock(child, depth);
            if (rendered is not null)
                result.Add(rendered);
        }
        return result;
    }

    private static IRenderable RenderTable(Markdig.Extensions.Tables.Table table)
    {
        var result = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Expand = true,
            // Markdown tables carry their own header row; Spectre would otherwise add an empty one.
            ShowHeaders = false,
        };

        var columnCount = Math.Max(
            table.ColumnDefinitions.Count,
            table.OfType<Markdig.Extensions.Tables.TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max());

        for (var i = 0; i < columnCount; i++)
        {
            var definition = i < table.ColumnDefinitions.Count ? table.ColumnDefinitions[i] : null;
            result.AddColumn(new TableColumn(string.Empty)
            {
                Alignment = definition?.Alignment switch
                {
                    Markdig.Extensions.Tables.TableColumnAlign.Center => Justify.Center,
                    Markdig.Extensions.Tables.TableColumnAlign.Right => Justify.Right,
                    _ => Justify.Left,
                },
            });
        }

        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var cells = new List<IRenderable>();
            for (var i = 0; i < columnCount; i++)
            {
                var cell = i < row.Count ? row[i] as Markdig.Extensions.Tables.TableCell : null;
                cells.Add(cell is null ? new Text(string.Empty) : RenderCell(cell, row.IsHeader));
            }
            result.AddRow(cells.ToArray());
        }

        return result;
    }

    private static IRenderable RenderCell(Markdig.Extensions.Tables.TableCell cell, bool isHeader)
    {
        var parts = new List<IRenderable>();
        foreach (var child in cell)
        {
            if (child is ParagraphBlock paragraph)
            {
                var p = new Paragraph();
                var style = isHeader ? new InlineStyle(HeaderCellStyle) : InlineStyle.None;
                AppendInlines(p, paragraph.Inline, style);
                parts.Add(p);
            }
            else
            {
                var rendered = RenderBlock(child, 1);
                if (rendered is not null)
                    parts.Add(rendered);
            }
        }

        return parts.Count switch
        {
            0 => new Text(string.Empty),
            1 => parts[0],
            _ => new Rows(parts),
        };
    }

    // ── Inlines ─────────────────────────────────────────────────────────────

    private readonly struct InlineStyle
    {
        public static InlineStyle None => new(null);
        public InlineStyle(Style? style) => Style = style;
        public Style? Style { get; }
    }

    private static void AppendInlines(Paragraph paragraph, ContainerInline? container, InlineStyle baseStyle)
    {
        if (container is null) return;
        foreach (var inline in container)
            AppendInline(paragraph, inline, baseStyle);
    }

    private static void AppendInline(Paragraph paragraph, Inline inline, InlineStyle baseStyle)
    {
        switch (inline)
        {
            case LiteralInline literal:
                paragraph.Append(literal.Content.ToString(), baseStyle.Style);
                break;

            case CodeInline code:
                paragraph.Append(code.Content, Merge(baseStyle.Style, CodeStyle));
                break;

            case EmphasisInline emphasis:
                var style = Merge(baseStyle.Style, EmphasisStyle(emphasis));
                AppendInlines(paragraph, emphasis, new InlineStyle(style));
                break;

            case LinkInline { IsImage: false } link:
                paragraph.Append(PlainText(link), Merge(baseStyle.Style, LinkStyle), new Link(link.Url ?? string.Empty));
                break;

            case LinkInline { IsImage: true } image:
                paragraph.Append("[image: ", Merge(baseStyle.Style, DimGrey));
                paragraph.Append(PlainText(image), Merge(baseStyle.Style, new Style(Color.Grey, decoration: Decoration.Italic)));
                paragraph.Append("]", Merge(baseStyle.Style, DimGrey));
                break;

            case AutolinkInline autolink:
                paragraph.Append(autolink.Url, Merge(baseStyle.Style, LinkStyle), new Link(autolink.Url));
                break;

            case HtmlInline html:
                paragraph.Append(html.Tag, Merge(baseStyle.Style, new Style(Color.Grey, decoration: Decoration.Dim)));
                break;

            case LineBreakInline lineBreak:
                paragraph.Append(lineBreak.IsHard ? "\n" : " ", baseStyle.Style);
                break;

            case TaskList:
                break; // handled by the list renderer

            case ContainerInline nested:
                AppendInlines(paragraph, nested, baseStyle);
                break;

            default:
                var text = PlainInline(inline);
                if (text.Length > 0)
                    paragraph.Append(text, baseStyle.Style);
                break;
        }
    }

    private static Style EmphasisStyle(EmphasisInline emphasis)
    {
        var decoration = Decoration.None;
        var ch = emphasis.DelimiterChar;
        var count = emphasis.DelimiterCount;
        if (ch == '~')
        {
            if (count >= 2) decoration |= Decoration.Strikethrough;
        }
        else if (ch is '*' or '_')
        {
            if (count >= 2) decoration |= Decoration.Bold;
            if (count % 2 == 1) decoration |= Decoration.Italic;
        }
        return new Style(decoration: decoration);
    }

    private static Style? Merge(Style? existing, Style? additional)
    {
        if (existing is null) return additional;
        if (additional is null) return existing;
        var a = existing.Value;
        var b = additional.Value;
        var foreground = b.Foreground == Color.Default ? a.Foreground : b.Foreground;
        var background = b.Background == Color.Default ? a.Background : b.Background;
        return new Style(foreground, background, a.Decoration | b.Decoration);
    }

    private static string PlainInline(Inline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        AutolinkInline auto => auto.Url,
        HtmlInline html => html.Tag,
        _ => string.Empty,
    };

    private static string PlainText(ContainerInline? container)
    {
        var sb = new StringBuilder();
        AppendPlain(sb, container);
        return sb.ToString();
    }

    private static void AppendPlain(StringBuilder sb, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case ContainerInline nested:
                    AppendPlain(sb, nested);
                    break;
                case LineBreakInline lineBreak:
                    sb.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;
                case TaskList:
                    break;
                default:
                    sb.Append(PlainInline(inline));
                    break;
            }
        }
    }

    private static string PlainText(Block block)
    {
        var sb = new StringBuilder();
        switch (block)
        {
            case LeafBlock { Inline: { } inline }:
                sb.Append(PlainText(inline));
                break;
            case LeafBlock leaf:
                foreach (var line in leaf.Lines.Lines)
                    sb.AppendLine(line.ToString());
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    var text = PlainText(child);
                    if (text.Length == 0) continue;
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(text);
                }
                break;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Prefixes the first rendered line of a renderable with one segment and continuation lines with another.
/// Used for list markers and blockquote bars.
/// </summary>
internal sealed class Prefixed : Renderable
{
    private readonly IRenderable _child;
    private readonly Segment _first;
    private readonly Segment _continuation;

    public Prefixed(IRenderable child, Segment first, Segment continuation)
    {
        _child = child;
        _first = first;
        _continuation = continuation;
    }

    protected override Measurement Measure(RenderOptions options, int maxWidth)
    {
        var indent = Math.Max(_first.CellCount(), _continuation.CellCount());
        var inner = _child.Measure(options, Math.Max(1, maxWidth - indent));
        return new Measurement(inner.Min + indent, inner.Max + indent);
    }

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var indent = Math.Max(_first.CellCount(), _continuation.CellCount());
        var width = Math.Max(1, maxWidth - indent);
        var lines = Segment.SplitLines(_child.Render(options, width));
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            yield return (i == 0 ? _first : _continuation).Clone();
            foreach (var segment in line)
                yield return segment;
            if (i < lines.Count - 1)
                yield return Segment.LineBreak;
        }
    }
}
