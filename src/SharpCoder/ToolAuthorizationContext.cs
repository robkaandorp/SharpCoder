using System;
using System.Collections.Generic;

namespace SharpCoder;

/// <summary>
/// One locally invokable tool call, presented to <see cref="AgentOptions.AuthorizeTool"/> before the
/// call is executed.
/// <para>
/// The instance is created by SharpCoder for a single invocation and is immutable: the tool name is
/// copied from the guarded function, the correlation from the value captured when the agent was
/// constructed, and <see cref="Arguments"/> is a defensive read-only copy of the arguments the model
/// supplied — so a host can authorize concrete file paths and shell commands, not only tool
/// categories, and mutating the copy never affects the call that follows.
/// </para>
/// </summary>
public sealed class ToolAuthorizationContext
{
    /// <summary>Creates a context; only SharpCoder creates these.</summary>
    /// <param name="toolName">The model-visible function name that is about to run.</param>
    /// <param name="correlationId">The host correlation captured for this agent, or <c>null</c>.</param>
    /// <param name="arguments">The read-only copy of the model-supplied arguments.</param>
    internal ToolAuthorizationContext(
        string toolName,
        string? correlationId,
        IReadOnlyDictionary<string, object?> arguments)
    {
        ToolName = toolName;
        CorrelationId = correlationId;
        Arguments = arguments;
    }

    /// <summary>
    /// The name of the tool that is about to be invoked — the same name the model used, for example
    /// <c>execute_bash_command</c>, <c>read_file</c>, <c>write_file</c>, <c>load_skill</c>,
    /// <c>start_sub_agent</c>, or the name of a custom tool.
    /// </summary>
    public string ToolName { get; }

    /// <summary>
    /// The host correlation captured from <see cref="AgentOptions.ToolAuthorizationCorrelationId"/>
    /// when this agent was constructed, so the host can authorize per user or per context. The value
    /// is fixed for the lifetime of the agent: later changes to the options affect only new agents.
    /// </summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// A read-only, defensively copied snapshot of the arguments the model passed to the tool
    /// (for example <c>filePath</c>, <c>command</c>, or <c>task</c>). The dictionary and the model
    /// cannot change it while the host inspects it, and the host cannot change the arguments of the
    /// call it is authorizing. Empty when the tool takes no arguments.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; }
}
