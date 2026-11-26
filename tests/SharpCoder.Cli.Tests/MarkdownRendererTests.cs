namespace SharpCoder.Cli.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void PlainParagraphIsRenderedAsText()
    {
        var output = TestConsoleFactory.Render("Hello, world.");
        Assert.Contains("Hello, world.", output);
    }

    [Fact]
    public void HeadingRendersText()
    {
        var output = TestConsoleFactory.Render("# Title\n\nBody text.");
        Assert.Contains("Title", output);
        Assert.Contains("Body text.", output);
        Assert.DoesNotContain("#", output);
    }

    [Fact]
    public void BoldAndItalicMarkersAreNotPrinted()
    {
        var output = TestConsoleFactory.Render("some **bold** and *italic* text");
        Assert.Contains("bold", output);
        Assert.Contains("italic", output);
        Assert.DoesNotContain("**", output);
    }

    [Fact]
    public void FencedCodeBlockRendersWithoutFence()
    {
        var output = TestConsoleFactory.Render("```csharp\nvar x = 1;\n```");
        Assert.Contains("var x = 1;", output);
        Assert.DoesNotContain("```", output);
        Assert.Contains("csharp", output);
    }

    [Fact]
    public void ListItemsRenderWithBullets()
    {
        var output = TestConsoleFactory.Render("- one\n- two\n- three");
        Assert.Contains("one", output);
        Assert.Contains("two", output);
        Assert.Contains("three", output);
    }

    [Fact]
    public void OrderedListRendersNumbers()
    {
        var output = TestConsoleFactory.Render("1. first\n2. second");
        Assert.Contains("1.", output);
        Assert.Contains("second", output);
    }

    [Fact]
    public void TableRendersAllCells()
    {
        var output = TestConsoleFactory.Render("| Name | Value |\n|---|---|\n| alpha | 1 |\n| beta | 2 |");
        Assert.Contains("Name", output);
        Assert.Contains("alpha", output);
        Assert.Contains("beta", output);
        Assert.Contains("1", output);
        Assert.Contains("2", output);
    }

    [Fact]
    public void BlockquoteRendersContent()
    {
        var output = TestConsoleFactory.Render("> quoted line");
        Assert.Contains("quoted line", output);
    }

    [Fact]
    public void LinkRendersLabel()
    {
        var output = TestConsoleFactory.Render("see [the docs](https://example.com) now");
        Assert.Contains("the docs", output);
    }

    [Fact]
    public void InlineCodeRendersContent()
    {
        var output = TestConsoleFactory.Render("run `dotnet test` now");
        Assert.Contains("dotnet test", output);
        Assert.DoesNotContain("`dotnet", output);
    }

    [Fact]
    public void ToolCallAnnotationRendersAsInlineCode()
    {
        var output = TestConsoleFactory.Render("`🔧 read_file(path=\"a.cs\")`\n\n> first line of output\n");
        Assert.Contains("read_file", output);
        Assert.Contains("first line of output", output);
    }

    [Fact]
    public void TaskListRendersCheckbox()
    {
        var output = TestConsoleFactory.Render("- [ ] todo\n- [x] done");
        Assert.Contains("todo", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void StrikethroughRenders()
    {
        var output = TestConsoleFactory.Render("this is ~~gone~~ text");
        Assert.Contains("gone", output);
        Assert.DoesNotContain("~~", output);
    }

    [Fact]
    public void HorizontalRuleRenders()
    {
        var output = TestConsoleFactory.Render("above\n\n---\n\nbelow");
        Assert.Contains("above", output);
        Assert.Contains("below", output);
    }

    [Fact]
    public void NestedListRenders()
    {
        var output = TestConsoleFactory.Render("- outer\n  - inner\n");
        Assert.Contains("outer", output);
        Assert.Contains("inner", output);
    }

    [Fact]
    public void EmptyInputProducesNoOutput()
    {
        Assert.Equal(string.Empty, TestConsoleFactory.Render(""));
        Assert.Equal(string.Empty, TestConsoleFactory.Render("   \n\n"));
    }

    [Fact]
    public void HtmlIsNotExecuted()
    {
        var output = TestConsoleFactory.Render("<script>alert(1)</script>");
        Assert.Contains("script", output);
    }
}
