using Xunit;

namespace SharpCoder.Cli.Tests;

public class MarkdownRendererStackSafetyTests
{
    // Regression for a fatal StackOverflowException that killed the CLI process. Markdig builds
    // a deeply nested inline tree for degenerate emphasis runs, and AppendInline <-> AppendInlines
    // used to recurse without a bound. The runtime cannot catch StackOverflowException, so the
    // whole process aborted (Exit 134 / EEPolicy::HandleFatalStackOverflow).

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void RenderMarkdown_WithDeeplyNestedEmphasis_DoesNotOverflowTheStack(int depth)
    {
        var console = TestConsoleFactory.Create(width: 120);
        var text = new string('*', depth) + "inner" + new string('*', depth);

        Assert.Null(Record.Exception(() => MarkdownRenderer.RenderMarkdown(console, text)));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    public void RenderMarkdown_WithDeeplyNestedBrackets_DoesNotOverflowTheStack(int depth)
    {
        var console = TestConsoleFactory.Create(width: 120);
        var text = new string('[', depth) + "inner" + new string(']', depth);

        Assert.Null(Record.Exception(() => MarkdownRenderer.RenderMarkdown(console, text)));
    }

    [Fact]
    public void RenderMarkdown_WithDeeplyNestedQuotes_FailsGracefullyNotFatally()
    {
        var console = TestConsoleFactory.Create(width: 120);
        var text = string.Concat(Enumerable.Repeat("> ", 200)) + "deep";

        // Markdig itself rejects pathological block nesting with a *catchable* ArgumentException.
        // What matters is that we never reach an uncatchable StackOverflowException.
        var ex = Record.Exception(() => MarkdownRenderer.RenderMarkdown(console, text));
        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("too deeply nested", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
