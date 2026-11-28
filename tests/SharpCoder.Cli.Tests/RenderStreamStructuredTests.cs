using System.Runtime.CompilerServices;
using SharpCoder;
using Spectre.Console;
using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class RenderStreamStructuredTests
{
    private static async IAsyncEnumerable<StreamingUpdate> Script(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return StreamingUpdate.TextDelta("Before.\n");
        yield return StreamingUpdate.ToolStarted(new ToolCallUpdate
        {
            Name = "write_file",
            CallId = "c1",
            Arguments = new Dictionary<string, object?> { ["filePath"] = "a.cs" },
        });
        // Legacy narration (what CodingAgent still emits for other hosts).
        yield return StreamingUpdate.TextDelta("\n\n`🔧 write_file(filePath=\"a.cs\")`\n");
        yield return StreamingUpdate.ToolFinished(new ToolCallUpdate
        {
            Name = "write_file",
            CallId = "c1",
            Result = "wrote 3 lines\nsecond line",
            Elapsed = TimeSpan.FromMilliseconds(4),
        });
        yield return StreamingUpdate.TextDelta("> wrote 3 lines\n");
        yield return StreamingUpdate.RoundProgress(new RoundProgressUpdate
        {
            Round = 0, ToolCallsSoFar = 1, MaxSteps = 25,
        });
        yield return StreamingUpdate.TextDelta("After.\n");
        yield return StreamingUpdate.Completed(new AgentResult { Status = "Success", Message = "After." });
    }

    [Fact]
    public async Task RawMode_ContainsOnlyModelText()
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Capabilities.Interactive = false;
        var ui = new ConsoleUi { MarkdownMode = MarkdownMode.Raw, RenderConsole = console };

        var result = await ui.RenderStreamAsync(Script(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("Before.", console.Output);
        Assert.Contains("After.", console.Output);
        Assert.DoesNotContain("🔧", console.Output);
        Assert.DoesNotContain("> wrote", console.Output);
        Assert.DoesNotContain("round 1", console.Output);
    }

    [Fact]
    public async Task LiveMode_RendersStructuredLinesNotLegacyNarration()
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Capabilities.Interactive = true;
        var ui = new ConsoleUi { MarkdownMode = MarkdownMode.Live, RenderConsole = console };

        var result = await ui.RenderStreamAsync(Script(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("write_file", console.Output);
        Assert.Contains("filePath=a.cs", console.Output);
        Assert.Contains("wrote 3 lines", console.Output);
        Assert.Contains("round 1", console.Output);
        // The legacy narration must not be rendered twice.
        Assert.DoesNotContain("`🔧", console.Output);
        Assert.DoesNotContain("> wrote", console.Output);
    }

    [Fact]
    public async Task LiveMode_ProseBeforeToolCallIsRenderedBeforeTheToolLine()
    {
        // Regression: the markdown writer buffers prose until a complete block, while the
        // structured tool renderer writes immediately. Without an explicit flush the tool line
        // appeared BEFORE the reasoning that preceded it.
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Capabilities.Interactive = true;
        var ui = new ConsoleUi { MarkdownMode = MarkdownMode.Live, RenderConsole = console };

        await ui.RenderStreamAsync(Script(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var output = console.Output;
        var before = output.IndexOf("Before.", StringComparison.Ordinal);
        var toolLine = output.IndexOf("write_file(filePath=a.cs)", StringComparison.Ordinal);
        var after = output.IndexOf("After.", StringComparison.Ordinal);

        Assert.True(before >= 0, "preceding prose must be rendered");
        Assert.True(toolLine >= 0, "tool line must be rendered");
        Assert.True(after >= 0, "following prose must be rendered");
        Assert.True(before < toolLine,
            "reasoning before a tool call must appear before the tool line");
        Assert.True(toolLine < after,
            "reasoning after a tool call must appear after the tool line");
    }

    [Fact]
    public async Task VerboseMode_ShowsSecondResultLine()
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Capabilities.Interactive = true;
        var ui = new ConsoleUi
        {
            MarkdownMode = MarkdownMode.Live,
            VerboseToolResults = true,
            RenderConsole = console,
        };

        await ui.RenderStreamAsync(Script(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Contains("second line", console.Output);
    }
}
