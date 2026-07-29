namespace SharpCoder.SubAgents;

/// <summary>Lifecycle status of a sub-agent.</summary>
public enum SubAgentStatus
{
    /// <summary>The sub-agent is still executing.</summary>
    Running,

    /// <summary>The sub-agent completed successfully.</summary>
    Completed,

    /// <summary>The sub-agent failed.</summary>
    Failed,

    /// <summary>The sub-agent exceeded its timeout.</summary>
    TimedOut,

    /// <summary>The sub-agent was cancelled.</summary>
    Cancelled
}
