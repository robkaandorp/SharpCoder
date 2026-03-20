using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class SearchToolsTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileTools _tools;

    public SearchToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SharpCoderSearch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _tools = new FileTools(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public async Task Glob_And_Grep_Work()
    {
        var ct = TestContext.Current.CancellationToken;
        var subdir = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(subdir);
        await File.WriteAllTextAsync(Path.Combine(subdir, "app.cs"), "public class App { public void Run() { } }", ct);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "README.md"), "This is an app.", ct);

        var globResult = _tools.glob("**/*.cs");
        Assert.Contains("app.cs", globResult);
        Assert.DoesNotContain("README.md", globResult);

        var grepResult = await _tools.grep(@"Run()", ct: ct);
        Assert.Contains("app.cs", grepResult);
        Assert.Contains("public void Run()", grepResult);

        var grepIncludeResult = await _tools.grep("app", "*.md", ct);
        Assert.Contains("README.md", grepIncludeResult);
        Assert.DoesNotContain("app.cs", grepIncludeResult);
    }

    [Fact]
    public async Task Glob_RespectsPathPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var srcDir = Path.Combine(_testDir, "src");
        var testsDir = Path.Combine(_testDir, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "App.cs"), "class App {}", ct);
        await File.WriteAllTextAsync(Path.Combine(testsDir, "AppTests.cs"), "class AppTests {}", ct);

        // src/**/*.cs should only find files under src/
        var result = _tools.glob("src/**/*.cs");
        Assert.Contains("App.cs", result);
        Assert.DoesNotContain("AppTests.cs", result);

        // tests/**/*.cs should only find files under tests/
        var testResult = _tools.glob("tests/**/*.cs");
        Assert.Contains("AppTests.cs", testResult);
        Assert.DoesNotContain("App.cs", testResult);
    }

    [Fact]
    public void Glob_WithoutGlobstar_SearchesTopLevel()
    {
        var subdir = Path.Combine(_testDir, "sub");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(_testDir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(subdir, "nested.txt"), "nested");

        // *.txt without ** should only match top-level
        var result = _tools.glob("*.txt");
        Assert.Contains("root.txt", result);
        Assert.DoesNotContain("nested.txt", result);
    }

    [Fact]
    public void Glob_DirectoryPrefix_WithoutGlobstar()
    {
        var subdir = Path.Combine(_testDir, "lib");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "util.cs"), "class Util {}");
        File.WriteAllText(Path.Combine(_testDir, "main.cs"), "class Main {}");

        // lib/*.cs should only match in lib/
        var result = _tools.glob("lib/*.cs");
        Assert.Contains("util.cs", result);
        Assert.DoesNotContain("main.cs", result);
    }

    [Fact]
    public void Glob_PathTraversal_ReturnsError()
    {
        var result = _tools.glob("../../**/*.cs");
        Assert.Contains("Error", result);
        Assert.Contains("outside the work directory", result);
    }
}
