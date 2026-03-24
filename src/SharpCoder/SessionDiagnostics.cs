using System.Collections.Generic;

namespace SharpCoder;

/// <summary>
/// Captures the exact inputs sent to the LLM for debugging and verification.
/// Populated before the LLM call so it's available even when the call fails.
/// </summary>
public sealed class SessionDiagnostics
{
    /// <summary>The fully assembled system prompt including all auto-loaded workspace instructions and skills.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>The user message sent to the LLM.</summary>
    public string UserMessage { get; set; } = string.Empty;

    /// <summary>Number of session history messages replayed (0 for first call).</summary>
    public int SessionHistoryCount { get; set; }

    /// <summary>Total number of ChatMessages sent to the LLM (system + history + user).</summary>
    public int TotalMessageCount { get; set; }

    /// <summary>Names of all tools registered in the ChatOptions.</summary>
    public IReadOnlyList<string> ToolNames { get; set; } = new List<string>();

    /// <summary>The working directory used for the agent.</summary>
    public string WorkDirectory { get; set; } = string.Empty;

    /// <summary>Whether bash/shell execution was enabled.</summary>
    public bool EnableBash { get; set; }

    /// <summary>Whether file write operations were enabled.</summary>
    public bool EnableFileWrites { get; set; }

    /// <summary>Whether workspace instructions were auto-loaded from disk.</summary>
    public bool AutoLoadedWorkspaceInstructions { get; set; }

    /// <summary>Whether skills were enabled and loaded.</summary>
    public bool SkillsEnabled { get; set; }

    /// <summary>The reasoning effort level, if set.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Maximum tool call steps allowed.</summary>
    public int MaxSteps { get; set; }
}
