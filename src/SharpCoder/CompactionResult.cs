namespace SharpCoder;

/// <summary>
/// Contains details about a completed context compaction event.
/// Passed to <see cref="AgentOptions.OnCompacted"/> after the compactor runs.
/// </summary>
public sealed class CompactionResult
{
    /// <summary>Estimated token count before compaction.</summary>
    public long TokensBefore { get; }

    /// <summary>Estimated token count after compaction.</summary>
    public long TokensAfter { get; }

    /// <summary>Number of messages before compaction.</summary>
    public int MessagesBefore { get; }

    /// <summary>Number of messages after compaction.</summary>
    public int MessagesAfter { get; }

    /// <summary>Percentage of tokens reduced (0–100). Zero if <see cref="TokensBefore"/> is zero.</summary>
    public int ReductionPercent => TokensBefore > 0
        ? (int)Math.Round((1.0 - (double)TokensAfter / TokensBefore) * 100)
        : 0;

    /// <summary>Creates a snapshot of token and message counts before and after compaction.</summary>
    /// <param name="tokensBefore">Estimated token count before compaction.</param>
    /// <param name="tokensAfter">Estimated token count after compaction.</param>
    /// <param name="messagesBefore">Number of messages before compaction.</param>
    /// <param name="messagesAfter">Number of messages after compaction.</param>
    public CompactionResult(long tokensBefore, long tokensAfter, int messagesBefore, int messagesAfter)
    {
        TokensBefore = tokensBefore;
        TokensAfter = tokensAfter;
        MessagesBefore = messagesBefore;
        MessagesAfter = messagesAfter;
    }
}
