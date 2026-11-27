namespace SharpCoder.Cli.Tests;

public class CliOptionsShellTests
{
    [Fact]
    public void DefaultShellIsAuto()
    {
        var options = CliOptions.Parse(new[] { "run", "task" });
        Assert.Equal(SharpCoder.ShellPreference.Auto, options!.Shell);
    }

    [Theory]
    [InlineData("auto", SharpCoder.ShellPreference.Auto)]
    [InlineData("pwsh", SharpCoder.ShellPreference.Pwsh)]
    [InlineData("powershell", SharpCoder.ShellPreference.PowerShell)]
    [InlineData("cmd", SharpCoder.ShellPreference.Cmd)]
    [InlineData("bash", SharpCoder.ShellPreference.Bash)]
    [InlineData("cmd.exe", SharpCoder.ShellPreference.Cmd)]
    [InlineData("PWSH", SharpCoder.ShellPreference.Pwsh)]
    public void ShellFlagParses(string value, SharpCoder.ShellPreference expected)
    {
        var options = CliOptions.Parse(new[] { "run", "--bash", "--shell", value, "task" });
        Assert.Equal(expected, options!.Shell);
    }

    [Fact]
    public void ShellFlagRejectsUnknownValue()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "run", "--shell", "zsh", "task" }));
    }
}
