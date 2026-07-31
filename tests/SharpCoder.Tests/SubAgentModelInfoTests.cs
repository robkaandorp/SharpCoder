using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

public class SubAgentModelInfoTests
{
    // ========================================================================
    // Existing 3-param constructors yield SupportsVision == false
    // ========================================================================

    [Fact]
    public void Single_Param_Constructor_Yields_SupportsVision_False()
    {
        var info = new SubAgentModelInfo("gpt-4");
        Assert.False(info.SupportsVision);
    }

    [Fact]
    public void Two_Param_Constructor_Yields_SupportsVision_False()
    {
        var info = new SubAgentModelInfo("gpt-4", "desc");
        Assert.False(info.SupportsVision);
    }

    [Fact]
    public void Three_Param_Constructor_Yields_SupportsVision_False()
    {
        var info = new SubAgentModelInfo("gpt-4", "desc", 8000);
        Assert.False(info.SupportsVision);
    }

    // ========================================================================
    // New 4-param overload
    // ========================================================================

    [Fact]
    public void Four_Param_Constructor_With_True_Yields_SupportsVision_True()
    {
        var info = new SubAgentModelInfo("gpt-4o", "vision model", 128000, supportsVision: true);
        Assert.True(info.SupportsVision);
    }

    [Fact]
    public void Four_Param_Constructor_With_False_Yields_SupportsVision_False()
    {
        var info = new SubAgentModelInfo("gpt-4", "text model", 8000, supportsVision: false);
        Assert.False(info.SupportsVision);
    }

    // ========================================================================
    // Id validation still works on the 4-param overload
    // ========================================================================

    [Fact]
    public void Four_Param_Constructor_Rejects_Empty_Id()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SubAgentModelInfo("", "desc", null, true));
        Assert.Contains("Model id cannot be null or whitespace", ex.Message);
    }

    // ========================================================================
    // Record equality — different SupportsVision values are NOT equal
    // ========================================================================

    [Fact]
    public void Records_With_Different_SupportsVision_Are_Not_Equal()
    {
        var withVision = new SubAgentModelInfo("gpt-4o", "vision", 128000, supportsVision: true);
        var withoutVision = new SubAgentModelInfo("gpt-4o", "vision", 128000, supportsVision: false);

        Assert.NotEqual(withVision, withoutVision);
        Assert.False(withVision == withoutVision);
        Assert.NotEqual(withVision.GetHashCode(), withoutVision.GetHashCode());
    }

    [Fact]
    public void Records_With_Same_SupportsVision_Are_Equal()
    {
        var a = new SubAgentModelInfo("gpt-4o", "vision", 128000, supportsVision: true);
        var b = new SubAgentModelInfo("gpt-4o", "vision", 128000, supportsVision: true);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}