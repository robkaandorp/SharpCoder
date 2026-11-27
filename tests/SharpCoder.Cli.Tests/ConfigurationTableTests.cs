using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class ConfigurationTableTests
{
    private static string Render(bool readOnly)
    {
        var console = TestConsoleFactory.Create(width: 120);
        var ui = new ConsoleUi { RenderConsole = console };

        ui.Configuration(
            model: "copilot/test-model",
            workDirectory: "/repo",
            maxSteps: 25,
            enableBash: false,
            readOnly: readOnly,
            reasoning: null,
            contextTokens: 200_000,
            shell: SharpCoder.ShellPreference.Auto,
            savePath: null);

        return console.Output;
    }

    [Fact]
    public void Configuration_ReadOnlyDisabled_ReportsFileWritesEnabled()
    {
        var output = Render(readOnly: false);

        Assert.Contains("File writes", output);
        Assert.Contains("enabled", output);
        Assert.Contains("without confirmation", output);
    }

    [Fact]
    public void Configuration_ReadOnlyEnabled_ReportsFileWritesDisabled()
    {
        var output = Render(readOnly: true);

        Assert.Contains("File writes", output);
        Assert.Contains("disabled", output);
        Assert.Contains("--read-only", output);
    }
}
