using SharpCoder;
using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class StructuredToolCallRendererTests
{
    private static ToolCallUpdate Detail(
        string name = "read_file",
        IReadOnlyDictionary<string, object?>? arguments = null,
        ToolCallFacts? facts = null,
        string result = "first line\nsecond line",
        bool isError = false) => new()
    {
        Name = name,
        CallId = "call_1",
        Arguments = arguments ?? new Dictionary<string, object?>
        {
            ["filePath"] = "src/SharpCoder.Cli/ConsoleUi.cs",
            ["offset"] = 10,
            ["limit"] = 20,
        },
        Result = result,
        IsError = isError,
        Elapsed = TimeSpan.FromMilliseconds(12),
        Facts = facts,
    };

    private static StructuredToolCallRenderer Renderer(TestConsole console, ToolDisplayMode mode, bool raw = false) =>
        new(console, mode, raw);

    // ---------------------------------------------------------------- compact mode

    [Fact]
    public void Compact_Started_WritesNothing()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);

        renderer.Started(Detail());

        Assert.Equal(string.Empty, console.Output);
    }

    [Fact]
    public void Compact_Progress_WritesNothing()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);

        renderer.Progress(new RoundProgressUpdate { Round = 2, ToolCallsSoFar = 7, MaxSteps = 25 });

        Assert.Equal(string.Empty, console.Output);
    }

    [Fact]
    public void Compact_ReadFile_UsesStructuredFactsNotResultText()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);
        var facts = new ToolCallFacts { StartLine = 10, EndLine = 29, CharactersRead = 4182 };

        renderer.Finished(Detail(facts: facts, result: "1: alpha\n2: beta"));

        Assert.Contains("Read", console.Output);
        Assert.Contains("10–29", console.Output);
        Assert.Contains("4,182 chars", console.Output);
        Assert.Contains("ConsoleUi.cs", console.Output); // filename emphasis kept
        Assert.DoesNotContain("alpha", console.Output); // result text is never scraped
        Assert.Equal(1, renderer.CompletedCount);
    }

    [Fact]
    public void Compact_EditFile_ShowsOccurrencesAndCharacterDelta()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);
        var facts = new ToolCallFacts { OccurrencesReplaced = 1, CharactersRemoved = 27, CharactersAdded = 41 };

        renderer.Finished(Detail(name: "edit_file", facts: facts));

        Assert.Contains("Edit", console.Output);
        Assert.Contains("1×", console.Output);
        Assert.Contains("−27", console.Output);
        Assert.Contains("+41", console.Output);
    }

    [Fact]
    public void Compact_WriteFile_ShowsCharacterCount()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);

        renderer.Finished(Detail(name: "write_file",
            facts: new ToolCallFacts { CharactersWritten = 1842, CharactersAdded = 1842 }));

        Assert.Contains("Write", console.Output);
        Assert.Contains("1,842 chars", console.Output);
    }

    [Fact]
    public void Compact_LoadSkill_IsOneShortLine()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);
        var args = new Dictionary<string, object?> { ["name"] = "build" };

        renderer.Finished(Detail(name: "load_skill", arguments: args,
            facts: new ToolCallFacts { SkillName = "build" }));

        Assert.Contains("Skill", console.Output);
        Assert.Contains("build", console.Output);
        Assert.Single(console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Compact_Bash_ShowsDefaultTimeoutElapsedAndCommandOnTwoLines()
    {
        var console = TestConsoleFactory.Create(width: 120);
        var renderer = Renderer(console, ToolDisplayMode.Compact);
        var args = new Dictionary<string, object?> { ["command"] = "dotnet build SharpCoder.slnx" };
        var facts = new ToolCallFacts { TimeoutMs = 120000, TimeoutIsDefault = true, ExitCode = 0, TimedOut = false };

        renderer.Finished(Detail(name: "execute_bash_command", arguments: args, facts: facts));

        Assert.Contains("Bash", console.Output);
        Assert.Contains("2m (default) timeout", console.Output);
        Assert.Contains("12ms", console.Output);
        Assert.Contains("exit 0", console.Output);
        Assert.Contains("dotnet build", console.Output);
    }

    [Fact]
    public void Compact_Bash_ExplicitTimeoutIsNotLabelledDefault()
    {
        var console = TestConsoleFactory.Create(width: 120);
        var renderer = Renderer(console, ToolDisplayMode.Compact);
        var args = new Dictionary<string, object?> { ["command"] = "echo ok", ["timeout_ms"] = 5000 };
        var facts = new ToolCallFacts { TimeoutMs = 5000, TimeoutIsDefault = false, ExitCode = 0, TimedOut = false };

        renderer.Finished(Detail(name: "execute_bash_command", arguments: args, facts: facts));

        Assert.Contains("5s timeout", console.Output);
        Assert.DoesNotContain("(default)", console.Output);
    }

    [Fact]
    public void Compact_GlobAndGrep_ShowStructuredCounts()
    {
        var globConsole = TestConsoleFactory.Create();
        Renderer(globConsole, ToolDisplayMode.Compact).Finished(Detail(name: "glob",
            arguments: new Dictionary<string, object?> { ["pattern"] = "**/*.cs" },
            facts: new ToolCallFacts { MatchCount = 100 }));
        Assert.Contains("Glob", globConsole.Output);
        Assert.Contains("100 matches", globConsole.Output);

        var grepConsole = TestConsoleFactory.Create();
        Renderer(grepConsole, ToolDisplayMode.Compact).Finished(Detail(name: "grep",
            arguments: new Dictionary<string, object?> { ["pattern"] = "FlushPending", ["include"] = "*.cs" },
            facts: new ToolCallFacts { MatchCount = 7, FileCount = 3 }));
        Assert.Contains("Grep", grepConsole.Output);
        Assert.Contains("7 matches", grepConsole.Output);
        Assert.Contains("3 files", grepConsole.Output);
    }

    [Fact]
    public void Compact_Error_IsFlagged()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Compact);

        renderer.Finished(Detail(isError: true, result: "Error: something failed"));

        Assert.Contains("✗", console.Output);
        Assert.DoesNotContain("something failed", console.Output);
    }

    [Fact]
    public void RawMode_WritesNothingInEitherMode()
    {
        foreach (var mode in new[] { ToolDisplayMode.Compact, ToolDisplayMode.Verbose })
        {
            var console = TestConsoleFactory.Create();
            var renderer = Renderer(console, mode, raw: true);
            renderer.Started(Detail());
            renderer.Finished(Detail());
            renderer.Progress(new RoundProgressUpdate { Round = 0, ToolCallsSoFar = 1, MaxSteps = 25 });
            Assert.Equal(string.Empty, console.Output);
        }
    }

    // ---------------------------------------------------------------- verbose mode

    [Fact]
    public void Verbose_ShowsFullResultAndArguments()
    {
        var console = TestConsoleFactory.Create(width: 120);
        var renderer = Renderer(console, ToolDisplayMode.Verbose);

        renderer.Started(Detail());
        renderer.Finished(Detail(facts: new ToolCallFacts { StartLine = 1, EndLine = 2, CharactersRead = 20 }));

        Assert.Contains("running", console.Output);
        Assert.Contains("ARGUMENTS", console.Output);
        Assert.Contains("RESULT", console.Output);
        Assert.Contains("first line", console.Output);
        Assert.Contains("second line", console.Output);
        Assert.Contains("lines 1–2", console.Output);
    }

    [Fact]
    public void Verbose_Progress_ShowsStepCount()
    {
        var console = TestConsoleFactory.Create();
        var renderer = Renderer(console, ToolDisplayMode.Verbose);

        renderer.Progress(new RoundProgressUpdate { Round = 2, ToolCallsSoFar = 7, MaxSteps = 25 });

        Assert.Contains("round 3", console.Output);
        Assert.Contains("7/25", console.Output);
    }

    [Fact]
    public void Verbose_Error_ShowsErrorInPanelHeader()
    {
        var console = TestConsoleFactory.Create(width: 120);
        var renderer = Renderer(console, ToolDisplayMode.Verbose);

        renderer.Finished(Detail(isError: true));

        Assert.Contains("✗ error", console.Output);
    }
}
