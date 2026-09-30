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

    /// <summary>
    /// Input token usage, if reported. Populated from <see cref="Usage"/> whenever at least one of
    /// the sub-agent's model calls was recorded, and <c>null</c> otherwise — so it is the sum over
    /// all of the sub-agent's calls, not the usage of its last one.
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>
    /// Output token usage, if reported. Populated from <see cref="Usage"/> whenever at least one of
    /// the sub-agent's model calls was recorded, and <c>null</c> otherwise — so it is the sum over
    /// all of the sub-agent's calls, not the usage of its last one.
    /// </summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// The running total of every model call this sub-agent made that has been forwarded to the
    /// parent's usage accounting so far — a detached snapshot, so later activity never changes it.
    /// This includes calls that failed, were cancelled, or timed out after the provider had already
    /// reported usage.
    /// <para>
    /// It is <c>null</c> only while no call has been recorded: a validation failure (which produces
    /// a standalone Failed snapshot with an empty <see cref="Id"/>), a run cancelled before its
    /// first call ended, or the Running snapshot of a sub-agent whose first call is still in flight.
    /// Snapshots taken from <see cref="SubAgentManager.GetStatus"/> report the totals recorded up to
    /// that moment, so a running sub-agent's totals grow as its calls end.
    /// </para>
    /// </summary>
    public TokenUsage? Usage { get; set; }

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
