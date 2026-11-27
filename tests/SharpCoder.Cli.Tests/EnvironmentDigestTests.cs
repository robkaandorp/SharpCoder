using Spectre.Console.Testing;

namespace SharpCoder.Cli.Tests;

public class EnvironmentDigestTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    private static CliOptions Options(string workDirectory, bool bash = false, bool readOnly = false)
    {
        var parts = new List<string> { "run", "-w", workDirectory };
        if (bash) parts.Add("--bash");
        if (readOnly) parts.Add("--read-only");
        parts.Add("task");
        return CliOptions.Parse(parts.ToArray())!;
    }

    [Fact]
    public void Build_IncludesRuntimeOsAndToolchain()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest(
            (file, args, _, _) => file == "dotnet" ? "11.0.100" : null,
            () => FixedNow);

        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("# Environment context", output);
        Assert.Contains("Date/time (UTC): 2026-11-27 20:00:00", output);
        Assert.Contains("OS: ", output);
        Assert.Contains($".NET runtime: {Environment.Version}", output);
        Assert.Contains("dotnet SDK: 11.0.100", output);
        Assert.Contains($"Work directory: {dir.Path}", output);
    }

    [Fact]
    public void Build_MissingProbes_DegradeToUnavailable()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest((_, _, _, _) => null, () => FixedNow);

        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("dotnet SDK: unavailable", output);
        Assert.Contains("Git branch: unavailable", output);
        Assert.DoesNotContain("Git status (short):", output);
        Assert.DoesNotContain("Last commit:", output);
    }

    [Fact]
    public void Build_GitBranchAndStatus_AreIncluded()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest((file, args, _, _) =>
        {
            if (file != "git") return null;
            if (args.Contains("abbrev-ref")) return "develop";
            if (args.Contains("status")) return " M README.md\n?? new-file.cs\n";
            if (args.Contains("log")) return "7beb86b Add CLI --read-only flag";
            return null;
        }, () => FixedNow);

        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("Git branch: develop", output);
        Assert.Contains("Git status (short):", output);
        Assert.Contains("  M README.md", output);
        Assert.Contains("  ?? new-file.cs", output);
        Assert.Contains("Last commit: 7beb86b Add CLI --read-only flag", output);
    }

    [Fact]
    public void Build_CleanWorkingTree_IsReported()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest((file, args, _, _) =>
        {
            if (file != "git") return null;
            if (args.Contains("abbrev-ref")) return "main";
            if (args.Contains("status")) return "";
            return null;
        }, () => FixedNow);

        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("(clean working tree)", output);
    }

    [Fact]
    public void Build_GitStatus_IsCapped()
    {
        using var dir = new TempDirectory();
        var many = string.Concat(Enumerable.Range(0, 60).Select(i => $" M file{i}.cs\n"));
        var digest = new EnvironmentDigest((file, args, _, _) =>
        {
            if (file != "git") return null;
            if (args.Contains("abbrev-ref")) return "develop";
            if (args.Contains("status")) return many;
            return null;
        }, () => FixedNow);

        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains($"… ({60 - EnvironmentDigest.MaxGitStatusLines} more entries)", output);
        Assert.DoesNotContain(" M file59.cs", output);
    }

    [Fact]
    public void Build_TopLevelEntries_AreListedAndLocalCachesHidden()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "README.md"), "hello");
        Directory.CreateDirectory(Path.Combine(dir.Path, "src"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "obj"));
        Directory.CreateDirectory(Path.Combine(dir.Path, ".git"));

        var digest = new EnvironmentDigest((_, _, _, _) => null, () => FixedNow);
        var output = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("  README.md", output);
        Assert.Contains("  src/", output);
        Assert.DoesNotContain("  obj/", output);
        Assert.DoesNotContain("  .git/", output);
    }

    [Fact]
    public void Build_Capabilities_ReflectOptions()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest((_, _, _, _) => null, () => FixedNow);

        var writable = digest.Build(Options(dir.Path), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);
        Assert.Contains("Bash tool: disabled", writable);
        Assert.Contains("read_file, glob, grep, write_file, edit_file (writes enabled)", writable);
        Assert.Contains("Skills: enabled (list_skills, load_skill)", writable);
        Assert.Contains("Max steps: 25", writable);

        var readOnly = digest.Build(Options(dir.Path, readOnly: true), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);
        Assert.Contains("read_file, glob, grep (read-only)", readOnly);
        Assert.DoesNotContain("write_file", readOnly);

        var withBash = digest.Build(Options(dir.Path, bash: true), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);
        Assert.Contains($"Bash tool: enabled (shell: {EnvironmentDigest.ShellPlaceholder})", withBash);
    }

    [Fact]
    public void Build_ResolvedShellPlaceholder_IsSubstitutable()
    {
        using var dir = new TempDirectory();
        var digest = new EnvironmentDigest((_, _, _, _) => null, () => FixedNow);

        var output = digest.Build(Options(dir.Path, bash: true), EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken)
            .Replace(EnvironmentDigest.ShellPlaceholder, "pwsh (auto: /usr/bin/pwsh)", StringComparison.Ordinal);

        Assert.Contains("Bash tool: enabled (shell: pwsh (auto: /usr/bin/pwsh))", output);
        Assert.DoesNotContain("{RESOLVED_SHELL}", output);
    }

    [Fact]
    public void Build_WithRealProbes_ProducesUsableOutputInRepoRoot()
    {
        // Exercises RunProcess end-to-end against the actual workspace: dotnet and git must both
        // resolve here. This guards against a broken ProcessStartInfo/ArgumentList setup.
        var workDirectory = FindRepoRoot();
        var options = CliOptions.Parse(new[] { "run", "-w", workDirectory, "task" })!;
        var digest = EnvironmentDigest.CreateDefault();

        var output = digest.Build(options, EnvironmentDigest.ShellPlaceholder, TestContext.Current.CancellationToken);

        Assert.Contains("# Environment context", output);
        Assert.Contains("dotnet SDK: ", output);
        Assert.DoesNotContain("dotnet SDK: unavailable", output);
        Assert.Contains("Git branch: ", output);
        Assert.DoesNotContain("Git branch: unavailable", output);
        Assert.Contains("Top-level workspace entries:", output);
        Assert.Contains("README.md", output);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "SharpCoder.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void Configuration_EnvContextRow_ReflectsOption()
    {
        static string Render(bool noEnvContext)
        {
            var console = TestConsoleFactory.Create(width: 120);
            var ui = new ConsoleUi { RenderConsole = console };
            ui.Configuration(
                model: "copilot/test-model",
                workDirectory: "/repo",
                maxSteps: 25,
                enableBash: false,
                readOnly: false,
                noEnvContext: noEnvContext,
                confirmTools: ToolConfirmationMode.Auto,
                reasoning: null,
                contextTokens: 200_000,
                shell: SharpCoder.ShellPreference.Auto,
                savePath: null);
            return console.Output;
        }

        Assert.Contains("Env context", Render(false));
        Assert.Contains("on (OS, SDK, git, workspace, tools in system prompt)", Render(false));
        Assert.Contains("off (--no-env-context)", Render(true));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sharpcoder-cli-envdigest-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }
}
