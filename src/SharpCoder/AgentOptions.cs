using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace SharpCoder;

public sealed class AgentOptions
{
    private string _workDirectory = Directory.GetCurrentDirectory();

    /// <summary>
    /// The working directory for the agent. Must be a valid, existing directory.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the directory does not exist.</exception>
    public string WorkDirectory
    {
        get => _workDirectory;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("WorkDirectory cannot be null or empty.", nameof(WorkDirectory));
            var fullPath = Path.GetFullPath(value);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"WorkDirectory does not exist: {fullPath}");
            _workDirectory = fullPath;
        }
    }

    public int MaxSteps { get; set; } = 25;

    /// <summary>
    /// Enables the bash/shell execution tool. Defaults to <c>false</c> for security.
    /// <para>
    /// <b>WARNING:</b> When enabled, the LLM can execute arbitrary shell commands on the
    /// host system with the same privileges as the running process. There is no sandboxing,
    /// command filtering, or directory confinement — the agent has full shell access.
    /// Only enable this in trusted or sandboxed environments (e.g. containers, CI runners).
    /// </para>
    /// </summary>
    public bool EnableBash { get; set; } = false;

    /// <summary>
    /// Optional shell executable override used by <c>execute_bash_command</c>. When set,
    /// this path is used instead of the platform default (<c>cmd.exe</c> on Windows,
    /// <c>/bin/bash</c> elsewhere). Typical uses on Windows: <c>bash.exe</c> (WSL),
    /// <c>C:\Program Files\Git\bin\bash.exe</c> (Git Bash), or <c>pwsh.exe</c>.
    /// <para>
    /// When overriding, also set <see cref="BashShellArgsFormat"/> if the shell's flag
    /// for passing a single command string is not <c>-c "..."</c>.
    /// </para>
    /// </summary>
    public string? BashShellPath { get; set; }

    /// <summary>
    /// Optional formatter that converts the LLM-supplied command string into the
    /// arguments passed to <see cref="BashShellPath"/>. Receives the raw command,
    /// returns the full argument string. Defaults to <c>-c "{command}"</c> with
    /// double-quote escaping (bash-style) when <see cref="BashShellPath"/> is set.
    /// </summary>
    public Func<string, string>? BashShellArgsFormat { get; set; }

    public bool EnableFileOps { get; set; } = true;
    public bool EnableFileWrites { get; set; } = true;
    public bool EnableSkills { get; set; } = true;
    
    // System Prompt settings
    public string? SystemPrompt { get; set; }
    public string? CustomInstructions { get; set; }
    public bool AutoLoadWorkspaceInstructions { get; set; } = true;

    // Tools
    public IList<AITool> CustomTools { get; set; } = new List<AITool>();

    // Context management
    /// <summary>Maximum context window size in tokens for the model. Used for compaction decisions.</summary>
    public int MaxContextTokens { get; set; } = 100_000;

    /// <summary>Fraction of MaxContextTokens at which automatic compaction triggers (0.0–1.0).</summary>
    public double CompactionThreshold { get; set; } = 0.8;

    /// <summary>Number of recent messages to keep verbatim during compaction.</summary>
    public int CompactionRetainRecent { get; set; } = 10;

    /// <summary>Enable automatic context compaction when approaching token limits.</summary>
    public bool EnableAutoCompaction { get; set; } = true;

    /// <summary>
    /// Optional callback invoked immediately before context compaction begins (before the
    /// summarisation LLM call). Use to show a "compacting…" indicator in the UI.
    /// </summary>
    public Action? OnCompacting { get; set; }

    /// <summary>
    /// Optional callback invoked after context compaction completes.
    /// Receives the number of messages compacted, the message count remaining,
    /// and the estimated token count after compaction.
    /// </summary>
    public Action<CompactionResult>? OnCompacted { get; set; }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Optional reasoning effort level for models that support extended thinking.
    /// When set, the model will adjust its reasoning depth accordingly.
    /// When <c>null</c>, no reasoning configuration is sent (provider default).
    /// </summary>
    public ReasoningEffort? ReasoningEffort { get; set; }

    /// <summary>
    /// When <c>true</c>, tool calls are surfaced as markdown-formatted
    /// <see cref="StreamingUpdateKind.TextDelta"/> events during streaming,
    /// inserted at the correct position between LLM text chunks.
    /// <para>
    /// Each tool call appears as an inline-code line with the tool name and
    /// truncated arguments, followed by a blockquote with a one-line result summary.
    /// </para>
    /// Defaults to <c>false</c> (tool calls are invisible in the stream, handled by
    /// <c>FunctionInvokingChatClient</c> as a black box).
    /// </summary>
    public bool ShowToolCallsInStream { get; set; }

    /// <summary>
    /// Optional <see cref="IChatClient"/> to use for context compaction summaries.
    /// When <c>null</c>, the main agent client is used (default behavior).
    /// Set this to a cheaper/faster model to avoid consuming the main model's
    /// rate limit or context window during compaction.
    /// </summary>
    public IChatClient? CompactionClient { get; set; }
}
