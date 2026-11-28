namespace SharpCoder.Cli;

/// <summary>How the CLI renders structured tool-call updates.</summary>
public enum ToolDisplayMode
{
    /// <summary>Default: one line per tool call (two for bash). Facts come from structured fields.</summary>
    Compact,

    /// <summary>A full panel per call with arguments, the complete result and status.</summary>
    Verbose,
}
