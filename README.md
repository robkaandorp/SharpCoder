# SharpCoder

[![NuGet](https://img.shields.io/nuget/v/SharpCoder.svg)](https://www.nuget.org/packages/SharpCoder/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A standalone, embeddable autonomous coding agent for .NET — built on [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/).

SharpCoder gives any `IChatClient` (OpenAI, Ollama, Azure, Anthropic, etc.) the ability to read files, write code, search codebases, and execute shell commands autonomously. Plug it into your app with a few lines of code.

## Features

- **Provider-agnostic** — works with any `IChatClient` implementation
- **Built-in tools** — file read/write/edit, glob, grep, bash, and skills
- **Persistent sessions** — multi-turn conversations with save/load to JSON
- **Streaming** — `IAsyncEnumerable<StreamingUpdate>` for real-time token delivery
- **Auto-compaction** — summarizes old context to stay within token limits
- **Security by default** — path traversal protection, bash disabled by default
- **Workspace-aware** — auto-loads `AGENTS.md` and `.github/copilot-instructions.md`
- **Rich results** — token usage, tool call count, full message history, diagnostics
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

## Sessions

Sessions provide persistent, multi-turn conversations. The agent remembers prior turns and builds on them:

```csharp
var session = AgentSession.Create();

// Turn 1 — agent reads the codebase
await agent.ExecuteAsync(session, "Read the Calculator class and understand it");

// Turn 2 — agent remembers what it read
await agent.ExecuteAsync(session, "Now add tests for the edge cases you found");

// Save session to disk for crash recovery
await session.SaveAsync("session.json");

// Load a saved session later
var restored = await AgentSession.LoadAsync("session.json");
await agent.ExecuteAsync(restored, "What did we do so far?");
```

### Session Forking

`AgentSession.Fork()` creates a deep copy of a session — useful for branching a conversation without affecting the original:

```csharp
var session = AgentSession.Create();
await agent.ExecuteAsync(session, "Analyse the codebase");

// Fork the session for an independent sub-task
var forked = session.Fork();
await agent.ExecuteAsync(forked, "Now refactor the Parser class");

// Original session is unaffected — continue where you left off
await agent.ExecuteAsync(session, "Now add tests for what you found");
```

Forked sessions get a new session ID, zeroed token counters, and fresh timestamps. Message history is deep-copied so mutations to either session are independent. `LastKnownContextTokens` is preserved for accurate compaction decisions.

Sessions track cumulative token usage, tool call counts, and exact context size from the most recent API response.

## Streaming

Stream text tokens as they arrive instead of waiting for the full response:

```csharp
await foreach (var update in agent.ExecuteStreamingAsync(session, "Refactor the Parser class"))
{
    switch (update.Kind)
    {
        case StreamingUpdateKind.TextDelta:
            Console.Write(update.Text); // incremental text chunk
            break;
        case StreamingUpdateKind.Completed:
            Console.WriteLine($"\nDone: {update.Result!.Status}");
            break;
    }
}
```

Streaming uses the same tool invocation pipeline as `ExecuteAsync` — tools execute transparently between text chunks. The session is persisted after the stream completes.

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
    },

    // Context management
    MaxContextTokens = 100_000,   // model's context window
    CompactionThreshold = 0.8,    // compact at 80% usage
    CompactionRetainRecent = 10,  // keep last 10 messages verbatim
    EnableAutoCompaction = true,  // enabled by default

    // Optional: separate (cheaper) model for context compaction summaries
    CompactionClient = new OllamaChatClient("http://localhost:11434", "llama3.2"),

    // Optional: reasoning effort for models with extended thinking
    ReasoningEffort = ReasoningEffort.Medium,

    // Optional: callback invoked before compaction begins (e.g. show a loading indicator)
    OnCompacting = () => Console.WriteLine("Compacting context…"),

    // Optional: callback invoked after compaction completes
    OnCompacted = result =>
        Console.WriteLine($"Compacted {result.TokensBefore} → {result.TokensAfter} tokens")
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

`ExecuteAsync` and `ExecuteStreamingAsync` return an `AgentResult` with:

```csharp
result.Status        // "Success", "MaxStepsReached", or "Error"
result.Message       // Final text response from the agent
result.Messages      // Full conversation history (all messages, tool calls, results)
result.ToolCallCount // Number of tool invocations made
result.ModelId       // Model that produced the response
result.FinishReason  // Why the model stopped (e.g. Stop, Length, ToolCalls)
result.Usage         // Token counts (InputTokenCount, OutputTokenCount, TotalTokenCount)
result.Diagnostics   // Snapshot of everything sent to the LLM (system prompt, tools, etc.)
```

## Context Compaction

Long-running sessions can exceed model context limits. SharpCoder automatically compacts conversation history by summarizing older messages while preserving recent context:

- Triggered when tokens exceed `CompactionThreshold × MaxContextTokens`
- Uses exact token counts from API responses (`LastKnownContextTokens`) when available; falls back to heuristic estimate (`~4 chars per token`) before the first API call
- Older messages are summarized into a single `[CONTEXT SUMMARY]` message
- Recent messages (count controlled by `CompactionRetainRecent`) are kept verbatim
- Key decisions, findings, and file paths are preserved in the summary
- **Automatic recovery** — If an API call fails due to context overflow (`model_max_prompt_tokens_exceeded`), the agent force-compacts the session and retries once
- **Mid-loop compaction** — During streaming with tool calls, compaction occurs between tool rounds to handle large tool results (e.g., web search returning 50K tokens). Session history is synchronized from the authoritative messages list after compaction to prevent duplicate entries.

- Disable with `EnableAutoCompaction = false` if you manage context manually.
- Use `OnCompacting` / `OnCompacted` callbacks to hook into the compaction lifecycle — e.g. to show a loading indicator before the summarisation call starts.
- **Separate compaction model** — Configure `CompactionClient` to use a cheaper/smaller model (e.g., `llama3.2` via Ollama) just for context compaction summaries. When not set, the main `IChatClient` is used (backward compatible).

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

## Example: comparing models with `SharpCoder.CliAgent`

The `examples/SharpCoder.CliAgent` project is a small command-line harness for
running coding assignments against **Ollama Cloud** models and capturing a full
log per run — useful for comparing the coding skills and output quality of
different models side by side.

```bash
export OLLAMA_API_KEY=...   # create at https://ollama.com/settings/keys

dotnet run --project examples/SharpCoder.CliAgent -- \
    --model gpt-oss:120b \
    --reasoning Medium \
    --work-dir runs/todo-api-gpt-oss \
    --assignment assignments/todo-api.md \
    --log-dir logs \
    --max-steps 50
```

| Flag | Description |
|------|-------------|
| `-m`, `--model` | Ollama Cloud model id (default `gpt-oss:120b`) |
| `-r`, `--reasoning` | `None`, `Low`, `Medium`, or `High` (default `Medium`) |
| `-w`, `--work-dir` | Directory the agent creates and uses as the project root. **Fails if it already exists** to protect prior experiments |
| `-a`, `--assignment` | Path to a markdown file describing the task |
| `-l`, `--log-dir` | Where per-run log files are written (default `logs`) |
| `-s`, `--max-steps` | Maximum agent iterations (default `50`) |
| `-c`, `--context-window` | Model context-window size in tokens (default `131072`). Bump for larger-context models (e.g. `262144` for Qwen3-Coder). |

Each run produces a single log file named
`{timestamp}_{model}_{reasoning}_{workdir}.log` that contains the flags,
assignment text, every `ILogger` event the agent emitted (tool calls,
compaction, errors), the final `AgentResult` (status, token usage, finish
reason), and the complete message history including all tool calls and
tool results. This makes it straightforward to diff two runs of the same
assignment across different models.

Bash and file writes are both enabled in this example — only run it against
trusted assignments.

## License

MIT
