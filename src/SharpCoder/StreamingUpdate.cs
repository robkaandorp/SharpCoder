namespace SharpCoder;

/// <summary>
/// Represents the kind of streaming update emitted during agent execution.
/// </summary>
public enum StreamingUpdateKind
{
    /// <summary>A chunk of text from the assistant's response.</summary>
    TextDelta,

    /// <summary>The agent has completed execution.</summary>
    Completed
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

    private StreamingUpdate(StreamingUpdateKind kind, string? text, AgentResult? result)
    {
        Kind = kind;
        Text = text;
        Result = result;
    }

    /// <summary>Creates a text delta update containing a chunk of the assistant's response.</summary>
    public static StreamingUpdate TextDelta(string text) =>
        new(StreamingUpdateKind.TextDelta, text, null);

    /// <summary>Creates a completion update with the final agent result.</summary>
    public static StreamingUpdate Completed(AgentResult result) =>
        new(StreamingUpdateKind.Completed, null, result);
}
