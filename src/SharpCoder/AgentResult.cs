using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;

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
    /// Token usage for the final response, if reported by the provider.
    /// </summary>
    public UsageDetails? Usage { get; set; }

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
