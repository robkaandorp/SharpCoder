using SharpCoder.Providers;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Tests for the first-non-whitespace token selection in
/// <see cref="ChatClientFactory"/>: the Copilot resolver
/// (<see cref="ChatClientFactory.ResolveCopilotToken"/>), the GitHub-env resolver
/// (<see cref="ChatClientFactory.ResolveGitHubEnvToken"/>), and the public availability API
/// (<see cref="ChatClientFactory.IsTokenAvailable"/>).
/// </summary>
/// <remarks>
/// <para>
/// These tests mutate the process-wide <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment variables
/// and the static <c>_tokenProvider</c> field, so they join the serialized
/// <c>EnvVarMutation</c> collection (<c>DisableParallelization = true</c>, see
/// <see cref="EnvVarMutationCollection"/>). The constructor captures the original environment
/// values and the prior <c>_tokenProvider</c>; <see cref="Dispose"/> restores both, so no test's
/// mutation can spill into another.
/// </para>
/// <para>
/// The resolvers are <c>internal static</c> and reachable through the assembly's
/// <c>InternalsVisibleTo</c> grant, so each scenario asserts the resolver output directly and
/// also asserts <see cref="ChatClientFactory.IsTokenAvailable"/> for the same scenario — proving
/// the resolver and the public API agree.
/// </para>
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class TokenResolutionTests : IDisposable
{
    private readonly string? _origGhToken;
    private readonly string? _origGithubToken;
    private readonly Func<string?>? _origTokenProvider;

    public TokenResolutionTests()
    {
        _origGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _origGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _origTokenProvider = GetTokenProviderForTest();

        // Start every test from a clean slate: no stored token, no env tokens.
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        ChatClientFactory.SetTokenProvider(() => null!);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _origGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _origGithubToken);
        SetTokenProviderForTest(_origTokenProvider);
    }

    /// <summary>
    /// Reads the static <c>_tokenProvider</c> field. There is no public getter, so this is the
    /// only way to capture the value installed before the test ran, to restore it afterwards.
    /// </summary>
    private static Func<string?>? GetTokenProviderForTest()
    {
        var field = typeof(ChatClientFactory).GetField(
            "_tokenProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<string?>?)field?.GetValue(null);
    }

    /// <summary>Restores the static <c>_tokenProvider</c> field to a captured prior value.</summary>
    private static void SetTokenProviderForTest(Func<string?>? provider)
    {
        var field = typeof(ChatClientFactory).GetField(
            "_tokenProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field?.SetValue(null, provider);
    }

    // ── Copilot resolver: first-non-whitespace precedence at every level ─────

    /// <summary>A valid stored OAuth token beats valid env vars.</summary>
    [Fact]
    public void ResolveCopilotToken_StoredTokenBeatsEnvVars()
    {
        ChatClientFactory.SetTokenProvider(() => "stored-oauth-token");
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-token");

        Assert.Equal("stored-oauth-token", ChatClientFactory.ResolveCopilotToken());
        Assert.True(ChatClientFactory.IsTokenAvailable());
    }

    /// <summary>A whitespace stored token is treated as absent; a valid GH_TOKEN wins.</summary>
    [Fact]
    public void ResolveCopilotToken_WhitespaceStoredToken_GH_TOKENWins()
    {
        ChatClientFactory.SetTokenProvider(() => "   ");
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-token");

        Assert.Equal("gh-token", ChatClientFactory.ResolveCopilotToken());
        Assert.True(ChatClientFactory.IsTokenAvailable());
    }

    /// <summary>A whitespace GH_TOKEN is treated as absent; a valid GITHUB_TOKEN wins.</summary>
    [Fact]
    public void ResolveCopilotToken_WhitespaceGH_TOKEN_GITHUB_TOKENWins()
    {
        ChatClientFactory.SetTokenProvider(() => null!);
        Environment.SetEnvironmentVariable("GH_TOKEN", " \t ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-token");

        Assert.Equal("github-token", ChatClientFactory.ResolveCopilotToken());
        Assert.True(ChatClientFactory.IsTokenAvailable());
    }

    /// <summary>A whitespace GITHUB_TOKEN alone means no token is available.</summary>
    [Fact]
    public void ResolveCopilotToken_WhitespaceGITHUB_TOKENAlone_Unavailable()
    {
        ChatClientFactory.SetTokenProvider(() => null!);
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "   ");

        Assert.Null(ChatClientFactory.ResolveCopilotToken());
        Assert.False(ChatClientFactory.IsTokenAvailable());
    }

    /// <summary>All sources absent → no token.</summary>
    [Fact]
    public void ResolveCopilotToken_AllAbsent_ReturnsNull()
    {
        ChatClientFactory.SetTokenProvider(() => null!);
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        Assert.Null(ChatClientFactory.ResolveCopilotToken());
        Assert.False(ChatClientFactory.IsTokenAvailable());
    }

    /// <summary>Whitespace at every level, including the stored token → no token.</summary>
    [Fact]
    public void ResolveCopilotToken_AllWhitespace_ReturnsNull()
    {
        ChatClientFactory.SetTokenProvider(() => " \n ");
        Environment.SetEnvironmentVariable("GH_TOKEN", " ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "\t");

        Assert.Null(ChatClientFactory.ResolveCopilotToken());
        Assert.False(ChatClientFactory.IsTokenAvailable());
    }

    // ── GitHub resolver: env-only and whitespace-aware ──────────────────────────

    /// <summary>GH_TOKEN beats GITHUB_TOKEN when both are valid.</summary>
    [Fact]
    public void ResolveGitHubEnvToken_GH_TOKENBeatsGITHUB_TOKEN()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-token");

        Assert.Equal("gh-token", ChatClientFactory.ResolveGitHubEnvToken());
    }

    /// <summary>Whitespace GH_TOKEN is treated as absent; a valid GITHUB_TOKEN wins.</summary>
    [Fact]
    public void ResolveGitHubEnvToken_WhitespaceGH_TOKEN_GITHUB_TOKENWins()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "   ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-token");

        Assert.Equal("github-token", ChatClientFactory.ResolveGitHubEnvToken());
    }

    /// <summary>The GitHub resolver ignores the stored OAuth token entirely.</summary>
    [Fact]
    public void ResolveGitHubEnvToken_IgnoresStoredOAuthToken()
    {
        ChatClientFactory.SetTokenProvider(() => "stored-oauth-token");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        Assert.Null(ChatClientFactory.ResolveGitHubEnvToken());
    }

    /// <summary>Whitespace GITHUB_TOKEN alone → no token.</summary>
    [Fact]
    public void ResolveGitHubEnvToken_WhitespaceGITHUB_TOKENAlone_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", " ");

        Assert.Null(ChatClientFactory.ResolveGitHubEnvToken());
    }
}
