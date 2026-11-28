using System;
using System.Collections.Generic;
using System.Threading;

namespace SharpCoder;

/// <summary>
/// Optional, tool-specific metrics emitted alongside a <see cref="ToolCallUpdate"/>. Built-in tools
/// publish these as structured data, so hosts never have to scrape numbers out of the human-readable
/// <see cref="ToolCallUpdate.Result"/> text. All properties are nullable: a missing value means the
/// tool did not report that metric for this invocation, not zero.
/// </summary>
public sealed class ToolCallFacts
{
    /// <summary>First and last 1-based line numbers read (inclusive). <c>read_file</c>.</summary>
    public int? StartLine { get; set; }

    /// <summary>Last 1-based line number read (inclusive). <c>read_file</c>.</summary>
    public int? EndLine { get; set; }

    /// <summary>Number of characters of file content returned. <c>read_file</c>.</summary>
    public int? CharactersRead { get; set; }

    /// <summary>Number of exact occurrences replaced. <c>edit_file</c>.</summary>
    public int? OccurrencesReplaced { get; set; }

    /// <summary>Number of characters removed by a replacement. <c>edit_file</c>.</summary>
    public int? CharactersRemoved { get; set; }

    /// <summary>Number of characters added by a replacement or write. <c>edit_file</c>, <c>write_file</c>.</summary>
    public int? CharactersAdded { get; set; }

    /// <summary>Number of characters written. <c>write_file</c>.</summary>
    public int? CharactersWritten { get; set; }

    /// <summary>Number of paths returned. <c>glob</c>.</summary>
    public int? MatchCount { get; set; }

    /// <summary>Number of files containing matches. <c>grep</c>.</summary>
    public int? FileCount { get; set; }

    /// <summary>Number of available skills. <c>list_skills</c>.</summary>
    public int? SkillCount { get; set; }

    /// <summary>Name of the loaded skill. <c>load_skill</c>.</summary>
    public string? SkillName { get; set; }

    /// <summary>Effective whole-capture timeout for this invocation, in milliseconds. <c>execute_bash_command</c>.</summary>
    public long? TimeoutMs { get; set; }

    /// <summary>
    /// True when the effective timeout came from the configured default rather than an explicit
    /// <c>timeout_ms</c> argument. Used to render "default" alongside the real timeout value.
    /// </summary>
    public bool? TimeoutIsDefault { get; set; }

    /// <summary>Observed process exit code. Null when it could not be observed. <c>execute_bash_command</c>.</summary>
    public int? ExitCode { get; set; }

    /// <summary>True when the command hit its timeout. <c>execute_bash_command</c>.</summary>
    public bool? TimedOut { get; set; }
}

/// <summary>
/// Per-invocation channel through which built-in tools publish <see cref="ToolCallFacts"/> to the
/// streaming loop that invoked them. Async-local so concurrent executions and sub-agents stay
/// isolated; the loop sets a fresh scope before each <c>AIFunction.InvokeAsync</c> and reads the
/// published facts afterwards.
/// </summary>
internal static class ToolCallMetrics
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Starts a fresh scope, invokes <paramref name="action"/> inside it, and returns its facts.</summary>
    internal static async Task<ToolCallFacts?> CaptureAsync(Func<Task> action)
    {
        var scope = new Scope();
        var previous = Current.Value;
        Current.Value = scope;
        try
        {
            await action().ConfigureAwait(false);
            return scope.Facts;
        }
        finally
        {
            Current.Value = previous;
        }
    }

    /// <summary>Publishes facts for the current invocation. No-op outside a capture scope.</summary>
    internal static void Publish(ToolCallFacts facts)
    {
        var scope = Current.Value;
        if (scope is not null)
            scope.Facts = facts;
    }

    private sealed class Scope
    {
        public ToolCallFacts? Facts { get; set; }
    }
}
