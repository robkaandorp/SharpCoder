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
        var subdir = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(subdir);
        await File.WriteAllTextAsync(Path.Combine(subdir, "app.cs"), "public class App { public void Run() { } }");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "README.md"), "This is an app.");

        var globResult = _tools.glob("**/*.cs");
        Assert.Contains("app.cs", globResult);
        Assert.DoesNotContain("README.md", globResult);

        var grepResult = await _tools.grep(@"Run()");
        Assert.Contains("app.cs", grepResult);
        Assert.Contains("public void Run()", grepResult);

        var grepIncludeResult = await _tools.grep("app", "*.md");
        Assert.Contains("README.md", grepIncludeResult);
        Assert.DoesNotContain("app.cs", grepIncludeResult);
    }
}
