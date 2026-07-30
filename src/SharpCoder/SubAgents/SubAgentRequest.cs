using System;
using System.Collections.Generic;

namespace SharpCoder.SubAgents;

/// <summary>
/// A request to start a sub-agent.
/// </summary>
public sealed class SubAgentRequest
{
    /// <summary>The task description handed to the sub-agent.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>Optional model ID; must exist in <see cref="SubAgentOptions.AvailableModels"/>.</summary>
    public string? Model { get; set; }

    /// <summary>Optional system prompt replacing the default one.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Overrides the bash tool flag; null inherits the manager default.</summary>
    public bool? EnableBash { get; set; }

    /// <summary>Overrides the file-ops tool flag; null inherits the manager default.</summary>
    public bool? EnableFileOps { get; set; }

    /// <summary>Overrides the file-writes tool flag; null inherits the manager default.</summary>
    public bool? EnableFileWrites { get; set; }

    /// <summary>Overrides the skills tool flag; null inherits the manager default.</summary>
    public bool? EnableSkills { get; set; }

    /// <summary>Optional per-sub-agent timeout; must be positive, clamped to <see cref="SubAgentOptions.MaxTimeout"/>.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Optional image/PDF attachments handed to the sub-agent's initial message.</summary>
    public IReadOnlyList<ImageAttachment>? Images { get; set; }
}
