using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace SharpCoder.SubAgents;

/// <summary>Configuration surface for the sub-agent runtime.</summary>
public sealed class SubAgentOptions
{
    /// <summary>Maximum number of concurrently running sub-agents.</summary>
    public int MaxConcurrentSubAgents { get; set; } = 4;

    /// <summary>Default per-sub-agent timeout.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound for per-request timeouts; larger values are clamped.</summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of characters retained from a sub-agent summary.</summary>
    public int MaxSummaryChars { get; set; } = 8_000;

    /// <summary>Host-provided catalog of models available to sub-agents.</summary>
    public IList<SubAgentModelInfo> AvailableModels { get; } = new List<SubAgentModelInfo>();

    /// <summary>Maps a model ID to an <see cref="IChatClient"/>.</summary>
    /// <remarks>
    /// Clients returned by this factory are OWNED by the SubAgentManager and disposed
    /// after the sub-agent's run completes (before awaiters are signalled), or immediately
    /// if the run never starts. Do not share a returned client across factory calls or
    /// reuse it elsewhere. The manager does NOT dispose <see cref="DefaultClient"/> or
    /// the parent agent's client.
    /// </remarks>
    public Func<string, IChatClient>? ClientFactory { get; set; }

    /// <summary>Fallback client when no model is specified. When null the parent agent's client is used.</summary>
    public IChatClient? DefaultClient { get; set; }

    /// <summary>Default bash tool flag for sub-agents.</summary>
    public bool DefaultEnableBash { get; set; }

    /// <summary>Default file-ops tool flag for sub-agents.</summary>
    public bool DefaultEnableFileOps { get; set; } = true;

    /// <summary>Default file-writes tool flag for sub-agents.</summary>
    public bool DefaultEnableFileWrites { get; set; }

    /// <summary>Default skills tool flag for sub-agents.</summary>
    public bool DefaultEnableSkills { get; set; } = true;

    /// <summary>Maximum agent-loop steps per sub-agent.</summary>
    public int MaxSteps { get; set; } = 25;

    /// <summary>
    /// Optional host-designated directory that sub-agent image/PDF attachments may ALSO be loaded
    /// from, IN ADDITION to the parent agent's work directory. Null (the default) means not
    /// configured, and image loading uses the work directory only.
    /// <para>
    /// When set it must be a non-empty absolute path to an existing directory; otherwise
    /// <see cref="SubAgentManager"/> construction throws <see cref="ArgumentException"/>. The value
    /// is canonicalized and snapshotted at manager construction, so mutating it afterwards cannot
    /// widen the roots an existing manager accepts. Containment is enforced for this root exactly
    /// as for the work directory: paths escaping it are rejected.
    /// </para>
    /// </summary>
    public string? AdditionalImagesRoot { get; set; }
}
