namespace SharpCoder.Cli.Tests;

public class CliOptionsMarkdownTests
{
    [Fact]
    public void DefaultMarkdownModeIsAuto()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.Equal(MarkdownMode.Auto, options!.Markdown);
    }

    [Theory]
    [InlineData("auto", MarkdownMode.Auto)]
    [InlineData("live", MarkdownMode.Live)]
    [InlineData("full", MarkdownMode.Full)]
    [InlineData("raw", MarkdownMode.Raw)]
    [InlineData("off", MarkdownMode.Raw)]
    [InlineData("none", MarkdownMode.Raw)]
    [InlineData("LIVE", MarkdownMode.Live)]
    public void MarkdownFlagParses(string value, MarkdownMode expected)
    {
        var options = CliOptions.Parse(new[] { "run", "--markdown", value, "task" });
        Assert.Equal(expected, options!.Markdown);
    }

    [Fact]
    public void MarkdownFlagRejectsUnknownValue()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "run", "--markdown", "fancy", "task" }));
    }
}
