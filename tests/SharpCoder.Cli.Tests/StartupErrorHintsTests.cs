namespace SharpCoder.Cli.Tests;

public class StartupErrorHintsTests
{
    [Theory]
    [InlineData("OLLAMA_API_KEY is required for ollama-cloud provider", "OLLAMA_API_KEY")]
    [InlineData("GH_TOKEN or GITHUB_TOKEN is required for github provider", "GH_TOKEN")]
    [InlineData("PowerShell 7+ (pwsh) was requested but was not found on PATH.", "--shell auto")]
    [InlineData("ShellPreference.Bash was requested but no bash was found on PATH.", "--shell auto")]
    [InlineData("ShellPreference.Auto could not find a usable shell: no 'bash' on PATH", "--shell auto")]
    [InlineData("Unknown LLM provider: 'nope'", "LLM_PROVIDER")]
    [InlineData("WorkDirectory does not exist: /no/such/dir", "--workdir")]
    public void For_KnownFailure_ReturnsActionableHint(string message, string expectedFragment)
    {
        var hint = StartupErrorHints.For(message);

        Assert.NotNull(hint);
        Assert.Contains(expectedFragment, hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("connection reset by peer")]
    [InlineData("HTTP 500 from provider")]
    [InlineData("bash: line 1: dotnet: command not found")]
    [InlineData("The path /tmp/foo does not exist")]
    public void For_UnknownOrEmptyMessage_ReturnsNull(string message)
    {
        Assert.Null(StartupErrorHints.For(message));
    }

    [Fact]
    public void For_IsCaseInsensitive()
    {
        Assert.Contains("--shell auto", StartupErrorHints.For("PWSh was not found")!);
        Assert.Contains("OLLAMA_API_KEY", StartupErrorHints.For("ollama_api_key is missing")!);
    }
}
