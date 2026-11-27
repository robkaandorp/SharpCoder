namespace SharpCoder.Cli.Tests;

public class CliOptionsConfirmToolsTests
{
    [Fact]
    public void DefaultIsAuto()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.Equal(ToolConfirmationMode.Auto, options!.ConfirmTools);
    }

    [Theory]
    [InlineData("auto", ToolConfirmationMode.Auto)]
    [InlineData("always", ToolConfirmationMode.Always)]
    [InlineData("ask", ToolConfirmationMode.Always)]
    [InlineData("never", ToolConfirmationMode.Never)]
    [InlineData("off", ToolConfirmationMode.Never)]
    [InlineData("NEVER", ToolConfirmationMode.Never)]
    public void ConfirmToolsFlagParses(string value, ToolConfirmationMode expected)
    {
        var options = CliOptions.Parse(new[] { "run", "--confirm-tools", value, "task" });
        Assert.Equal(expected, options!.ConfirmTools);
    }

    [Theory]
    [InlineData("-y")]
    [InlineData("--yes")]
    public void YesAliasParsesAsNever(string flag)
    {
        var options = CliOptions.Parse(new[] { "run", flag, "task" });
        Assert.Equal(ToolConfirmationMode.Never, options!.ConfirmTools);
    }

    [Fact]
    public void ConfirmToolsFlagRejectsUnknownValue()
    {
        Assert.Throws<ArgumentException>(() =>
            CliOptions.Parse(new[] { "run", "--confirm-tools", "maybe", "task" }));
    }

    [Fact]
    public void HelpTextDocumentsConfirmationFlags()
    {
        Assert.Contains("--confirm-tools", CliOptions.HelpText);
        Assert.Contains("--yes", CliOptions.HelpText);
    }
}
