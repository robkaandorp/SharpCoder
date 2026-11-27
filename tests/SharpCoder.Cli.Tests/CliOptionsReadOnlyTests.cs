namespace SharpCoder.Cli.Tests;

public class CliOptionsReadOnlyTests
{
    [Fact]
    public void DefaultIsNotReadOnly()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.False(options!.ReadOnly);
    }

    [Theory]
    [InlineData("--read-only")]
    [InlineData("--no-writes")]
    public void ReadOnlyFlagsSetReadOnly(string flag)
    {
        var options = CliOptions.Parse(new[] { "run", flag, "task" });
        Assert.True(options!.ReadOnly);
    }

    [Fact]
    public void ReadOnlyFlagIsIndependentOfBash()
    {
        var options = CliOptions.Parse(new[] { "run", "--bash", "--read-only", "task" });
        Assert.True(options!.ReadOnly);
        Assert.True(options.EnableBash);
    }

    [Fact]
    public void HelpTextDocumentsReadOnlyFlag()
    {
        Assert.Contains("--read-only", CliOptions.HelpText);
        Assert.Contains("--no-writes", CliOptions.HelpText);
    }
}
