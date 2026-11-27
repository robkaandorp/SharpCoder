namespace SharpCoder.Cli.Tests;

public class CliOptionsEnvContextTests
{
    [Fact]
    public void DefaultHasEnvContextEnabled()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.False(options!.NoEnvContext);
    }

    [Fact]
    public void NoEnvContextFlagDisablesIt()
    {
        var options = CliOptions.Parse(new[] { "run", "--no-env-context", "task" });
        Assert.True(options!.NoEnvContext);
    }

    [Fact]
    public void HelpTextDocumentsNoEnvContextFlag()
    {
        Assert.Contains("--no-env-context", CliOptions.HelpText);
    }
}
