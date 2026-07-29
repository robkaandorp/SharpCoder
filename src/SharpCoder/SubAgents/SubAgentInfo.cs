using System;

namespace SharpCoder.SubAgents;

/// <summary>Immutable snapshot of a tracked sub-agent.</summary>
public sealed class SubAgentInfo
{
    /// <summary>The sub-agent identifier (empty for validation failures).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The (possibly truncated) task description.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>Current status.</summary>
    public SubAgentStatus Status { get; set; }

    /// <summary>When the sub-agent started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the sub-agent reached a terminal status, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The model ID used, if any.</summary>
    public string? Model { get; set; }

    /// <summary>The result summary; null while running or on failure.</summary>
    public string? Summary { get; set; }

    /// <summary>The error message, if the sub-agent failed.</summary>
    public string? Error { get; set; }

    /// <summary>Input token usage, if reported.</summary>
    public long? InputTokens { get; set; }

    /// <summary>Output token usage, if reported.</summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// Truncates <paramref name="value"/> to <paramref name="max"/> characters,
    /// replacing the final character with a Unicode ellipsis when truncation occurs.
    /// </summary>
    internal static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value.Substring(0, max - 1) + "\u2026";
    }
}
