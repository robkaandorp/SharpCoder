using Xunit;

namespace SharpCoder.Cli.Tests;

public class CompactSubjectTests
{
    [Fact]
    public void CellWidth_UsesSpectresCellAccounting()
    {
        Assert.Equal(0, ToolDisplayFormatters.CellWidth(string.Empty));
        Assert.Equal(5, ToolDisplayFormatters.CellWidth("abc  "));
        Assert.Equal(2, ToolDisplayFormatters.CellWidth("\uD83D\uDD27")); // wide emoji = 2 cells
        Assert.Equal(27, ToolDisplayFormatters.CellWidth("138–210 · 4,182 chars · 8ms"));
    }

    [Fact]
    public void Path_ClipsFromTheLeftKeepingTheFilename()
    {
        var (plain, markup) = CompactSubject.Path("a/very/long/directory/src/Important.cs").Clip(24);

        Assert.StartsWith("…", plain);
        Assert.EndsWith("Important.cs", plain);
        Assert.Equal(24, ToolDisplayFormatters.CellWidth(plain));
        Assert.Contains("[bold]Important.cs[/]", markup);
        Assert.Contains("[dim]", markup);
    }

    [Fact]
    public void Path_WithoutDirectoryKeepsOnlyTheFilename()
    {
        var (plain, markup) = CompactSubject.Path("CHANGELOG.md").Clip(40);

        Assert.Equal("CHANGELOG.md", plain);
        Assert.Equal("[bold]CHANGELOG.md[/]", markup);
    }

    [Fact]
    public void Bold_ClipsFromTheRight()
    {
        var (plain, markup) = CompactSubject.Bold("FlushPendingAndMore").Clip(10);

        Assert.EndsWith("…", plain);
        Assert.Equal(10, ToolDisplayFormatters.CellWidth(plain));
        Assert.StartsWith("[bold]", markup);
    }

    [Fact]
    public void Plain_EscapesBracketsSoPathsCannotInjectMarkup()
    {
        var (_, markup) = CompactSubject.Plain("src/[red]a.cs[/]").Clip(80);

        Assert.Contains("[[red]]", markup);
        Assert.DoesNotContain("[red]", markup.Replace("[[red]]", string.Empty));
    }

    [Fact]
    public void Clip_ZeroOrNegativeBudget_ReturnsEmptyNotAnException()
    {
        foreach (var budget in new[] { -5, 0 })
        {
            var (plain, markup) = CompactSubject.Path("src/a.cs").Clip(budget);
            Assert.Equal(string.Empty, plain);
            Assert.Equal(string.Empty, markup);
        }
    }

    [Fact]
    public void Clip_OneCellBudget_IsJustAnEllipsis()
    {
        var (plain, _) = CompactSubject.Path("src/very/long/path.cs").Clip(1);
        Assert.Equal("…", plain);
    }

    [Fact]
    public void ClipStart_KeepsTheTail()
    {
        Assert.Equal("8ms", ToolDisplayFormatters.ClipStart("138–210 · 4,182 chars · 8ms", 3));
        Assert.Equal("…8ms", ToolDisplayFormatters.ClipStart("138–210 · 4,182 chars · 8ms", 4));
        Assert.Equal("138–210 · 4,182 chars · 8ms",
            ToolDisplayFormatters.ClipStart("138–210 · 4,182 chars · 8ms", 80));
        Assert.Equal(string.Empty, ToolDisplayFormatters.ClipStart("abc", 0));
    }

    [Fact]
    public void Path_NormalizesWindowsSeparators()
    {
        var (plain, _) = CompactSubject.Path(@"src\SharpCoder.Cli\ConsoleUi.cs").Clip(80);
        Assert.Equal("src/SharpCoder.Cli/ConsoleUi.cs", plain);
    }
}
