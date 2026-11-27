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
- **Rich results and usage accounting** — per-call token usage, tool call count, full message history, diagnostics
- **XML documentation** — shipped with both packages for IntelliSense
- **Targets `netstandard2.1`** — runs on .NET Core 3+, .NET 5–10, and beyond

## Quick Start

GitHub Copilot provider requests use the per-account API endpoint advertised by GitHub when it can be discovered; if the lookup fails or the endpoint is untrusted, requests quietly fall back to `https://api.githubcopilot.com`. GitHub Enterprise (`*.ghe.com`) endpoints are not supported.

```bash
dotnet add package SharpCoder
# OllamaSharp is the provider client used below; any IChatClient works.
dotnet add package OllamaSharp
```

```csharp
using Microsoft.Extensions.AI;
using OllamaSharp;
using SharpCoder;

// Use any IChatClient — OpenAI, Ollama, Azure, etc.
IChatClient chatClient = new OllamaApiClient(new Uri("http://localhost:11434"), "qwen2.5-coder");

var agent = new CodingAgent(chatClient, new AgentOptions
{
    WorkDirectory = "/path/to/your/project",
    MaxSteps = 15
});

var result = await agent.ExecuteAsync("Add unit tests for the Calculator class");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Tool calls: {result.ToolCallCount}");
Console.WriteLine($"Tokens used: input={result.TokenUsage.Total.InputTokens}, output={result.TokenUsage.Total.OutputTokens}");
Console.WriteLine(result.Message);
```

## Host tool authorization

Use `AgentOptions.AuthorizeTool` to approve each locally invokable tool call before it runs. The callback receives the tool name, captured correlation ID and read-only argument snapshot, and returns `Allow` or a host-authored `Deny` decision:

```csharp
using System.Threading;
using System.Threading.Tasks;
using SharpCoder;

var options = new AgentOptions
{
    WorkDirectory = "/path/to/project",
    ToolAuthorizationCorrelationId = "user-123",
    AuthorizeTool = (context, cancellationToken) =>
    {
        if (context.ToolName == "write_file" || context.ToolName == "edit_file")
            return Task.FromResult(ToolAuthorizationDecision.Deny("File writes are not allowed for this user."));

        return Task.FromResult(ToolAuthorizationDecision.Allow);
    }
};

var agent = new CodingAgent(chatClient, options);
```

The callback and correlation ID are captured together when the `CodingAgent` is constructed and stay fixed for its lifetime. Accepted sub-agent children inherit that captured pair; later `AgentOptions` mutations affect only agents constructed afterwards. A denial, callback fault or null decision fails closed before tool side effects. Callback-fault details are sanitized and never shown to the model on both the manual-streaming and `FunctionInvokingChatClient` paths; the host-approved reason supplied to `Deny` is model-visible. Caller cancellation propagates.

All locally invokable tools are guarded: built-in bash, file-operation and skills tools, custom `AIFunction` tools, and the four sub-agent tools. When authorization is enabled, non-`AIFunction` custom tools fail closed while `BuildChatOptions` builds the model request. With no callback configured, tool behavior is unchanged from previous versions.

This callback boundary covers only tools SharpCoder registers for the model. Trusted host entry points are outside it and must be authorized by the host when needed: direct use of raw `FileTools`, host-retained unwrapped custom functions, and public `SubAgentManager.StartAsync`.

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

### Passing images directly to ExecuteAsync

You can also attach images directly to any turn when your own code loads or captures them. Pass an `IReadOnlyList<ImageAttachment>` to the image-capable `ExecuteAsync` or `ExecuteStreamingAsync` overload. The user message is sent as `TextContent` plus one `DataContent` per attachment. Callers must set `ImageAttachment.MediaType` explicitly (for example `"image/png"`, `"image/jpeg"`, `"application/pdf"`); there is no automatic inference from file extension for direct attachments.

```csharp
using Microsoft.Extensions.AI;
using OllamaSharp;
using SharpCoder;

IChatClient chatClient = new OllamaApiClient(new Uri("http://localhost:11434"), "llama3.2-vision");
var agent = new CodingAgent(chatClient, new AgentOptions
{
    WorkDirectory = "/path/to/project",
    MaxSteps = 15
});

string path = "screenshot.png";
byte[] screenshotBytes = await File.ReadAllBytesAsync(path);

var images = new List<ImageAttachment>
{
    new ImageAttachment
    {
        Data = screenshotBytes,
        MediaType = "image/png",
        Name = path
    }
};

var result = await agent.ExecuteAsync("What does this screenshot show?", images, CancellationToken.None);
Console.WriteLine(result.Message);
```

Images count toward `AgentSession.EstimatedContextTokens` with a flat per-attachment estimate. Image attachments are not persisted across `AgentSession.SaveAsync`/`LoadAsync` or `Fork()`.

## Sub-agents

Delegate self-contained subtasks — codebase analysis, large-text summarization, and parallel research — to background sub-sessions. Only their summaries and status metadata return to the main session, never full transcripts. Successful sub-agents retain their complete final response text verbatim as the summary through the `await_sub_agents` and `get_sub_agent_status` handoff; child conversation and tool transcripts are still not forwarded.

**v0.20.0 breaking migration:** `SubAgentOptions.MaxSummaryChars` and its plumbing were removed with no replacement or compatibility alias. Remove property assignments and dependent assertions, rebuild consuming applications against the updated packages, and keep `SharpCoder` and `SharpCoder.Providers` versions aligned when used together.

```csharp
using Microsoft.Extensions.AI;
using OllamaSharp;
using SharpCoder;
using SharpCoder.SubAgents;

IChatClient mainClient = new OllamaApiClient(new Uri("http://localhost:11434"), "qwen2.5-coder");

var agent = new CodingAgent(mainClient, new AgentOptions
{
    WorkDirectory = "/path/to/project",
    MaxSteps = 25,
    SubAgents = new SubAgentOptions
    {
        MaxConcurrentSubAgents = 4,
        DefaultTimeout = TimeSpan.FromMinutes(10),
        MaxTimeout = TimeSpan.FromMinutes(30),
        AvailableModels =
        {
            new SubAgentModelInfo("llama3.2", "Fast summarizer", 128_000),
            new SubAgentModelInfo("qwen2.5-coder", "Coding specialist", 131_072)
        },
        ClientFactory = modelId => new OllamaApiClient(new Uri("http://localhost:11434"), modelId)
    }
});

// The agent gains four tools: start_sub_agent, await_sub_agents,
// get_sub_agent_status, list_sub_agent_models.
// Sub-agents run read-only by default and cannot exceed the parent's capabilities.

var result = await agent.ExecuteAsync("Analyze the codebase and summarize the architecture");

// Always dispose to cancel any running sub-agents
await agent.DisposeAsync();
```

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxConcurrentSubAgents` | 4 | Maximum concurrently running sub-agents |
| `DefaultTimeout` | 10 min | Per-sub-agent timeout if not overridden |
| `MaxTimeout` | 30 min | Upper bound for per-request timeouts; larger values clamped |
| `DefaultEnableBash` | false | Default bash tool flag for sub-agents |
| `DefaultEnableFileOps` | true | Default file-ops tool flag for sub-agents |
| `DefaultEnableFileWrites` | false | Default file-writes tool flag for sub-agents |
| `DefaultEnableSkills` | true | Default skills tool flag for sub-agents |
| `MaxSteps` | 25 | Maximum agent-loop steps per sub-agent |
| `ClientFactory` | null | Maps a model ID to an `IChatClient`; required when a sub-agent requests a model from `AvailableModels` |
| `DefaultClient` | null | Fallback client when no model is specified; when null the parent agent's client is used |

Sub-agents can never exceed the parent agent's enabled capabilities (bash, file ops, file writes, skills). LLM-supplied overrides are clamped by the parent's flags, snapshotted at manager creation. Sub-agents run read-only by default.

### Observing sub-agent progress

Subscribe to `agent.SubAgentChanged` before calling `ExecuteAsync` to catch all events — the sub-agent manager is created lazily inside `BuildChatOptions` at the start of each execution call (before any sub-agent is started), and the agent-level event forwards to the manager's event at creation time. Two notifications fire per accepted run (start + terminal). The payload is a detached `SubAgentInfo` snapshot safe to read from any thread; a fresh instance is passed to each handler. Handlers are invoked synchronously in registration order; a throwing handler is caught and logged without affecting other handlers.

```csharp
agent.SubAgentChanged += info =>
{
    Console.WriteLine($"Sub-agent {info.Id}: {info.Status}");
    // info.Status is Running, Completed, Failed, TimedOut, or Cancelled
};
```

Sub-agents cannot spawn their own sub-agents. This flat-design limitation is planned for a future release.

- Mark vision-capable models in `SubAgentOptions.AvailableModels` with `new SubAgentModelInfo(..., supportsVision: true)`; the LLM can discover them through the `supports_vision` field emitted by `list_sub_agent_models`.

### Handing images to a vision sub-agent

A parent agent that does not itself need vision can still delegate image or PDF analysis to a vision-capable sub-agent. Use `start_sub_agent` with the `image_paths` argument and configure the sub-agent with a vision-capable model. Paths are repo-relative to `WorkDirectory` and are confined by the same path-safety rules as file tools.

By default, `image_paths` resolve only within the parent agent's `WorkDirectory`. Set `SubAgentOptions.AdditionalImagesRoot` to an absolute directory path to allow `image_paths` to also resolve within that directory — useful for temporary or uploaded attachments outside the repo. The value is validated and snapshotted at manager construction; `null` (the default) means no additional root is configured.

```csharp
var agent = new CodingAgent(mainClient, new AgentOptions
{
    WorkDirectory = "/path/to/project",
    MaxSteps = 25,
    SubAgents = new SubAgentOptions
    {
        MaxConcurrentSubAgents = 4,
        DefaultTimeout = TimeSpan.FromMinutes(10),
        MaxTimeout = TimeSpan.FromMinutes(30),
        AvailableModels =
        {
            new SubAgentModelInfo("llama3.2-vision", "Vision-capable analyzer", 128_000)
        },
        ClientFactory = modelId => new OllamaApiClient(new Uri("http://localhost:11434"), modelId)
    }
});

// The LLM can call:
// start_sub_agent(
//     task: "Describe this UI screenshot and suggest accessibility fixes",
//     model: "llama3.2-vision",
//     image_paths: new[] { "docs/screenshots/homepage.png" })
//
// Only the summary returns to the parent session; the full image transcript
// never enters the main conversation.
```

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
    // Which shell execute_bash_command launches (default: Auto —
    // pwsh → powershell.exe → cmd.exe on Windows; bash → /bin/sh on Linux/macOS)
    BashShell = SharpCoder.ShellPreference.Auto,

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
    CompactionRetainRecent = 2,     // floor: always keep at least this many recent messages
    CompactionRetainTokens = 16_000, // keep as many recent messages as fit in 16k tokens
    EnableToolResultElision = true,  // stub out bulky old tool results instead of summarizing them
    EnableAutoCompaction = true,  // enabled by default

    // Optional: separate (cheaper) model for context compaction summaries
    CompactionClient = new OllamaApiClient(new Uri("http://localhost:11434"), "llama3.2"),

    // Optional: compaction model's context window (enables chunked compaction when set)
    CompactionMaxTokens = 16_000, // compaction model's context window; null = use MaxContextTokens

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

On Windows the command runs under PowerShell 7+ (`pwsh`) when it is installed, falling
back to Windows PowerShell (`powershell.exe`), then to `cmd.exe` if neither is present. On
Linux and macOS it uses `bash` found on `PATH`, falling back to `/bin/sh`. Shells are located via `PATH`, never via fixed install locations. The shell is resolved once when the
agent is built; if the configured shell cannot be found, construction throws instead of failing
on the first command. On PowerShell the command is executed with a UTF-8 console encoding prelude
and a `-EncodedCommand` wrapper, so Unicode in the command and in output round-trips correctly and
no shell quoting rules leak into your command text; output is UTF-8. Cmdlet error records are
emitted by PowerShell as CLIXML on stderr (e.g. `#< CLIXML`), which is how PowerShell surfaces
them when nobody is attached to the console — that is normal, not corruption. Prefer
`pwsh`/PowerShell syntax (`Get-ChildItem`, `;`) over cmd.exe syntax; `&&`/`||` only work in
PowerShell 7+. To force a specific shell set `BashShell` (`SharpCoder.ShellPreference.Pwsh`,
`PowerShell`, `Cmd`, `Bash`, or `Auto`) — or `BashShellPath` for an arbitrary executable such as
Git Bash or WSL bash. On cmd.exe the command tail is passed through verbatim after
`/d /s /c ` (cmd.exe does not understand .NET argument quoting). If you set
`BashShellArgsFormat`, its output is split on whitespace with quote-grouping and no escape
processing — it is not a shell-quoted string.

`execute_bash_command` accepts an optional `timeout_ms` tool argument: a positive integer in milliseconds for that invocation. If omitted or `null`, the instance-configured default applies (normally 120000 ms); for example, `900000` provides a 15-minute budget. Zero or negative values are rejected before process launch, and each invocation starts a fresh shell process. The original C# caller signature remains compatible. Each stdout and stderr stream is capped at about 50,000 characters; for full output, redirect it to a file and use `grep` or `tail` to inspect it. A valid tool call is:

```json
{"command":"dotnet test --configuration Release", "timeout_ms":900000}
```

Caller cancellation propagates an `OperationCanceledException`; it does not return a timeout transcript, and raw captured output is not promised on cancellation. The command deadline covers root-process exit and redirected-output draining. Native process-tree termination is attempted while the root process is still alive; descendants that escaped ancestry or were reparented after root exit cannot reliably be found or killed, so cleanup is not guaranteed to be complete. The fixed five-second cleanup allowance bounds managed waits only, not synchronous OS calls, and there is no public `AgentOptions` command-timeout setting or managed background-job API. Completed output remains available with incomplete-capture diagnostics when output was not fully captured, and actual exit status is reported, including silent nonzero exits.

## Agent Result

`ExecuteAsync` and `ExecuteStreamingAsync` return an `AgentResult` with:

```csharp
result.Status        // "Success", "MaxStepsReached", or "Error"
result.Message       // Final text response from the agent
result.Messages      // Full conversation history (all messages, tool calls, results)
result.ToolCallCount // Number of tool invocations made
result.ModelId       // Model that produced the response
result.FinishReason  // Why the model stopped (e.g. Stop, Length, ToolCalls)
result.Usage         // Provider/framework usage for the final response (may aggregate internal round trips)
result.TokenUsage    // Per-call usage for this execution, grouped by source and model
result.Diagnostics   // Snapshot of everything sent to the LLM (system prompt, tools, etc.)
```

`Usage` is the provider-returned usage for the final response; on `FunctionInvokingChatClient` paths it may aggregate that request's internal round trips. It does not include compaction or sub-agent calls and is not a consistent per-execution total. Use `TokenUsage` for SharpCoder's per-call totals across sources.

## Usage accounting

`AgentResult.TokenUsage` is a detached, point-in-time snapshot of calls recorded for that execution. `AgentSession.Usage` is cumulative across calls recorded into the session and can be saved and loaded. Its `Total` sums all buckets, while `Entries` groups usage by source and model.

```csharp
var agent = new CodingAgent(chatClient, new AgentOptions
{
    WorkDirectory = "/path/to/project",
    OnUsage = usageEvent =>
    {
        // Report each ended model call; events include source, model and usage.
        Console.WriteLine($"{usageEvent.Source}/{usageEvent.Model}: {usageEvent.Usage}");
    }
});
```

**Thread safety:** `OnUsage` may be invoked concurrently and may run after `ExecuteAsync` has returned, because background sub-agents forward calls as they finish. Protect shared handler state accordingly. Sub-agent calls are forwarded into the parent's accounting as `SubAgent`, with the sub-agent ID on the event; `SubAgentInfo.Usage` exposes each sub-agent's running totals. A supported `SubAgentCompaction` classification exists, though current sub-agent executions are stateless and do not compact.

The `TokenUsage.Estimated` breakdown contains **estimates**, not measurements, of input categories (system prompt, tool definitions, user and assistant text, tool calls and results, reasoning, and images) and output categories (text, tool calls, and reasoning). Category sums cover only calls that contributed an estimate; `InputEstimatedCalls` and `OutputEstimatedCalls` show that coverage. A sub-agent call that ends after the result is built updates `AgentSession.Usage` and invokes `OnUsage`, but does not update the already-returned `AgentResult.TokenUsage` snapshot.

## Context Compaction

Long-running sessions can exceed model context limits. SharpCoder automatically compacts conversation history by summarizing older messages while preserving recent context:

- Triggered when tokens exceed `CompactionThreshold × MaxContextTokens`
- Uses exact token counts from the most recent API round-trip (`LastKnownContextTokens`) when available; falls back to heuristic estimate (`~4 chars per token`) before the first API call. When tools are invoked, the token count reflects the final round's context size — not the sum across all internal tool-call round-trips.
- Two-stage: old **tool-result bodies and attachments are first elided** (replaced by a one-line stub naming the tool, its arguments and the payload size — re-run the tool to recover the content). If elision alone brings the history back under the threshold, no summarization call is made at all; otherwise the remaining old messages are summarized into a single `[CONTEXT SUMMARY]` message
- Recent messages are kept verbatim within a token budget: as many newest messages as fit in `CompactionRetainTokens` (default 16,000, capped at half the compaction threshold), but always at least `CompactionRetainRecent` (default 2) messages, and never split mid tool-call/tool-result group
- **Leading system messages are preserved** — all consecutive `ChatRole.System` messages at the start of the history are kept intact and are never included in the summary. Callers do not need to re-add the system prompt after compaction.
- Key decisions, findings, and file paths are preserved in the summary
- **Automatic recovery** — If an API call fails due to context overflow (`model_max_prompt_tokens_exceeded`), the agent force-compacts the session and retries once
- **Mid-loop compaction** — During streaming with tool calls, compaction occurs between tool rounds to handle large tool results (e.g., web search returning 50K tokens). Session history is synchronized from the authoritative messages list after compaction to prevent duplicate entries.

- Disable with `EnableAutoCompaction = false` if you manage context manually.
- Use `OnCompacting` / `OnCompacted` callbacks to hook into the compaction lifecycle — e.g. to show a loading indicator before the summarisation call starts.
- **Separate compaction model** — Configure `CompactionClient` to use a cheaper/smaller model (e.g., `llama3.2` via Ollama) just for context compaction summaries. When not set, the main `IChatClient` is used (backward compatible).
- **Chunked compaction** — When `CompactionMaxTokens` is set and old messages exceed that budget, they are split into token-budgeted chunks and each chunk is summarized separately. The per-chunk summaries are concatenated into one summary message. This prevents the compaction model from overflowing when it has a smaller context window than the main model. When `CompactionMaxTokens` is null (default), all old messages are summarized in a single call (existing behavior).
- **Cancellation** — When a summarization call throws `OperationCanceledException` while the caller's token is cancelled, compaction rethrows it without changing history or `LastKnownContextTokens` or invoking `OnCompacted`; the attempted call is still recorded in usage, and chunked summarization stops without fallback text. Exceptions from `OnCompacting`/`OnCompacted` are not covered by this guarantee. No-op paths (such as auto-compaction disabled or too few messages) may still return `false` with a cancelled token; client-originated cancellation/timeout failures while the caller's token is not cancelled are handled by path: single-call summarization logs and returns `false`; automatic chunked paths log and substitute fallback text, while chunked `CompactOldestPercentAsync`/`ForceCompactAsync` return `false` without logging or fallback text.
- **Partial compaction** — `CompactOldestPercentAsync(session, options, percent, ct)` summarizes only the oldest X% of tokens, keeping the newest portion verbatim. Gentler than full compaction — preserves more recent context with full fidelity while reducing token usage.

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

## Command-line interface

The repository includes a small terminal front-end over the library, in `src/SharpCoder.Cli` (not shipped to NuGet). It streams responses live, renders tool calls inline, shows per-turn and cumulative token/tool usage, and supports saved sessions and the same providers as `SharpCoder.Providers`.

```bash
dotnet run --project src/SharpCoder.Cli -- \
    --model ollama-local/llama3 "Summarize this repository"

# interactive session (default)
dotnet run --project src/SharpCoder.Cli
```

In interactive mode the banner is followed by a prompt; `/help` lists the slash commands (`/save`, `/session`, `/markdown`, `/clear`, `/exit`). A task given without a subcommand runs once as a one-shot (`SharpCoder.Cli "fix the build"` is shorthand for `SharpCoder.Cli run "fix the build"`). Useful options: `--workdir`, `--max-steps`, `--reasoning`, `--bash` (with `--shell auto|pwsh|powershell|cmd|bash` to pick the shell), `--read-only`/`--no-writes` to disable `write_file`/`edit_file`, `--confirm-tools auto|always|never` (or `-y`/`--yes`) to control consent prompts for write and bash tools, `--verbose` (or `/verbose`) for full tool results, `--no-env-context` to skip the startup environment snapshot, `--session`/`--save` for session persistence, `-m|--model` for provider-prefixed model ids (`copilot/…`, `github/…`, `ollama-local/…`, `ollama-cloud/…`), `--context-tokens` for the model's context window, `--retain-tokens` for the verbatim tail kept on compaction, and `--markdown` for how assistant output is rendered. Credentials come from the same environment variables as `SharpCoder.Providers` (`GITHUB_TOKEN`/`GH_TOKEN`, `OLLAMA_API_KEY`, `OLLAMA_URL`, `LLM_PROVIDER`).

### Structured tool updates

The CLI renders tool calls as distinct terminal lines: a cyan call line with arguments, a result line (red when the tool failed), elapsed time, and a round/step progress line. By default only the first line of a tool result is shown; `--verbose` (or `/verbose on` in the REPL) prints the full, untruncated result, which is useful for build/test logs. When output is redirected (`--markdown raw`), tool annotations are kept off stdout, so `SharpCoder.Cli run "..." > out.txt` contains only the model's text.

These come from the library's additive `AgentOptions.EmitStructuredToolUpdates` flag. When enabled on the manual streaming tool loop, `CodingAgent` emits `StreamingUpdateKind.ToolCallStarted`, `ToolCallCompleted` and `RoundProgress` updates carrying `ToolCallUpdate` (full arguments, untruncated result, error flag, elapsed time) and `RoundProgressUpdate`. The flag defaults to `false`; the pre-existing markdown text narration is still emitted unchanged, so existing hosts that only read `TextDelta` see exactly the same output:

```csharp
var options = new AgentOptions
{
    ShowToolCallsInStream = true,
    EmitStructuredToolUpdates = true,
};

await foreach (var update in agent.ExecuteStreamingAsync(session, task))
{
    if (update.Kind == StreamingUpdateKind.ToolCallCompleted && update.ToolCall is { } t)
        Console.WriteLine($"{t.Name}: {t.Result} ({t.Elapsed.TotalMilliseconds:n0} ms, error={t.IsError})");
}
```

The flag has no effect on the default streaming path, where `FunctionInvokingChatClient` owns the tool loop and individual invocations are not observable.

### Tool confirmation

By default the CLI asks for consent before `write_file`, `edit_file` and `execute_bash_command` run. The prompt shows the tool name and its arguments; answer `y` to allow once, `n` to deny once (the default), `a` to allow all remaining calls in the session, or `s` to deny all remaining calls. Read/search tools and skills are never prompted. A denial is returned to the agent as a refusal — the tool does not run and no file or command is affected.

`--confirm-tools` selects the mode:

| Mode | Behavior |
| --- | --- |
| `auto` (default) | Prompt on an interactive terminal; auto-allow when input or output is redirected, so `SharpCoder.Cli run … > out.txt` never hangs |
| `always` | Prompt even when redirected; EOF / no answer denies (fail closed) |
| `never` | Allow all guarded calls without asking — for CI and trusted batches |

`-y` / `--yes` is an alias for `never`. The startup configuration table shows the effective mode. Note that `--read-only` and `--no-writes` remove the write tools entirely, which is a stronger guarantee than prompting.

### Startup environment context

By default the CLI adds a compact **environment context** block to the agent's system prompt so the model knows its surroundings without probing: OS/architecture, .NET runtime and SDK version, work directory, git branch / status / last commit, a bounded top-level workspace listing, and the enabled tool set (including the resolved shell and read-only state). It is a startup snapshot — the agent should re-check with a tool if state may have changed. Everything is best-effort with short timeouts (a missing toolchain or a non-git directory reports `unavailable`) and the output is capped (20 git-status lines, 40 workspace entries, 4,000 characters per command), so it cannot consume a meaningful share of the context window. Use `--no-env-context` to disable it; the startup configuration table shows the effective setting.

### Markdown rendering

Assistant output is rendered as markdown in the terminal. The CLI renders complete blocks as they finish arriving, so output still streams live; tool-call notes are shown as dim one-liners. It understands headings, paragraphs, bold/italic/strikethrough, inline code, fenced code blocks (with language label), ordered/unordered/task lists, blockquotes, tables, links, and horizontal rules.

`--markdown` selects how much processing happens. It defaults to `auto`, which uses `live` on an interactive terminal and `raw` when output is redirected or piped (so `SharpCoder.Cli … > out.txt` stays plain text):

| Mode | Behavior |
| --- | --- |
| `live` | Render each block as soon as it is complete (default on a TTY) |
| `full` | Buffer the whole turn and render once at the end |
| `raw` | No processing; the model's bytes are written verbatim |
| `auto` | `live` on a TTY, `raw` otherwise (default) |

`/markdown` in the interactive REPL shows or changes the mode for the rest of the session.

The CLI sets the context window used for compaction decisions to **1,000,000 tokens by default** (the library default is 100,000), and keeps up to **16,000 tokens** of recent conversation verbatim when compacting (`--retain-tokens`); older tool results are first replaced with one-line elision stubs, and only if that is not enough is the remainder summarized. Override it with `--context-tokens` — accepts a raw number or a `k`/`m` suffix (e.g. `--context-tokens 200000`, `--context-tokens 200k`, `--context-tokens 1m`). Compaction triggers at 80% of the configured window. Set this to your model's real window: set it too low and the agent compacts long before it needs to; set it too high and a smaller model fails with context-overflow errors (the agent then force-compacts and retries once, at the cost of the failed request and a more aggressive compaction than pre-emptive one).

The produced binary is `SharpCoder.Cli`. The CLI targets `net11.0` and is deliberately **not** in `SharpCoder.slnx` (CI builds the solution with the .NET 10 SDK); build it directly with `dotnet build src/SharpCoder.Cli` until CI moves to .NET 11.

## Security

- **Path traversal protection** — all file operations are confined to `WorkDirectory`
- **Bash disabled by default** — opt in explicitly with `EnableBash = true` (CLI: `--bash`)
- **File writes are enabled by default, but confirmed interactively** — the CLI registers `write_file`/`edit_file` and asks for consent via `AuthorizeTool` on an interactive terminal (see [Tool confirmation](#tool-confirmation)). In redirected/scripted runs `auto` mode allows them without prompting, so use `--confirm-tools always`, `-y`/`--yes` deliberately, or `--read-only`/`--no-writes` (which removes the write tools entirely). The library itself has no confirmation prompt: hosts must set `AgentOptions.AuthorizeTool` or `EnableFileWrites = false` themselves. The CLI's startup configuration table shows the effective settings. Only enable writes in trusted or sandboxed environments.
- **No sandboxing for bash** — when enabled, the agent has full shell access with the process's privileges. Only enable in trusted environments (containers, CI runners)

## License

MIT
