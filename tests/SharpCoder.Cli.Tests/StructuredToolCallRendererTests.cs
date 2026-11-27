using SharpCoder;
using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class StructuredToolCallRendererTests
{
    private static ToolCallUpdate Detail(string result = "first line\nsecond line", bool isError = false) => new()
    {
        Name = "write_file",
        CallId = "call_1",
        Arguments = new Dictionary<string, object?> { ["filePath"] = "src/a.cs" },
        Result = result,
        IsError = isError,
        Elapsed = TimeSpan.FromMilliseconds(12),
    };

    [Fact]
    public void Started_ShowsToolNameAndArguments()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: false, raw: false);

        renderer.Started(Detail());

        Assert.Contains("write_file", console.Output);
        Assert.Contains("filePath=src/a.cs", console.Output);
    }

    [Fact]
    public void Finished_NonVerbose_ShowsOnlyFirstLine()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: false, raw: false);

        renderer.Finished(Detail());

        Assert.Contains("first line", console.Output);
        Assert.DoesNotContain("second line", console.Output);
        Assert.Equal(1, renderer.CompletedCount);
    }

    [Fact]
    public void Finished_Verbose_ShowsFullResult()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: true, raw: false);

        renderer.Finished(Detail());

        Assert.Contains("first line", console.Output);
        Assert.Contains("second line", console.Output);
    }

    [Fact]
    public void Finished_LongResult_IsCappedWhenNotVerbose()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: false, raw: false);

        renderer.Finished(Detail(new string('x', 500)));

        Assert.DoesNotContain(new string('x', 500), console.Output);
        Assert.Contains("…", console.Output);
    }

    [Fact]
    public void Finished_Error_IsFlagged()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: false, raw: false);

        renderer.Finished(Detail("Error: something failed", isError: true));

        Assert.Contains("reported an error", console.Output);
    }

    [Fact]
    public void RawMode_WritesNothing()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: true, raw: true);

        renderer.Started(Detail());
        renderer.Finished(Detail());
        renderer.Progress(new RoundProgressUpdate { Round = 0, ToolCallsSoFar = 1, MaxSteps = 25 });

        Assert.Equal(string.Empty, console.Output);
    }

    [Fact]
    public void Progress_ShowsStepCount()
    {
        var console = TestConsoleFactory.Create();
        var renderer = new StructuredToolCallRenderer(console, verbose: false, raw: false);

        renderer.Progress(new RoundProgressUpdate { Round = 2, ToolCallsSoFar = 7, MaxSteps = 25 });

        Assert.Contains("round 3", console.Output);
        Assert.Contains("7/25", console.Output);
    }
}
