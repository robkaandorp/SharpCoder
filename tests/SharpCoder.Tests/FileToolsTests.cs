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
        var filePath = "test.txt";
        var content = "Line 1\nLine 2\nLine 3";

        var writeResult = await _tools.write_file(filePath, content);
        Assert.Contains("Successfully wrote", writeResult);

        var readResult = await _tools.read_file(filePath);
        Assert.Contains("1: Line 1", readResult);
        Assert.Contains("2: Line 2", readResult);
    }

    [Fact]
    public async Task EditFile_ExactReplacement_Works()
    {
        var filePath = "edit_test.txt";
        await _tools.write_file(filePath, "Hello World\nThis is a test.");

        var editResult = await _tools.edit_file(filePath, "Hello World", "Hello C#");
        Assert.Contains("Successfully replaced", editResult);

        var readResult = await _tools.read_file(filePath);
        Assert.Contains("1: Hello C#", readResult);
    }

    [Fact]
    public async Task EditFile_NoMatch_ReturnsError()
    {
        var filePath = "edit_nomatch.txt";
        await _tools.write_file(filePath, "Hello World");

        var editResult = await _tools.edit_file(filePath, "Missing", "New");
        Assert.Contains("Error: oldString not found", editResult);
    }
}
