namespace SharpCoder.Cli.Tests;

public class MarkdownBlockSplitterTests
{
    [Fact]
    public void EmptyInputProducesNoChunk()
    {
        Assert.Equal(-1, MarkdownBlockSplitter.FindFirstCompleteBlock(""));
    }

    [Fact]
    public void SingleParagraphWithoutTrailingBlankLineIsIncomplete()
    {
        Assert.Equal(-1, MarkdownBlockSplitter.FindFirstCompleteBlock("hello world"));
    }

    [Fact]
    public void ParagraphFollowedByBlankLineIsComplete()
    {
        const string text = "hello world\n\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(text.Length, end);
    }

    [Fact]
    public void ParagraphFollowedByAnotherParagraphCompletesAtBlankLine()
    {
        const string text = "first para\n\nsecond para\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("first para\n\n".Length, end);
    }

    [Fact]
    public void HeadingIsSingleLineBlock()
    {
        const string text = "# Title\nrest";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("# Title\n".Length, end);
    }

    [Fact]
    public void AtxHeadingAtEndWithoutNewlineIsComplete()
    {
        const string text = "# Title";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(text.Length, end);
    }

    [Fact]
    public void UnclosedFenceIsNotComplete()
    {
        const string text = "```csharp\nvar x = 1;\n";
        Assert.Equal(-1, MarkdownBlockSplitter.FindFirstCompleteBlock(text));
    }

    [Fact]
    public void ClosedFenceIsComplete()
    {
        const string text = "```csharp\nvar x = 1;\n```\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(text.Length, end);
    }

    [Fact]
    public void FenceContentWithMarkdownIsNotSplit()
    {
        const string text = "```\n# not a heading\n\njust text\n```\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(text.Length, end);
    }

    [Fact]
    public void ListFollowedByBlankLineIsComplete()
    {
        const string text = "- one\n- two\n\nafter\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("- one\n- two\n\n".Length, end);
    }

    [Fact]
    public void TableFollowedByBlankLineIsComplete()
    {
        const string text = "| a | b |\n|---|---|\n| 1 | 2 |\n\nafter\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |\n\n".Length, end);
    }

    [Fact]
    public void BlockquoteFollowedByBlankLineIsComplete()
    {
        const string text = "> quoted\n> line two\n\nafter\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("> quoted\n> line two\n\n".Length, end);
    }

    [Fact]
    public void SetextHeadingIsCompleteAfterUnderline()
    {
        const string text = "Title\n=====\nbody\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal("Title\n=====\n".Length, end);
    }

    [Fact]
    public void ThematicBreakIsSingleLine()
    {
        const string text = "---\nafter";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(4, end);
    }

    [Fact]
    public void LeadingBlankLinesAreIncludedInTheChunk()
    {
        const string text = "\n\npara\n\n";
        var end = MarkdownBlockSplitter.FindFirstCompleteBlock(text);
        Assert.Equal(text.Length, end);
    }
}
