using System;
using System.Collections.Generic;

namespace SharpCoder;

/// <summary>
/// Represents the kind of streaming update emitted during agent execution.
/// </summary>
public enum StreamingUpdateKind
{
    /// <summary>A chunk of text from the assistant's response.</summary>
    TextDelta,

    /// <summary>The agent has completed execution.</summary>
    Completed,

    /// <summary>
    /// A tool call is about to be invoked locally. Carries <see cref="StreamingUpdate.ToolCall"/>.
    /// Only emitted when <see cref="AgentOptions.EmitStructuredToolUpdates"/> is enabled.
    /// </summary>
    ToolCallStarted,

    /// <summary>
    /// A locally invoked tool call has finished (successfully or with an error). Carries
    /// <see cref="StreamingUpdate.ToolCall"/> with <see cref="ToolCallUpdate.Result"/> populated.
    /// Only emitted when <see cref="AgentOptions.EmitStructuredToolUpdates"/> is enabled.
    /// </summary>
    ToolCallCompleted,

    /// <summary>
    /// A model round has ended and the agent is about to start another one. Carries
    /// <see cref="StreamingUpdate.Progress"/>. Only emitted when
    /// <see cref="AgentOptions.EmitStructuredToolUpdates"/> is enabled.
    /// </summary>
    RoundProgress,
}

/// <summary>Structured detail about one locally invoked tool call in a streaming execution.</summary>
public sealed class ToolCallUpdate
{
    /// <summary>The model-visible name of the tool.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The function call id, when the provider supplied one.</summary>
    public string? CallId { get; set; }

    /// <summary>A read-only snapshot of the arguments the model supplied.</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; set; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Full tool result text, when <see cref="ToolCallUpdate"/> is attached to a
    /// <see cref="StreamingUpdateKind.ToolCallCompleted"/> update. Null on a started update.
    /// Not truncated — hosts decide how much to display.
    /// </summary>
    public string? Result { get; set; }

    /// <summary>True when the invocation threw, or the tool name was unknown.</summary>
    public bool IsError { get; set; }

    /// <summary>Wall-clock duration of the invocation.</summary>
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Progress detail for a model round in a streaming execution.</summary>
public sealed class RoundProgressUpdate
{
    /// <summary>Zero-based index of the round that just ended.</summary>
    public int Round { get; set; }

    /// <summary>Number of tool calls executed so far in this execution.</summary>
    public int ToolCallsSoFar { get; set; }

    /// <summary>Approximate tool-call budget from <see cref="AgentOptions.MaxSteps"/>.</summary>
    public int MaxSteps { get; set; }
}

/// <summary>
/// An incremental update yielded during streaming agent execution.
/// Use <see cref="Kind"/> to determine which properties are populated.
/// </summary>
public sealed class StreamingUpdate
{
    /// <summary>The type of this update.</summary>
    public StreamingUpdateKind Kind { get; }

    /// <summary>
    /// Text content. Populated when <see cref="Kind"/> is <see cref="StreamingUpdateKind.TextDelta"/>.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Final execution result. Populated when <see cref="Kind"/> is <see cref="StreamingUpdateKind.Completed"/>.
    /// </summary>
    public AgentResult? Result { get; }

    /// <summary>
    /// Structured tool-call detail. Populated for <see cref="StreamingUpdateKind.ToolCallStarted"/>
    /// and <see cref="StreamingUpdateKind.ToolCallCompleted"/>.
    /// </summary>
    public ToolCallUpdate? ToolCall { get; }

    /// <summary>
    /// Round progress. Populated for <see cref="StreamingUpdateKind.RoundProgress"/>.
    /// </summary>
    public RoundProgressUpdate? Progress { get; }

    private StreamingUpdate(
        StreamingUpdateKind kind,
        string? text,
        AgentResult? result,
        ToolCallUpdate? toolCall,
        RoundProgressUpdate? progress)
    {
        Kind = kind;
        Text = text;
        Result = result;
        ToolCall = toolCall;
        Progress = progress;
    }

    /// <summary>Creates a text delta update containing a chunk of the assistant's response.</summary>
    /// <param name="text">Text chunk carried by the update.</param>
    /// <returns>A text-delta update containing <paramref name="text"/>.</returns>
    public static StreamingUpdate TextDelta(string text) =>
        new(StreamingUpdateKind.TextDelta, text, null, null, null);

    /// <summary>Creates a completion update with the final agent result.</summary>
    /// <param name="result">Final result of the agent execution.</param>
    /// <returns>A completion update carrying <paramref name="result"/>.</returns>
    public static StreamingUpdate Completed(AgentResult result) =>
        new(StreamingUpdateKind.Completed, null, result, null, null);

    /// <summary>Creates a structured "tool call started" update.</summary>
    /// <param name="detail">Tool-call detail.</param>
    /// <returns>An update carrying <paramref name="detail"/>.</returns>
    public static StreamingUpdate ToolStarted(ToolCallUpdate detail) =>
        new(StreamingUpdateKind.ToolCallStarted, null, null, detail, null);

    /// <summary>Creates a structured "tool call completed" update.</summary>
    /// <param name="detail">Tool-call detail, with <see cref="ToolCallUpdate.Result"/> populated.</param>
    /// <returns>An update carrying <paramref name="detail"/>.</returns>
    public static StreamingUpdate ToolFinished(ToolCallUpdate detail) =>
        new(StreamingUpdateKind.ToolCallCompleted, null, null, detail, null);

    /// <summary>Creates a round-progress update.</summary>
    /// <param name="progress">Progress detail.</param>
    /// <returns>An update carrying <paramref name="progress"/>.</returns>
    public static StreamingUpdate RoundProgress(RoundProgressUpdate progress) =>
        new(StreamingUpdateKind.RoundProgress, null, null, null, progress);
}
