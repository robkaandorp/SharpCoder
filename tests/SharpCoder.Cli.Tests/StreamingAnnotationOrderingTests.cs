using Spectre.Console;

namespace SharpCoder.Cli.Tests;

public class StreamingOrderingTests
{
    [Fact]
    public void AnnotationsAndProseKeepStreamOrderInFullMode()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Full);

        writer.Append("intro line\n\n");
        writer.Append("`🔧 read_file(path=\"a.cs\")`\n");
        writer.Append("> 3 lines\n");
        writer.Append("after tool\n");
        writer.Flush();

        var output = console.Output;
        var intro = output.IndexOf("intro line", StringComparison.Ordinal);
        var call = output.IndexOf("read_file", StringComparison.Ordinal);
        var result = output.IndexOf("3 lines", StringComparison.Ordinal);
        var after = output.IndexOf("after tool", StringComparison.Ordinal);

        Assert.True(intro >= 0 && call > intro && result > call && after > result,
            $"Expected intro < call < result < after. Output:{Environment.NewLine}{output}");
    }

    [Fact]
    public void AnnotationsAndProseKeepStreamOrderInLiveMode()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);

        writer.Append("intro line\n\n");
        writer.Append("`🔧 read_file(path=\"a.cs\")`\n");
        writer.Append("> 3 lines\n");
        writer.Append("after tool\n\n");
        writer.Flush();

        var output = console.Output;
        var intro = output.IndexOf("intro line", StringComparison.Ordinal);
        var call = output.IndexOf("read_file", StringComparison.Ordinal);
        var result = output.IndexOf("3 lines", StringComparison.Ordinal);
        var after = output.IndexOf("after tool", StringComparison.Ordinal);

        Assert.True(intro >= 0 && call > intro && result > call && after > result,
            $"Expected intro < call < result < after. Output:{Environment.NewLine}{output}");
    }

    [Fact]
    public void ToolResultLineIsNotTreatedAsMarkdownBlockquote()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        writer.Append("`🔧 read_file(path=\"a.cs\")`\n");
        writer.Append("> summary line\n");
        writer.Append("follow-up paragraph\n\n");
        writer.Flush();

        var output = console.Output;
        // The result annotation must not cause the follow-up paragraph to be swallowed into a blockquote.
        Assert.Contains("follow-up paragraph", output);
        Assert.Contains("summary line", output);
    }

    [Fact]
    public void SplitAnnotationAcrossAppendsStillDetected()
    {
        var console = TestConsoleFactory.Create();
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        writer.Append("intro\n\n`🔧 read_fil");
        writer.Append("e(path=\"a.cs\")`\n> 3 lines\nbody\n\n");
        writer.Flush();

        var output = console.Output;
        Assert.Contains("read_file", output);
        Assert.Contains("3 lines", output);
        Assert.Contains("body", output);
    }
}
