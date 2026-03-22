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

    public bool EnableFileOps { get; set; } = true;
    public bool EnableFileWrites { get; set; } = true;
    public bool EnableSkills { get; set; } = true;
    
    // System Prompt settings
    public string? SystemPrompt { get; set; }
    public string? CustomInstructions { get; set; }
    public bool AutoLoadWorkspaceInstructions { get; set; } = true;

    // Tools
    public IList<AITool> CustomTools { get; set; } = new List<AITool>();

    public ILogger Logger { get; set; } = NullLogger.Instance;
}
