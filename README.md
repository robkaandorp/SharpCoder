# SharpCoder

[![NuGet](https://img.shields.io/nuget/v/SharpCoder.svg)](https://www.nuget.org/packages/SharpCoder/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A standalone, embeddable autonomous coding agent for .NET — built on [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/).

SharpCoder gives any `IChatClient` (OpenAI, Ollama, Azure, Anthropic, etc.) the ability to read files, write code, search codebases, and execute shell commands autonomously. Plug it into your app with a few lines of code.

## Features

- **Provider-agnostic** — works with any `IChatClient` implementation
- **Built-in tools** — file read/write/edit, glob, grep, bash, and skills
- **Security by default** — path traversal protection, bash disabled by default
- **Workspace-aware** — auto-loads `AGENTS.md` and `.github/copilot-instructions.md`
- **Rich results** — token usage, tool call count, full message history
- **Targets `netstandard2.1`** — runs on .NET Core 3+, .NET 5–10, and beyond

## Quick Start

```bash
dotnet add package SharpCoder
```

```csharp
using Microsoft.Extensions.AI;
using SharpCoder;

// Use any IChatClient — OpenAI, Ollama, Azure, etc.
IChatClient chatClient = new OllamaChatClient("http://localhost:11434", "qwen2.5-coder");

var agent = new CodingAgent(chatClient, new AgentOptions
{
    WorkDirectory = "/path/to/your/project",
    MaxSteps = 15
});

var result = await agent.ExecuteAsync("Add unit tests for the Calculator class");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Tool calls: {result.ToolCallCount}");
Console.WriteLine($"Tokens used: {result.Usage?.TotalTokenCount}");
Console.WriteLine(result.Message);
```

## Configuration

All behavior is controlled through `AgentOptions`:

```csharp
var options = new AgentOptions
{
    // Where the agent operates (default: current directory)
    WorkDirectory = "/my/project",

    // Max tool call iterations before stopping (default: 25)
    MaxSteps = 25,

    // Enable shell command execution (default: false — security risk!)
    EnableBash = false,

    // File system tools (default: true)
    EnableFileOps = true,
    EnableFileWrites = true,

    // Skill loading from .github/skills/ (default: true)
    EnableSkills = true,

    // Override the default system prompt
    SystemPrompt = "You are a test-writing expert.",

    // Append custom instructions to the system prompt
    CustomInstructions = "Always use xUnit. Prefer Arrange-Act-Assert.",

    // Auto-load AGENTS.md and copilot-instructions.md (default: true)
    AutoLoadWorkspaceInstructions = true,

    // Add your own custom AITools
    CustomTools = new List<AITool>
    {
        AIFunctionFactory.Create(MyCustomTool)
    }
};
```

## Built-in Tools

| Tool | Description | Enabled by |
|------|-------------|-----------|
| `read_file` | Read file contents with line numbers and pagination | `EnableFileOps` |
| `write_file` | Create or overwrite files | `EnableFileWrites` |
| `edit_file` | Exact string replacement (single occurrence) | `EnableFileWrites` |
| `glob` | Find files by pattern (e.g. `src/**/*.cs`) | `EnableFileOps` |
| `grep` | Search file contents with regex | `EnableFileOps` |
| `execute_bash_command` | Run shell commands | `EnableBash` |
| `list_skills` / `load_skill` | Discover and load project skills | `EnableSkills` |

## Agent Result

`ExecuteAsync` returns an `AgentResult` with:

```csharp
result.Status        // "Success" or "Error"
result.Message       // Final text response from the agent
result.Messages      // Full conversation history (all messages, tool calls, results)
result.ToolCallCount // Number of tool invocations made
result.ModelId       // Model that produced the response
result.FinishReason  // Why the model stopped (e.g. "stop", "length")
result.Usage         // Token counts (InputTokenCount, OutputTokenCount, TotalTokenCount)
```

## Skills

The agent can discover and load project-specific skills from `.github/skills/`. Each skill is a Markdown file with YAML frontmatter:

```markdown
---
name: build
description: How to build this project
---

# Build Instructions

Run `dotnet build` to compile the solution.
```

The agent calls `list_skills` to discover what's available and `load_skill` to read the full instructions.

## Security

- **Path traversal protection** — all file operations are confined to `WorkDirectory`
- **Bash disabled by default** — opt in explicitly with `EnableBash = true`
- **No sandboxing for bash** — when enabled, the agent has full shell access with the process's privileges. Only enable in trusted environments (containers, CI runners)

## License

MIT
