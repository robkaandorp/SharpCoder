# SharpCoder — Copilot Instructions

SharpCoder is an embeddable autonomous coding-agent library for .NET, built on
`Microsoft.Extensions.AI`. It wraps any `IChatClient` (OpenAI, Ollama, Azure,
Anthropic, …) in an agent loop with file/search/bash tools, multi-turn sessions,
streaming, and automatic context compaction. It is shipped as the `SharpCoder`
NuGet package.

## Solution layout

- `src/SharpCoder/` — the library (the NuGet package). Target: `netstandard2.1`,
  `LangVersion 12.0`, nullable enabled, **`TreatWarningsAsErrors=true`**.
- `tests/SharpCoder.Tests/` — xUnit v3 tests. Target: `net10.0`.
  `InternalsVisibleTo` is granted so tests can touch internals.
- `examples/SharpCoder.CliAgent/` — runnable sample CLI agent.
- Solution file is `SharpCoder.slnx` (new XML format; use it, not a `.sln`).

## Build / test / run

Use the solution file at the repo root — all commands run against `SharpCoder.slnx`:

```bash
dotnet restore
dotnet build   --configuration Release
dotnet test    --configuration Release --no-build
```

Run a **single test** (xUnit v3 via `Microsoft.NET.Test.Sdk`):

```bash
dotnet test tests/SharpCoder.Tests --filter "FullyQualifiedName~CodingAgentTests.MethodName"
# or by display name
dotnet test tests/SharpCoder.Tests --filter "DisplayName~fragment"
```

Run the example CLI:

```bash
dotnet run --project examples/SharpCoder.CliAgent
```

There is no separate lint step — `TreatWarningsAsErrors` in both csproj files
means any new warning fails the build. CI (`.github/workflows/nuget-publish.yml`)
runs restore → build → test on `ubuntu-latest` with the .NET 10 SDK.

## Architecture (the big picture)

The agent loop is orchestrated by `CodingAgent` in `src/SharpCoder/`. Five files
form the core and are tightly coupled — change one, check the others:

- **`CodingAgent.cs`** — the loop. `ExecuteAsync` / `ExecuteStreamingAsync`
  build a `ChatOptions` with tools, call the `IChatClient`, execute any tool
  calls, append results to the session, and repeat up to `MaxSteps`. Streaming
  reuses the same pipeline with mid-loop compaction between tool rounds.
- **`AgentOptions.cs`** — single configuration surface. New behavior is almost
  always exposed as an `AgentOptions` property (see `OnCompacting`,
  `CompactionClient`, `ReasoningEffort`, `CustomTools`, …). Feature flags
  (`EnableBash`, `EnableFileOps`, `EnableFileWrites`, `EnableSkills`) gate which
  tools are registered with the LLM.
- **`AgentSession.cs`** — multi-turn state: `MessageHistory`, cumulative token
  counters, `LastKnownContextTokens` (exact input-token count from the most
  recent API response, used by the compactor), `Save/LoadAsync` (JSON), and
  `Fork()` (deep-copy via JSON; resets ID + counters, preserves history and
  `LastKnownContextTokens`).
- **`ContextCompactor.cs`** — summarizes old messages when tokens exceed
  `CompactionThreshold × MaxContextTokens`. Has three entry points:
  threshold-based `CompactIfNeededAsync`, the live-list overload used during
  streaming tool loops, and `ForceCompactAsync` for overflow recovery. On a
  `model_max_prompt_tokens_exceeded`-class error (detected by
  `IsContextOverflowError`, which walks the inner-exception chain and matches
  several provider-specific phrasings) the agent force-compacts and retries once.
  Uses `CompactionClient` if set, else the main `IChatClient`.
- **`AgentResult.cs` / `SessionDiagnostics.cs` / `StreamingUpdate.cs`** — return
  types. `Diagnostics` is snapshotted **before** the LLM call so it is available
  even on failure.

Tools live in `src/SharpCoder/Tools/` (`FileTools.cs`, `BashTools.cs`,
`SkillTools.cs`) and are registered via `AIFunctionFactory.Create(...)` inside
`CodingAgent.BuildChatOptions()`. All file operations are confined to
`AgentOptions.WorkDirectory` — never introduce a tool that escapes it.

## Conventions specific to this codebase

- **Public API changes go through `AgentOptions`.** Don't add constructor
  parameters to `CodingAgent`; add a property to `AgentOptions` with a sensible
  default that preserves existing behavior (backward compat is a stated goal of
  every minor release — see `CHANGELOG.md`).
- **Target stays `netstandard2.1`** for the library. Do not introduce APIs that
  require a newer BCL, and do not add `net*` TFMs to `src/SharpCoder`. The test
  project is `net10.0` and may use anything.
- **No new top-level dependencies** in `src/SharpCoder.csproj` without a reason —
  the library intentionally ships with only `Microsoft.Extensions.AI` and
  `Microsoft.Extensions.Logging.Abstractions`.
- **Compaction safety:** when you touch the streaming tool loop, remember that
  compaction rebuilds `session.MessageHistory` in place. `StreamWithToolCallsAsync`
  must **not** re-append response messages after mid-loop compaction (prior bug;
  see changelog `0.7.1`). Sync the live messages list from the session after
  compaction, not the other way around.
- **Token accounting:** prefer `session.LastKnownContextTokens` over the
  `~4 chars per token` heuristic — use `Math.Max(LastKnownContextTokens, heuristic)`
  in hot paths so estimates don't regress after large tool results (`0.7.2`).
- **Logging** uses `ILogger` from `Microsoft.Extensions.Logging.Abstractions`;
  fall back to `NullLogger.Instance` when `AgentOptions.Logger` is null.
- **Tests** use xUnit v3 with global `using Xunit;`. Tests typically inject a
  fake `IChatClient` (see `FixedResponseClient` / `StreamingResponseClient`
  nested in `CodingAgentTests.cs`) rather than mocking — follow that pattern.
- **Changelog discipline:** every user-visible change is recorded in
  `CHANGELOG.md` under a Keep-a-Changelog heading with
  Added/Changed/Fixed sections. Bump `<VersionPrefix>` in
  `src/SharpCoder/SharpCoder.csproj` for releases; CI appends `-beta.N` on the
  `develop` branch via `--version-suffix`. Do not set `<VersionSuffix>` in the
  csproj (caused `0.5.0-beta-beta.42`-style double-beta; see `0.6.0`).
- **Skills:** agent-loadable skills live under `.github/skills/` as Markdown
  files with YAML frontmatter (`name`, `description`). The agent auto-loads
  `AGENTS.md` and `.github/copilot-instructions.md` into the system prompt when
  `AutoLoadWorkspaceInstructions = true`.

## Release flow

- Push to `develop` → CI publishes a `-beta.<run_number>` prerelease to NuGet.
- Tag `vX.Y.Z` on `main`/`master` → CI publishes the clean `X.Y.Z` package.
- Never commit a populated `<VersionSuffix>`; let CI provide it.
