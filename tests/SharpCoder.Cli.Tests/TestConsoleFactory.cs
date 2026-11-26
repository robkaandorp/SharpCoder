using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

internal static class TestConsoleFactory
{
    /// <summary>Creates a non-interactive test console with a fixed width that records output.</summary>
    public static TestConsole Create(int width = 80)
    {
        var console = new TestConsole { EmitAnsiSequences = false };
        console.Profile.Width = width;
        return console;
    }

    /// <summary>Renders markdown to the recorded text output using the production render path.</summary>
    public static string Render(string markdown, int width = 80)
    {
        var console = Create(width);
        MarkdownRenderer.RenderMarkdown(console, markdown);
        return console.Output;
    }
}
