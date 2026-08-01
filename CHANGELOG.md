# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [0.16.0]

### Added

- **Sub-agent additional images root** — `SubAgentOptions` gains an `AdditionalImagesRoot` property (nullable `string?`). When set to a non-empty absolute path to an existing directory, sub-agent `image_paths` may resolve within it IN ADDITION to the parent agent's working-directory root — useful for volatile/temporary attachments (e.g., a Composer chat upload) without touching the repo. The value is validated at `SubAgentManager` construction (non-null requires a non-empty absolute path to an existing directory; whitespace/relative/nonexistent/file-not-directory → `ArgumentException`; `null` = not configured). It is canonicalized and snapshotted — post-construction mutation of the options does not widen the accepted roots. Two-root resolution: absolute paths inside either root are accepted; relative paths probe the primary root first, then the additional root (primary wins on collision); `../` escapes from either root are rejected. Count/size limits and no-config behavior are unchanged.

## [0.15.1]

### Added

- **Informational sub-agent vision flag** — `SubAgentModelInfo` gains a `SupportsVision` property (`bool`, default `false`). A new binary-compatible 4-parameter constructor overload `SubAgentModelInfo(string id, string? description, int? contextWindow, bool supportsVision)` is provided; the existing 3-parameter constructor `SubAgentModelInfo(string id, string? description = null, int? contextWindow = null)` is unchanged, so existing callers and compiled binaries continue to work. The `list_sub_agent_models` tool now emits a `supports_vision` field (`true` or `false`) for every catalog model, letting hosts mark which sub-agent models are vision-capable (for example when using `image_paths` for image/PDF tasks). This flag is informational only — SharpCoder does not reject visual inputs when a model's `supports_vision` is `false`.

## [0.15.0]

### Added

- **Vision/image support** — `CodingAgent` gains image-capable `ExecuteAsync` and `ExecuteStreamingAsync` overloads accepting `IReadOnlyList<ImageAttachment>?`. The user message is sent as `TextContent` plus one `DataContent` per image. New public `ImageAttachment` type with `Data byte[]`, `MediaType string`, and `Name string?`. Existing string-only entry points delegate to the new overload with `images: null`, so this is non-breaking.
- **Sub-agent image hand-off** — `start_sub_agent` gains an optional `image_paths` parameter (`string[]?` array of repo-relative paths). Files are resolved within the working directory via the shared `PathSafety` resolver, loaded by `ImageLoader`, and validated against count/size limits (max 8 images / 20 MiB cumulative). Media types are inferred from extension: `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.pdf`. This lets a text-only parent agent delegate image or PDF analysis to a vision-capable sub-agent model. New `SubAgentRequest.Images` property carries the attachments; `SubAgentManager` deep-copies them into the immutable per-run `RunSpec` snapshot.
- **Internal infrastructure** — Shared `PathSafety` lexical path resolver (boundary-safe, platform-correct) now also used by `FileTools.glob`. New bounded `ImageLoader` with race-safe reads and work-directory containment.
- **Token accounting** — `AgentSession.EstimatedContextTokens` now adds a flat per-image estimate (`ImageTokenEstimate = 1500`) for every `DataContent` whose `MediaType` starts with `image/` or equals `application/pdf`, after the existing `chars / 4` text heuristic.

### Note

- Image attachments are NOT persisted across `AgentSession.Fork()` or `AgentSession.SaveAsync/LoadAsync`. Only text-based `ChatMessage` history is preserved; visual content must be re-supplied on each turn that needs it.

## [0.14.0]

### Added

- **Sub-agent lifecycle events** — `SubAgentManager.SubAgentChanged` and agent-level `CodingAgent.SubAgentChanged`. Fire once when a sub-agent starts (`SubAgentStatus.Running`) and once when it reaches a terminal status (`Completed`, `Failed`, `TimedOut`, or `Cancelled`). The payload is a detached `SubAgentInfo` snapshot — the manager does NOT mutate it after emission, and a FRESH instance is passed to EACH handler, so handlers can safely read it from any thread and their mutations are isolated. Handlers are invoked synchronously in registration order; a throwing handler is caught and logged without affecting other handlers or the manager. No event fires for validation failures. The agent-level event lets hosts subscribe before the first `Running` event — the manager is created lazily inside `BuildChatOptions` (invoked at the start of each `ExecuteAsync`/`ExecuteStreamingAsync` call), and `CodingAgent.SubAgentChanged` forwards to the manager's event via method-group subscription at manager creation time (`ActiveSubAgentManager` may still be null when subscribing).

## [0.13.1]

### Fixed

- **Sub-agent client leak** — `SubAgentManager` now owns and disposes `IChatClient` instances created via `SubAgentOptions.ClientFactory`, exactly once, on all code paths: after a run completes (disposal is ordered before awaiters are signalled via `Complete()`), and on every pre-run exit (validation failure, cancelled slot wait, manager disposal during slot wait, post-slot startup failure). Previously these factory-created clients were never disposed, leaking one `IChatClient` per model-selected sub-agent.
- Caller-owned clients (`DefaultClient`, or the parent agent's client when no model is specified) are NOT disposed by the manager.
- `SubAgentOptions.ClientFactory` XML doc now documents the ownership contract: clients returned by the factory are owned by `SubAgentManager` and disposed after the run; they should not be shared or reused.

## [0.13.0]

### Added

- **Sub-agents (sub-sessions)** — The main session can launch background sub-sessions via the `start_sub_agent` tool, each with an optional model, capability flags, and a specialized system prompt. The sub-agent's execution is backgrounded once a concurrency slot is acquired — the tool returns without awaiting completion, so the main session continues working or starts more sub-sessions up to `MaxConcurrentSubAgents` (default 4). When the limit is reached, the call waits for a slot to free (it never waits for sub-agent completion).
- **`await_sub_agents`** — Blocks until all (or listed) sub-sessions finish or time out, then returns their summaries plus status metadata (id, status, error, token counts) — never full sub-agent transcripts. Summaries are truncated to `MaxSummaryChars` (default 8000), keeping the main session's context clean — ideal for codebase analysis and large-text summarization.
- **`get_sub_agent_status`** — Polls progress (always returns a JSON array). **`list_sub_agent_models`** — Lets the agent discover which models the host has made available via `SubAgentOptions.AvailableModels` + `ClientFactory`.
- **Capability ceiling** — Sub-agents never exceed the parent agent's enabled capabilities: LLM-supplied `enable_bash`/`enable_file_writes` overrides are clamped by the parent's flags, snapshotted at manager creation; file operations and skills follow the manager defaults clamped the same way. Sub-agents run read-only by default (bash off, file writes off).
- **Lifecycle** — `CodingAgent` implements `IAsyncDisposable`; `DisposeAsync` cancels all running sub-agents (idempotent, safe under concurrent first use). New `CodingAgent.ActiveSubAgentManager` read-only inspection property. Cancelling an execution's `CancellationToken` does NOT cancel running sub-agents (they are owned by the manager, not the turn) — it only unblocks that turn's in-flight await/slot-wait.
- **Configuration snapshot** — The `SubAgentOptions` are defensively copied at first manager creation; later mutations have no effect.
- New `SharpCoder.SubAgents` namespace with public types: `SubAgentManager`, `SubAgentOptions`, `SubAgentRequest`, `SubAgentInfo`, `SubAgentStatus`, `SubAgentModelInfo`. New `AgentOptions.SubAgents` property to enable the feature. (`SubAgentTools` is an internal implementation detail — do NOT list it as public API.)

### Fixed

- `StreamWithToolCallsAsync` no longer swallows `OperationCanceledException` from tool calls into an error result — cancellation now propagates correctly when `ShowToolCallsInStream` is enabled.

### Note

Flat design — sub-agents cannot spawn their own sub-agents (planned for a future release).

## [0.12.1] - 2026-07-28

### Fixed

- **Tool calls lost from streaming results and session history** — The default `ExecuteStreamingAsync` path (`ShowToolCallsInStream = false`) rebuilt the `ChatResponse` from accumulated text only, discarding `FunctionCallContent`, `FunctionResultContent` and `TextReasoningContent`. Tools were still executed by `FunctionInvokingChatClient`, but `AgentResult.ToolCallCount` and `AgentSession.TotalToolCalls` always reported `0`, `AgentResult.Messages` contained no tool calls, and — most seriously — `session.MessageHistory` was left with only the user message and the final assistant text. Multi-turn sessions therefore had no memory of the tools the agent had invoked or their results. `BuildResponseFromUpdates` now uses `ToChatResponse()` to preserve the full message structure, with the round-separating display text moved to a dedicated `BuildDisplayText` helper so the final message formatting is unchanged. As a side effect, `AgentResult.Usage` now reflects usage aggregated across all tool rounds instead of only the last round. The `ShowToolCallsInStream = true` path was already correct and is unchanged.
- **Inflated `LastKnownContextTokens` from `FunctionInvokingChatClient` aggregate usage** — `CodingAgent` now uses a `UsageCapturingChatClient` wrapper to capture the final round-trip's `Usage.InputTokenCount` instead of the aggregate sum across all internal tool-call round-trips returned by `FunctionInvokingChatClient`. Previously, `session.LastKnownContextTokens` was set to the sum of all internal round-trip input tokens (e.g., 1.1M recorded vs ~186K actual context with 8 tool calls), causing `ContextCompactor` to trigger premature compaction at low real utilization (e.g., 19%), destroying conversation history that was still well within budget. The fix introduces a per-execution `UsageCapturingChatClient : DelegatingChatClient` that records the most recent round's input token count; `ExecuteAsync`, the default `ExecuteStreamingAsync` path, and `UpdateSession` all prefer this per-round value. The `ShowToolCallsInStream` path was already correct and is unchanged. Cumulative counters (`InputTokensUsed`/`OutputTokensUsed`) continue accumulating aggregate values.

## [0.12.0] - 2026-07-25

### Changed

- Version bump. Includes the `UsageCapturingChatClient` fix and NuGet package updates. Not formally released — superseded by v0.12.1.

## [0.11.0] - 2026-06-27

### Added

- **`ContextCompactor.CompactOldestPercentAsync`** — New partial compaction method that summarizes only the oldest X% of the session's token budget, leaving the newest portion verbatim. This is gentler than `ForceCompactAsync` (which compacts everything except the last `CompactionRetainRecent` messages) and produces a smaller compaction prompt. Accepts a `percent` parameter (1–95) controlling how much of the oldest content to summarize. Useful for long-running sessions where full compaction would lose too much detail.

## [0.10.0] - 2026-06-18

### Added

- **`AgentOptions.CompactionMaxTokens`** — New nullable `int?` property that specifies the compaction model's context window size. When set, enables chunked compaction: if old messages exceed this budget, they are split into token-budgeted chunks (75% of the budget), each summarized separately, and the per-chunk summaries are concatenated into one summary message. When null (default), falls back to `MaxContextTokens` and uses the existing single-call summarization path (no behavior change).

### Fixed

- **`ForceCompactAsync` exception propagation** — `ForceCompactAsync` now catches exceptions from the compaction LLM call, logs a warning, and returns `false` instead of propagating the exception to the caller. Previously, when the compaction model rejected an oversized summary prompt, the exception would crash the agent with no fallback.
- **CliAgent Ollama Cloud retries** — `examples\SharpCoder.CliAgent` now uses the existing resilient HTTP pipeline for Ollama Cloud runs while still forcing HTTP/1.1. This lets the example retry transient `503 Service Unavailable` responses from ollama.com instead of failing the run immediately.
- **CliAgent local-provider selection and retries** — `examples\SharpCoder.CliAgent` now routes non-Copilot model strings through `ChatClientFactory`, so `ollama-local/...` and `ollama-cloud/...` prefixes work as documented. Local Ollama runs also use the resilient HTTP pipeline, reducing failures against cloud-backed local models that intermittently return `503 Service Unavailable`.

## [0.9.0] - 2026-04-14

### Changed

- **Compaction code deduplication** — `ContextCompactor` now uses a single private `CompactMessageSliceAsync` core method for LLM summarization, summary building, and message list reconstruction. This consolidates logic that was previously duplicated across `CompactIfNeededAsync`, `ForceCompactAsync`, and the mid-loop `CompactIfNeededAsync` overload.

### Fixed

- **System message preservation** — `ContextCompactor` now preserves all consecutive leading `ChatRole.System` messages at the start of `session.MessageHistory` during both automatic (`CompactIfNeededAsync`) and force compaction (`ForceCompactAsync`). Callers no longer need to re-add the system prompt after compaction.

## [0.8.1] - 2026-04-13

### Changed

- Updated `Microsoft.Extensions.AI` NuGet package to `10.5.0`
- Updated `Microsoft.Extensions.Logging.Abstractions` NuGet package to `10.0.6`
- Updated `Microsoft.Extensions.AI.OpenAI` NuGet package to `10.5.0` (CliAgent example)
- Updated `Microsoft.Extensions.Logging` and `Microsoft.Extensions.Logging.Console` NuGet packages to `10.0.6` (CliAgent example)
- Updated `Microsoft.NET.Test.Sdk` NuGet package to `18.4.0` (test project)

## [0.8.0] - 2026-04-08

### Added

- **`AgentOptions.CompactionClient`** — New optional `IChatClient?` property to configure a dedicated LLM for context compaction summaries. When set, the compactor uses this client instead of the main `IChatClient`, allowing cheaper models to summarize old context. Falls back to the main client when not configured.

## [0.7.2] - 2026-04-06

### Fixed

- **Broadened context-overflow error detection** — `ContextCompactor.IsContextOverflowError()` now recognizes additional provider error variants beyond `model_max_prompt_tokens_exceeded`: `"context window exceeds ... limit"`, `"maximum context length"`, `"max prompt tokens"`, and `"prompt too long"`. This ensures automatic compaction recovery works across more LLM providers.

- **Stale token count in mid-loop compaction** — The live-message `CompactIfNeededAsync` overload (used during streaming tool loops) now uses `Math.Max(LastKnownContextTokens, heuristicEstimate)` as the estimated token count. Previously it used only the heuristic estimate, which could be significantly lower than the actual count after tool results were appended — causing compaction to not trigger when needed during long tool-call chains.

## [0.7.1]

### Fixed

- **Session corruption after mid-loop compaction** — `StreamWithToolCallsAsync` no longer re-appends response messages to the session after mid-loop compaction has already rebuilt the history. Previously, when compaction fired during a streaming tool loop, all pre-compaction messages were duplicated in the session, causing context overflow on subsequent iterations.

## [0.7.0] - 2026-04-02

### Added

- **`AgentOptions.OnCompacting`** — New optional `Action?` callback invoked immediately before context compaction begins (before the summarisation LLM call). Complements the existing `OnCompacted` callback and allows callers to show a live "compacting…" indicator in the UI. Fires in all three compaction paths: threshold-based, force-compact (overflow recovery), and mid-loop streaming compaction.

## [0.6.0] - 2026-03-31

### Added

- **`AgentSession.Fork()`** — Creates a deep copy of a session with a new session ID, zeroed token counters, and fresh timestamps. Message history is deep-copied via JSON serialization so mutations to either session don't affect the other. `LastKnownContextTokens` is preserved from the original. Accepts an optional custom `sessionId` parameter.

### Fixed

- **Version prefix double-beta** — Fixed `SharpCoder.csproj` version infrastructure that produced double-beta NuGet package versions (e.g. `0.5.0-beta-beta.42`). Changed from `<VersionSuffix>beta</VersionSuffix>` to CI-driven `--version-suffix` approach so release builds use the clean `<VersionPrefix>` value.

### Changed

- Bumped version from 0.5.0 to 0.6.0.

## [0.5.0] - 2026-03-29

### Added

- **Context overflow recovery** — When the API returns a `model_max_prompt_tokens_exceeded` error, the agent now automatically force-compacts the session and retries the request once. Previously, the agent would fail without attempting recovery.
- **`ContextCompactor.IsContextOverflowError()`** — New static helper method that detects context overflow errors in exception chains (searches message and all inner exceptions for `model_max_prompt_tokens_exceeded`).
- **`ContextCompactor.ForceCompactAsync()`** — New method that compacts session history unconditionally, regardless of token threshold. Used for recovery after context overflow errors. Respects `CompactionRetainRecent` and invokes `OnCompacted` callback on success.
- **`ContextCompactor.CompactIfNeededAsync(IList<ChatMessage>)` overload** — New overload that operates on a live messages list that may have diverged from `session.MessageHistory` during streaming tool loops. When compaction occurs, both the live list and session history are synchronized.
- **`AgentSession.LastKnownContextTokens`** — New property that tracks the exact input token count from the most recent API response. Updated after each API call and persisted across session save/load. Used by `ContextCompactor` for precise compaction decisions.

### Fixed

- **Mid-loop context compaction gap** — `StreamWithToolCallsAsync` now checks for context compaction after each tool execution round, before the next API call. Previously, compaction only occurred at the start of streaming, allowing large tool results (e.g. 50K tokens from web search) to blow past the context limit in subsequent rounds.
- **Stale token count in compaction decisions** — `AgentSession` now tracks `LastKnownContextTokens`, the exact input token count from the most recent API response (`response.Usage.InputTokenCount`). `ContextCompactor` uses this precise count instead of the heuristic `~4 chars per token` estimate when available. The heuristic remains as fallback before the first API call. This value persists across session save/load.

### Changed

- Improved resilience of long-running streaming sessions with large tool results by proactively compacting context between tool rounds.
- **Version infrastructure** — Changed from `<Version>` to `<VersionPrefix>` in `SharpCoder.csproj` to support CI-driven versioning. The base version is now `0.5.0` (was `0.4.4`). CI can append `-beta.N` suffix via `--version-suffix` for develop builds; release builds use the prefix as-is.
