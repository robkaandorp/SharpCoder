namespace SharpCoder.Cli.Tests;

public class StreamingMarkdownWriterTests
{
    [Fact]
    public void RawModeWritesBytesVerbatim()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Raw);
        writer.Append("# hi\n\n");
        writer.Append("**bold**");
        writer.Flush();
        Assert.Equal("# hi\n\n**bold**", console.Output);
    }

    [Fact]
    public void RawModeIsUsedWhenOutputIsRedirectedAndModeIsAuto()
    {
        var console = TestConsoleFactory.Create(); // TestConsole is not interactive
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Auto);
        Assert.Equal(MarkdownMode.Raw, writer.EffectiveMode);
    }

    [Fact]
    public void LiveModeRendersBlocksIncrementally()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        // Feed character by character; each complete block must be rendered as it completes.
        foreach (var ch in "# Title\n\npara text\n\n")
            writer.Append(ch.ToString());
        writer.Flush();

        var output = console.Output;
        Assert.Contains("Title", output);
        Assert.Contains("para text", output);
        Assert.DoesNotContain("#", output);
    }

    [Fact]
    public void LiveModeWithholdsIncompleteBlockUntilComplete()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        writer.Append("just a partial paragraph ");
        Assert.Equal(string.Empty, console.Output);
        writer.Append("with no blank line yet");
        Assert.Equal(string.Empty, console.Output);
        writer.Append("\n\n");
        Assert.Contains("just a partial paragraph with no blank line yet", console.Output);
    }

    [Fact]
    public void FullModeBuffersEverythingUntilFlush()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Full);
        writer.Append("# Title\n\nbody text\n\n");
        Assert.Equal(string.Empty, console.Output);
        writer.Flush();
        Assert.Contains("Title", console.Output);
        Assert.Contains("body text", console.Output);
    }

    [Fact]
    public void LiveModeHandlesFencedCodeSplitAcrossAppends()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        var chunks = new[] { "```csh", "arp\nvar x", " = 1;\n", "```\n\n" };
        foreach (var chunk in chunks)
            writer.Append(chunk);
        var output = console.Output;
        Assert.Contains("var x = 1;", output);
        Assert.DoesNotContain("```", output);
    }

    [Fact]
    public void AnyContentTracksInput()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        Assert.False(writer.AnyContent);
        writer.Append("x");
        Assert.True(writer.AnyContent);
    }

    [Fact]
    public void EmptyAppendDoesNotSetAnyContent()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        writer.Append(string.Empty);
        Assert.False(writer.AnyContent);
    }

    [Fact]
    public void ToolAnnotationLinesAreRenderedNotHidden()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        writer.Append("before\n\n`🔧 read_file(path=\"a.cs\")`\n\n> 42 lines\n\nafter\n\n");
        writer.Flush();
        var output = console.Output;
        Assert.Contains("read_file", output);
        Assert.Contains("42 lines", output);
        Assert.Contains("before", output);
        Assert.Contains("after", output);
    }
}
