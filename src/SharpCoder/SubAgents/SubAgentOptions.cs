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
}
