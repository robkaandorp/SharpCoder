using System.Reflection;
using SharpCoder;
using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

/// <summary>
/// Regression tests for the tool-confirmation prompt: Spectre markup must be parsed by the
/// console, never printed as literal "[dim]"/"[green]" text.
/// </summary>
public class SpectreToolConfirmationPrompterTests
{
    private static ToolAuthorizationContext Context(params (string Name, object? Value)[] args)
    {
        var ctor = typeof(ToolAuthorizationContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(IReadOnlyDictionary<string, object?>) },
            modifiers: null)!;

        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
            dictionary[name] = value;

        return (ToolAuthorizationContext)ctor.Invoke(new object?[]
        {
            "write_file", "test-correlation", dictionary,
        });
    }

    [Fact]
    public void Ask_DoesNotPrintLiteralSpectreMarkupTags()
    {
        var console = TestConsoleFactory.Create();
        var prompter = new SpectreToolConfirmationPrompter(console, _ => (char?)null);
        // Console.ReadKey is redirected in the test host, so the answer fails closed to "denied"
        // without blocking; that is exactly the branch we want to inspect.
        var answer = prompter.Ask(
            Context(("filePath", "src/a.cs"), ("content", "hello")),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolConfirmationAnswer.No, answer);
        Assert.DoesNotContain("[dim]", console.Output);
        Assert.DoesNotContain("[green]", console.Output);
        Assert.DoesNotContain("[red]", console.Output);
        Assert.DoesNotContain("[bold]", console.Output);
        Assert.DoesNotContain("[yellow]", console.Output);
        Assert.Contains("denied", console.Output);
    }

    [Fact]
    public void Ask_ShowsToolNameAndEscapedArguments()
    {
        var console = TestConsoleFactory.Create();
        var prompter = new SpectreToolConfirmationPrompter(console, _ => (char?)null);

        prompter.Ask(Context(("filePath", "src/[red]a.cs[/]")), TestContext.Current.CancellationToken);

        Assert.Contains("tool authorization: write_file", console.Output);
        Assert.Contains("filePath:", console.Output);
        // Markup.Escape doubles the brackets and MarkupLine parses them back to literal
        // characters, so the argument must be visible as the original verbatim value.
        // If it were left unescaped, "[red]" would be consumed as a style tag; if the
        // line were written with WriteLine, the doubled brackets would show instead.
        Assert.Contains("src/[red]a.cs[/]", console.Output);
        Assert.DoesNotContain("[[", console.Output);
    }
}
