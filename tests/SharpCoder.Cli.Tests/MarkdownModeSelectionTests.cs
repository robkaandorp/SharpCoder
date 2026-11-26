using Spectre.Console;
using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class LiveModeAutoDetectionTests
{
    [Fact]
    public void AutoSelectsRawWhenConsoleIsNotInteractive()
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        // TestConsole is not interactive by default.
        Assert.False(console.Profile.Capabilities.Interactive);

        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Auto);
        Assert.Equal(MarkdownMode.Raw, writer.EffectiveMode);
    }

    [Fact]
    public void ExplicitLiveModeIsHonouredEvenOnNonInteractiveConsole()
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        var writer = new StreamingMarkdownWriter(console, MarkdownMode.Live);
        Assert.Equal(MarkdownMode.Live, writer.EffectiveMode);
    }
}
