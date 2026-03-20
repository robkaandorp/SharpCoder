using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class BashToolsTests
{
    [Fact]
    public async Task ExecuteBashCommand_Echo_ReturnsOutput()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command("echo Hello XUnit", TestContext.Current.CancellationToken);

        Assert.Contains("Hello XUnit", result);
        Assert.Contains("--- STDOUT ---", result);
    }
}
