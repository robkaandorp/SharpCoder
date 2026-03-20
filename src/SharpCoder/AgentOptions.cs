using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace SharpCoder;

public sealed class AgentOptions
{
    public string WorkDirectory { get; set; } = Directory.GetCurrentDirectory();
    public int MaxSteps { get; set; } = 25;
    public bool EnableBash { get; set; } = true;
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
