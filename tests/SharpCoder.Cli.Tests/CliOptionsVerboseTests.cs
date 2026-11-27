namespace SharpCoder.Cli.Tests;

public class CliOptionsVerboseTests
{
    [Fact]
    public void DefaultIsNotVerbose()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.False(options!.VerboseToolResults);
    }

    [Fact]
    public void VerboseFlagEnablesIt()
    {
        var options = CliOptions.Parse(new[] { "run", "--verbose", "task" });
        Assert.True(options!.VerboseToolResults);
    }

    [Fact]
    public void HelpTextAndReplHelpMentionVerbose()
    {
        Assert.Contains("--verbose", CliOptions.HelpText);
    }
}
