using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using SharpCoder.SubAgents;

namespace SharpCoder;

public sealed class AgentResult
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// All messages from the conversation, including tool calls and results.
    /// </summary>
    public IList<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

    /// <summary>
    /// The model ID that produced the response, if reported by the provider.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// The finish reason reported by the provider (e.g. "stop", "length").
    /// </summary>
    public ChatFinishReason? FinishReason { get; set; }

    /// <summary>
    /// <c>UsageDetails</c> of the response the provider returned for the final call of this
    /// execution, if it reported any. This is the OLD meaning of usage on a result: for a
    /// single-round execution it is the usage of that one round, and on the paths where
    /// <c>FunctionInvokingChatClient</c> performs several internal round trips it is the provider's
    /// aggregate for that final request. It is kept unchanged for compatibility — use
    /// <see cref="TokenUsage"/> for SharpCoder's own per-call accounting (all rounds, all sources,
    /// call counts and "was it reported?" counters).
    /// </summary>
    public UsageDetails? Usage { get; set; }

    /// <summary>
    /// Per-call token usage of this execution: every model call recorded, summed per source and
    /// model, with the call count. SharpCoder populates it with a detached snapshot taken when the
    /// result was built, so later activity never mutates it. Populated for <c>Success</c>,
    /// <c>MaxStepsReached</c> and <c>Error</c> results (including usage recorded before the
    /// failure); it stays empty only when no call was recorded.
    /// <para>
    /// A snapshot is detached, so it is a point-in-time view: sub-agents started by this execution
    /// keep forwarding their calls into this execution's recorder after it returned, and those calls
    /// are recorded into the session and reported through <see cref="AgentOptions.OnUsage"/>, but
    /// they never change this already-returned snapshot. To see a sub-agent's own totals, read
    /// <see cref="SubAgentInfo.Usage"/> (or <c>input_tokens</c>/<c>output_tokens</c> from
    /// <c>await_sub_agents</c>).
    /// </para>
    /// </summary>
    public UsageSummary TokenUsage { get; set; } = new UsageSummary();

    /// <summary>
    /// Total number of tool calls made during the conversation.
    /// </summary>
    public int ToolCallCount { get; set; }

    /// <summary>
    /// Diagnostic snapshot of everything sent to the LLM.
    /// Populated before the call, so available even on failure.
    /// </summary>
    public SessionDiagnostics? Diagnostics { get; set; }

    /// <summary>
    /// Counts tool calls across all messages in the conversation.
    /// </summary>
    internal static int CountToolCalls(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Count();
}
