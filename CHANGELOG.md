# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.5.0] - 2026-03-29

### Added

- **Context overflow recovery** — When the API returns a `model_max_prompt_tokens_exceeded` error, the agent now automatically force-compacts the session and retries the request once. Previously, the agent would fail without attempting recovery.
- **`ContextCompactor.IsContextOverflowError()`** — New static helper method that detects context overflow errors in exception chains (searches message and all inner exceptions for `model_max_prompt_tokens_exceeded`).
- **`ContextCompactor.ForceCompactAsync()`** — New method that compacts session history unconditionally, regardless of token threshold. Used for recovery after context overflow errors. Respects `CompactionRetainRecent` and invokes `OnCompacted` callback on success.
- **`ContextCompactor.CompactIfNeededAsync(IList<ChatMessage>)` overload** — New overload that operates on a live messages list that may have diverged from `session.MessageHistory` during streaming tool loops. When compaction occurs, both the live list and session history are synchronized.
- **`AgentSession.LastKnownContextTokens`** — New property that tracks the exact input token count from the most recent API response. Updated after each API call and persisted across session save/load. Used by `ContextCompactor` for precise compaction decisions.

### Fixed

- **Mid-loop context compaction gap** — `StreamWithToolCallsAsync` now checks for context compaction after each tool execution round, before the next API call. Previously, compaction only occurred at the start of streaming, allowing large tool results (e.g., 50K tokens from web search) to blow past the context limit in subsequent rounds.
- **Stale token count in compaction decisions** — `AgentSession` now tracks `LastKnownContextTokens`, the exact input token count from the most recent API response (`response.Usage.InputTokenCount`). `ContextCompactor` uses this precise count instead of the heuristic `~4 chars per token` estimate when available. The heuristic remains as fallback before the first API call. This value persists across session save/load.

### Changed

- Improved resilience of long-running streaming sessions with large tool results by proactively compacting context between tool rounds.
- **Version infrastructure** — Changed from `<Version>` to `<VersionPrefix>` in `SharpCoder.csproj` to support CI-driven versioning. The base version is now `0.5.0` (was `0.4.4`). CI can append `-beta.N` suffix via `--version-suffix` for develop builds; release builds use the prefix as-is.
