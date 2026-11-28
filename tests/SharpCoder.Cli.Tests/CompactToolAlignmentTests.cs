using SharpCoder;
using Spectre.Console.Testing;
using Xunit;

namespace SharpCoder.Cli.Tests;

/// <summary>
/// Compact tool-call lines are two columns: icon/label/subject flush left, metrics flush
/// right at the terminal margin. These tests pin that invariant down across widths and
/// tools, including the wide 🔧 emoji and the narrow-console degradation path.
/// </summary>
public class CompactToolAlignmentTests
{
    private static ToolCallUpdate Detail(
        string name,
        IReadOnlyDictionary<string, object?> args,
        ToolCallFacts? facts,
        int elapsedMs = 8,
        bool isError = false) => new()
    {
        Name = name,
        CallId = "call_1",
        Arguments = args,
        Result = "unused",
        IsError = isError,
        Elapsed = TimeSpan.FromMilliseconds(elapsedMs),
        Facts = facts,
    };

    private static string OnlyLine(TestConsole console) =>
        console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void ReadEditWrite_MetricsEndExactlyAtTheRightMargin(int width)
    {
        foreach (var (name, args, facts, expectedEnd) in new[]
        {
            ("read_file", new Dictionary<string, object?> { ["filePath"] = "src/SharpCoder.Cli/ConsoleUi.cs" },
                new ToolCallFacts { StartLine = 138, EndLine = 210, CharactersRead = 4182 }, "8ms"),
            ("edit_file", new Dictionary<string, object?> { ["filePath"] = "src/SharpCoder.Cli/Program.cs" },
                new ToolCallFacts { OccurrencesReplaced = 1, CharactersRemoved = 27, CharactersAdded = 41 }, "8ms"),
            ("write_file", new Dictionary<string, object?> { ["filePath"] = "CHANGELOG.md" },
                new ToolCallFacts { CharactersWritten = 2910 }, "8ms"),
        })
        {
            var console = TestConsoleFactory.Create(width: width);
            var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

            renderer.Finished(Detail(name, args, facts));

            var line = OnlyLine(console);
            Assert.EndsWith(expectedEnd, line);
            // Spectre's own cell width accounts for the emoji surrogate pair as two cells.
            Assert.Equal(width, ToolDisplayFormatters.CellWidth(line));
        }
    }

    [Fact]
    public void TwoCalls_AlignTheirMetricsAtTheSameColumn()
    {
        var console = TestConsoleFactory.Create(width: 100);
        var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

        renderer.Finished(Detail("read_file",
            new Dictionary<string, object?> { ["filePath"] = "a.cs" },
            new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 10 }, 1));
        renderer.Finished(Detail("write_file",
            new Dictionary<string, object?> { ["filePath"] = "a/very/long/path/to/some/other/File.cs" },
            new ToolCallFacts { CharactersWritten = 5000 }, 2));

        var lines = console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(lines[0].LastIndexOf("1ms"), lines[1].LastIndexOf("2ms"));
        Assert.Equal(100, ToolDisplayFormatters.CellWidth(lines[0]));
        Assert.Equal(100, ToolDisplayFormatters.CellWidth(lines[1]));
    }

    [Fact]
    public void SubjectAppearsBeforeTheGap_AndFilenameSurvives()
    {
        var console = TestConsoleFactory.Create(width: 80);
        var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

        renderer.Finished(Detail("read_file",
            new Dictionary<string, object?> { ["filePath"] = "src/SharpCoder.Cli/ConsoleUi.cs" },
            new ToolCallFacts { StartLine = 138, EndLine = 210, CharactersRead = 4182 }));

        var line = OnlyLine(console);
        var filenameAt = line.IndexOf("ConsoleUi.cs", StringComparison.Ordinal);
        var metricsAt = line.IndexOf("138–210", StringComparison.Ordinal);
        Assert.True(filenameAt >= 0, "filename missing");
        Assert.True(metricsAt > filenameAt, "metrics must be to the RIGHT of the filename");
        Assert.Contains("  ", line[filenameAt..metricsAt], StringComparison.Ordinal); // padding gap
    }

    [Fact]
    public void LongPath_ElidesFromTheLeftButKeepsTheFilename()
    {
        var console = TestConsoleFactory.Create(width: 60);
        var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

        renderer.Finished(Detail("read_file",
            new Dictionary<string, object?> { ["filePath"] = "an/extremely/long/directory/that/goes/on/and/on/src/ImportantFile.cs" },
            new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 20 }));

        var line = OnlyLine(console);
        Assert.Contains("…", line);
        Assert.Contains("ImportantFile.cs", line);
        Assert.Equal(60, ToolDisplayFormatters.CellWidth(line));
    }

    [Fact]
    public void NarrowConsole_DropsWholeMetricSegmentsRatherThanProducingGarbage()
    {
        var console = TestConsoleFactory.Create(width: 40);
        var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

        renderer.Finished(Detail("read_file",
            new Dictionary<string, object?> { ["filePath"] = "src/SharpCoder.Cli/ConsoleUi.cs" },
            new ToolCallFacts { StartLine = 138, EndLine = 210, CharactersRead = 4182 }));

        var line = OnlyLine(console);
        Assert.EndsWith("8ms", line);
        Assert.DoesNotContain("chars · 8ms", line); // whole segment dropped, not "…2 chars"
        Assert.DoesNotContain("…2 chars", line);
        Assert.Equal(40, ToolDisplayFormatters.CellWidth(line));
    }

    [Fact]
    public void Bash_TimeoutIsLeftAndElapsedExitAreRight()
    {
        var console = TestConsoleFactory.Create(width: 100);
        var renderer = new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false);

        renderer.Finished(Detail("execute_bash_command",
            new Dictionary<string, object?> { ["command"] = "dotnet build" },
            new ToolCallFacts { TimeoutMs = 120000, TimeoutIsDefault = true, ExitCode = 0 }, 1400));

        var lines = console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // summary + command
        var summary = lines[0];
        var timeoutAt = summary.IndexOf("2m (default) timeout", StringComparison.Ordinal);
        var elapsedAt = summary.IndexOf("1.4s", StringComparison.Ordinal);
        Assert.True(timeoutAt >= 0);
        Assert.True(elapsedAt > timeoutAt, "elapsed/exit must be right of the timeout");
        Assert.EndsWith("exit 0", summary);
        Assert.Equal(100, ToolDisplayFormatters.CellWidth(summary));
    }

    [Fact]
    public void GrepAndSkill_AlsoUseTheTwoColumnLayout()
    {
        foreach (var (name, args, facts, expectedEnd) in new[]
        {
            ("grep", new Dictionary<string, object?> { ["pattern"] = "FlushPending", ["include"] = "*.cs" },
                (ToolCallFacts?)new ToolCallFacts { MatchCount = 7, FileCount = 3 }, "8ms"),
            ("load_skill", new Dictionary<string, object?> { ["name"] = "build" },
                (ToolCallFacts?)new ToolCallFacts { SkillName = "build" }, "8ms"),
            ("glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" },
                (ToolCallFacts?)new ToolCallFacts { MatchCount = 12 }, "8ms"),
            ("list_skills", new Dictionary<string, object?>(),
                (ToolCallFacts?)new ToolCallFacts { SkillCount = 3 }, "8ms"),
        })
        {
            var console = TestConsoleFactory.Create(width: 80);
            new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false)
                .Finished(Detail(name, args, facts));

            var line = OnlyLine(console);
            Assert.EndsWith(expectedEnd, line);
            Assert.Equal(80, ToolDisplayFormatters.CellWidth(line));
        }
    }

    [Fact]
    public void ErrorGlyph_DoesNotBreakAlignment()
    {
        var console = TestConsoleFactory.Create(width: 80);
        new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false)
            .Finished(Detail("read_file",
                new Dictionary<string, object?> { ["filePath"] = "a.cs" },
                new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 10 },
                isError: true));

        var line = OnlyLine(console);
        Assert.Contains("✗", line);
        Assert.Equal(80, ToolDisplayFormatters.CellWidth(line));
    }

    [Fact]
    public void BracketsInFilename_AreEscapedAndAlignmentIsPreserved()
    {
        var console = TestConsoleFactory.Create(width: 80);
        new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: false)
            .Finished(Detail("read_file",
                new Dictionary<string, object?> { ["filePath"] = "src/[red]a.cs[/]" },
                new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 10 }));

        var line = OnlyLine(console);
        // The literal brackets survive as visible text; no red style is injected.
        Assert.Contains("[red]a.cs[/]", line);
        Assert.Equal(80, ToolDisplayFormatters.CellWidth(line));
    }

    [Fact]
    public void RawMode_StillWritesNothing()
    {
        var console = TestConsoleFactory.Create(width: 80);
        new StructuredToolCallRenderer(console, ToolDisplayMode.Compact, raw: true)
            .Finished(Detail("read_file",
                new Dictionary<string, object?> { ["filePath"] = "a.cs" },
                new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 10 }));

        Assert.Equal(string.Empty, console.Output);
    }
}
