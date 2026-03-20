using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class FileToolsTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileTools _tools;

    public FileToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SharpCoderTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task WriteAndReadFile_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var filePath = "test.txt";
        var content = "Line 1\nLine 2\nLine 3";

        var writeResult = await _tools.write_file(filePath, content, ct);
        Assert.Contains("Successfully wrote", writeResult);

        var readResult = await _tools.read_file(filePath, ct: ct);
        Assert.Contains("1: Line 1", readResult);
        Assert.Contains("2: Line 2", readResult);
    }

    [Fact]
    public async Task EditFile_ExactReplacement_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var filePath = "edit_test.txt";
        await _tools.write_file(filePath, "Hello World\nThis is a test.", ct);

        var editResult = await _tools.edit_file(filePath, "Hello World", "Hello C#", ct);
        Assert.Contains("Successfully replaced", editResult);

        var readResult = await _tools.read_file(filePath, ct: ct);
        Assert.Contains("1: Hello C#", readResult);
    }

    [Fact]
    public async Task EditFile_NoMatch_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var filePath = "edit_nomatch.txt";
        await _tools.write_file(filePath, "Hello World", ct);

        var editResult = await _tools.edit_file(filePath, "Missing", "New", ct);
        Assert.Contains("Error: oldString not found", editResult);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config")]
    public async Task ReadFile_PathTraversal_ReturnsError(string maliciousPath)
    {
        var result = await _tools.read_file(maliciousPath, ct: TestContext.Current.CancellationToken);
        Assert.Contains("escapes the work directory", result);
    }

    [Theory]
    [InlineData("../../escape.txt")]
    [InlineData("/tmp/escape.txt")]
    public async Task WriteFile_PathTraversal_ReturnsError(string maliciousPath)
    {
        var result = await _tools.write_file(maliciousPath, "pwned", TestContext.Current.CancellationToken);
        Assert.Contains("escapes the work directory", result);
    }

    [Theory]
    [InlineData("../../escape.txt")]
    public async Task EditFile_PathTraversal_ReturnsError(string maliciousPath)
    {
        var result = await _tools.edit_file(maliciousPath, "old", "new", TestContext.Current.CancellationToken);
        Assert.Contains("escapes the work directory", result);
    }
}
