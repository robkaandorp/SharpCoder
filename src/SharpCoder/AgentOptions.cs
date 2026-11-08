using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using SharpCoder.SubAgents;

namespace SharpCoder;

/// <summary>Configures the tools, prompts, model behavior, and context management for a <see cref="CodingAgent"/>.</summary>
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

    /// <summary>Approximate tool-call budget; enforcement depends on the execution path. The streaming tool path checks the budget between model rounds but processes each round's full tool-call batch, so it can run beyond the configured value. Defaults to 25; reaching the path's limit returns a result with status <c>MaxStepsReached</c>.</summary>
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

    /// <summary>Registers tools for reading files and searching paths and contents. File paths are checked for lexical containment within <see cref="WorkDirectory"/>, but symlinks and reparse points are not resolved, so a workspace link can allow a read or search to reach a file outside the workspace. Defaults to <see langword="true"/>.</summary>
    public bool EnableFileOps { get; set; } = true;
    /// <summary>Registers <c>write_file</c> and <c>edit_file</c> in addition to the read/search tools when <see cref="EnableFileOps"/> is enabled. Defaults to <see langword="true"/>.</summary>
    public bool EnableFileWrites { get; set; } = true;
    /// <summary>Registers tools for listing and loading workspace skills from <c>.github/skills</c>. Defaults to <see langword="true"/>.</summary>
    public bool EnableSkills { get; set; } = true;
    
    // System Prompt settings
    /// <summary>Replaces the built-in system prompt when non-empty; custom instructions and enabled workspace instructions are appended separately.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Optional instructions appended under a <c>Custom Instructions</c> heading in the system prompt.</summary>
    public string? CustomInstructions { get; set; }

    /// <summary>When enabled (the default), appends workspace instruction files found under the work directory to the system prompt.</summary>
    public bool AutoLoadWorkspaceInstructions { get; set; } = true;

    // Tools
    /// <summary>Additional model tools registered alongside the enabled built-in tools.</summary>
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

    /// <summary>Logger used for agent, tool, and compaction diagnostics; defaults to <see cref="NullLogger.Instance"/>.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Optional callback invoked exactly once for every model call the agent records, as soon as
    /// that call ends — however it ends (a completed response, a failure, or a stream disposed
    /// early; a call that reported no usage arrives with zero tokens). Use it to report usage live.
    /// <para>
    /// Events arrive from every source SharpCoder produces: agent-loop rounds
    /// (<see cref="UsageSource.Agent"/>) and context-compaction summary calls
    /// (<see cref="UsageSource.Compaction"/>) — including compaction a host triggers directly
    /// through <see cref="ContextCompactor.ForceCompactAsync(AgentSession, AgentOptions, CancellationToken)"/>, <see cref="ContextCompactor.CompactOldestPercentAsync(AgentSession, AgentOptions, int, CancellationToken)"/>
    /// or <see cref="ContextCompactor.CompactIfNeededAsync(AgentSession, AgentOptions, CancellationToken)"/>,
    /// and every chunk of chunked summarisation — plus every model call a sub-agent makes, which is
    /// forwarded live by <see cref="SubAgentManager"/> as <see cref="UsageSource.SubAgent"/>, or as
    /// <see cref="UsageSource.SubAgentCompaction"/> for a compaction call inside a sub-agent, with
    /// the sub-agent's id in <see cref="UsageEvent.SubAgentId"/>. Sub-agent events are recorded into
    /// the session and reported here whenever they arrive, including after the parent execution
    /// returned.
    /// </para>
    /// <para>
    /// <strong>Handlers must be thread-safe.</strong> Sub-agents run on background threads, so this
    /// callback can be invoked concurrently from several threads at once — from this agent's own
    /// execution and from every running sub-agent that has calls ending at the same moment — and an
    /// invocation can arrive after the enclosing <c>ExecuteAsync</c> call has already returned.
    /// Guard handler state accordingly.
    /// </para>
    /// <para>
    /// The callback is invoked outside any internal lock, and an exception it throws is caught and
    /// logged: it can never fail or otherwise alter the run.
    /// </para>
    /// </summary>
    public Action<UsageEvent>? OnUsage { get; set; }

    /// <summary>
    /// Optional async authorization callback invoked immediately before every locally invokable tool
    /// SharpCoder registers for the model — custom tools, <c>execute_bash_command</c>,
    /// <c>read_file</c>/<c>glob</c>/<c>grep</c>, <c>write_file</c>/<c>edit_file</c>,
    /// <c>load_skill</c>/<c>list_skills</c>, and the sub-agent tools (<c>start_sub_agent</c>,
    /// <c>await_sub_agents</c>, <c>get_sub_agent_status</c>, <c>list_sub_agent_models</c>).
    /// Return <see cref="ToolAuthorizationDecision.Allow"/> to let the call run, or
    /// <see cref="ToolAuthorizationDecision.Deny(string)"/> to refuse it before it produces any side
    /// effect.
    /// <para>
    /// <b>The captured pair is fixed for the agent's lifetime.</b> The delegate and
    /// <see cref="ToolAuthorizationCorrelationId"/> are captured when each
    /// <see cref="CodingAgent"/> is constructed. Later changes to this property — setting it,
    /// clearing it, or replacing it — and later changes to
    /// <see cref="ToolAuthorizationCorrelationId"/> affect only agents constructed afterwards; an
    /// existing agent (and every execution it runs) keeps the pair it captured.
    /// </para>
    /// <para>
    /// <b>Denials and faults.</b> A denial is never executed: the model receives a refusal message
    /// containing the <see cref="ToolAuthorizationDecision.Reason"/> you supplied — that reason is
    /// host-approved, <b>model-visible</b> text, so never put secrets, stack traces, or internal
    /// host state in it; when it is <c>null</c>/blank the model receives a fixed, host-neutral
    /// refusal. A callback that throws (or that returns <c>null</c>) fails <b>closed</b>: the tool
    /// does not run and the model receives a fixed refusal that never contains the exception's type,
    /// message, or stack trace — on both the manual streaming tool loop and the
    /// <c>FunctionInvokingChatClient</c> path (which is configured with
    /// <c>IncludeDetailedErrors = true</c>). An <see cref="OperationCanceledException"/> is
    /// cancellation, not a fault, and propagates as cancellation. The caller's
    /// <see cref="System.Threading.CancellationToken"/> is passed to the callback and, on allow, to
    /// the tool unchanged.
    /// </para>
    /// <para>
    /// <b>Scope of the guarantee.</b> Only the tools SharpCoder itself registers in the model's
    /// <c>ChatOptions</c> pass through this boundary. These are <b>trusted host entry points</b>
    /// that this callback never sees, and that a host must authorize itself if it exposes them:
    /// <c>FileTools</c>/<c>BashTools</c>/<c>SkillTools</c> instances used directly, the original
    /// unwrapped custom tool instances a host keeps its own reference to, and the public
    /// <see cref="SubAgents.SubAgentManager.StartAsync(SubAgents.SubAgentRequest, System.Threading.CancellationToken)"/>.
    /// </para>
    /// <para>
    /// <b>Handlers must be thread-safe.</b> This callback can be invoked concurrently: a parent
    /// agent's tools and the tools of background sub-agents it started can call it at the same
    /// moment, from different threads. It is invoked outside any internal lock.
    /// </para>
    /// <para>
    /// When this property is <c>null</c> (the default) every tool is registered exactly as before —
    /// same names, schemas, descriptions and behavior, with no wrapper in the chain. When it is set,
    /// every custom tool in <see cref="CustomTools"/> must be an <see cref="AIFunction"/>: a
    /// non-invokable custom tool (for example an <see cref="AIFunctionDeclaration"/>, or a
    /// provider-executed tool) cannot be guarded locally, so building the model request fails closed
    /// by throwing <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    public Func<ToolAuthorizationContext, CancellationToken, Task<ToolAuthorizationDecision>>? AuthorizeTool { get; set; }

    /// <summary>
    /// Optional immutable host correlation — typically a user, tenant, or conversation id — passed to
    /// <see cref="AuthorizeTool"/> on every guarded tool call through
    /// <see cref="ToolAuthorizationContext.CorrelationId"/>, so a host can authorize per user or per
    /// context rather than per tool name alone.
    /// <para>
    /// The value is captured when each <see cref="CodingAgent"/> is constructed (a
    /// <see cref="string"/> is immutable, so the captured reference cannot change afterwards), and it
    /// stays fixed for that agent's lifetime and all of its executions. Because
    /// <see cref="string"/> cannot be mutated in place, replacing this property after construction
    /// affects only agents constructed afterwards — an existing agent keeps the value it captured.
    /// </para>
    /// <para>
    /// The correlation is host metadata only: SharpCoder stores it but never interprets, parses, or
    /// logs it beyond a diagnostic message written when a call is refused.
    /// </para>
    /// </summary>
    public string? ToolAuthorizationCorrelationId { get; set; }

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

    /// <summary>
    /// Maximum context window size in tokens for the compaction model.
    /// When null (default), falls back to <see cref="MaxContextTokens"/>.
    /// Set this to the compaction model's actual context window to enable
    /// chunked compaction — if old messages exceed this budget, they are
    /// summarized in chunks that each fit within this limit.
    /// </summary>
    public int? CompactionMaxTokens { get; set; }

    /// <summary>
    /// Optional sub-agent configuration. When set, the agent gains start_sub_agent,
    /// await_sub_agents, get_sub_agent_status, and list_sub_agent_models tools that
    /// spawn background sub-sessions and return only their summaries.
    /// Sub-agents can never receive capabilities that are disabled on this parent
    /// options instance (bash, file ops, file writes, skills) — LLM-supplied
    /// overrides are clamped by the parent's enabled capabilities, snapshotted at
    /// manager creation.
    /// The configuration is defensively snapshotted the first time a sub-agent
    /// manager is created; later mutations of this property or the SubAgentOptions
    /// object have no effect on an already-established manager.
    /// Mutating this property concurrently with running executions is not supported
    /// (safe windows: before the first execution, or between executions).
    /// Hosts shut down sub-agents exclusively through <see cref="CodingAgent.DisposeAsync"/>;
    /// never dispose the manager obtained from <see cref="CodingAgent.ActiveSubAgentManager"/> directly.
    /// </summary>
    public SubAgentOptions? SubAgents { get; set; }
}
