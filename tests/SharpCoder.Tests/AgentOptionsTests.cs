using System;
using System.IO;
using Xunit;

namespace SharpCoder.Tests;

public class AgentOptionsTests
{
    [Fact]
    public void WorkDirectory_ValidPath_Accepted()
    {
        var options = new AgentOptions
        {
            WorkDirectory = Path.GetTempPath()
        };
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), options.WorkDirectory);
    }

    [Fact]
    public void WorkDirectory_NullOrEmpty_Throws()
    {
        var options = new AgentOptions();
        Assert.Throws<ArgumentException>(() => options.WorkDirectory = null!);
        Assert.Throws<ArgumentException>(() => options.WorkDirectory = "");
        Assert.Throws<ArgumentException>(() => options.WorkDirectory = "   ");
    }

    [Fact]
    public void WorkDirectory_NonexistentPath_Throws()
    {
        var options = new AgentOptions();
        Assert.Throws<DirectoryNotFoundException>(() =>
            options.WorkDirectory = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void WorkDirectory_Default_IsCurrentDirectory()
    {
        var options = new AgentOptions();
        Assert.Equal(Path.GetFullPath(Directory.GetCurrentDirectory()), options.WorkDirectory);
    }
}
