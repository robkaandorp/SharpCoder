namespace SharpCoder.Cli;

/// <summary>
/// Turns a startup failure into an actionable message. Provider and shell construction throw
/// bare <see cref="InvalidOperationException"/>s; without a hint the human sees only the raw text.
/// Pure string mapping so it can be unit-tested without constructing a client or an agent.
/// </summary>
internal static class StartupErrorHints
{
    /// <summary>Returns a hint line for <paramref name="message"/>, or <c>null</c> when none applies.</summary>
    public static string? For(string message)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        // Missing credentials: name the exact variable(s) to set.
        if (Contains(message, "OLLAMA_API_KEY"))
            return "Set OLLAMA_API_KEY (or choose a different --model provider).";
        if (Contains(message, "GH_TOKEN") || Contains(message, "GITHUB_TOKEN"))
            return "Set GH_TOKEN or GITHUB_TOKEN for the copilot/github providers (or choose a different --model).";
        if (Contains(message, "OLLAMA_URL"))
            return "Check OLLAMA_URL (default: http://localhost:11434).";

        // Every shell-resolution failure in BashTools names its preference, pwsh or PowerShell.
        // Matching those (rather than any "bash" mention) avoids false hints for unrelated errors.
        if (Contains(message, "ShellPreference") || Contains(message, "pwsh") ||
            Contains(message, "PowerShell") || Contains(message, "usable shell"))
            return "Use --shell auto (or a shell installed on this machine); see --help for the list.";

        if (Contains(message, "Unknown LLM provider") || Contains(message, "LLM_PROVIDER"))
            return "Check --model and LLM_PROVIDER; see --help for supported provider prefixes.";

        if (Contains(message, "WorkDirectory"))
            return "Check --workdir points to an existing directory.";

        return null;
    }

    private static bool Contains(string message, string term) =>
        message.Contains(term, StringComparison.OrdinalIgnoreCase);
}
