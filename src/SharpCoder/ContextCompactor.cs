using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder;

/// <summary>
/// Compacts conversation history by summarizing old messages when context approaches token limits.
/// Preserves the system prompt and recent messages, replacing older ones with a summary.
/// </summary>
public sealed class ContextCompactor
{
    private readonly IChatClient _client;
    private readonly ILogger _logger;

    public ContextCompactor(IChatClient client, ILogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Compact message history if estimated tokens exceed the threshold.
    /// Returns true if compaction was performed.
    /// </summary>
    public async Task<bool> CompactIfNeededAsync(
        AgentSession session,
        AgentOptions options,
        CancellationToken ct = default)
    {
        if (!options.EnableAutoCompaction) return false;

        var threshold = (long)(options.MaxContextTokens * options.CompactionThreshold);
        var estimated = session.LastKnownContextTokens > 0
            ? session.LastKnownContextTokens
            : session.EstimatedContextTokens;

        if (estimated < threshold) return false;
        if (session.MessageHistory.Count <= options.CompactionRetainRecent + 1)
            return false; // Not enough messages to compact

        var tokensBefore = estimated;
        var messagesBefore = session.MessageHistory.Count;

        _logger.LogInformation(
            "Context compaction triggered: ~{Tokens} tokens (threshold: {Threshold}), {Messages} messages",
            estimated, threshold, messagesBefore);

        try
        {
            var (compacted, compactedMessages, oldCount) = await CompactMessageSliceAsync(
                session.MessageHistory, 0, options.CompactionRetainRecent, options,
                substituteNullSummary: true, ct);

            if (!compacted) return false;

            session.MessageHistory = new List<ChatMessage>(compactedMessages);

            _logger.LogInformation(
                "Compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
                oldCount, session.MessageHistory.Count, session.EstimatedContextTokens);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session.EstimatedContextTokens,
                messagesBefore, session.MessageHistory.Count));

            session.LastKnownContextTokens = 0;
            return true;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Context compaction failed, continuing with full history");
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the exception (or any inner exception in its chain)
    /// contains a string indicating the context window was exceeded, such as
    /// "model_max_prompt_tokens_exceeded", "context window exceeds limit",
    /// "maximum context length", "max prompt tokens", or "prompt too long".
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> if a context-overflow error was found; otherwise <c>false</c>.</returns>
    public static bool IsContextOverflowError(Exception? ex)
    {
        while (ex != null)
        {
            var msg = ex.Message;
            if (msg.Contains("model_max_prompt_tokens_exceeded", StringComparison.OrdinalIgnoreCase) ||
                (msg.Contains("context window exceeds", StringComparison.OrdinalIgnoreCase) && msg.Contains("limit", StringComparison.OrdinalIgnoreCase)) ||
                msg.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("max prompt tokens", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("prompt too long", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Compacts the session unconditionally, regardless of whether the token threshold
    /// has been reached. Useful when the API has already rejected the request due to
    /// context overflow. Respects <see cref="AgentOptions.CompactionRetainRecent"/>.
    /// Invokes the <see cref="AgentOptions.OnCompacted"/> callback on success.
    /// </summary>
    /// <param name="session">The session whose history should be compacted.</param>
    /// <param name="options">Agent options (retain-recent count, callback, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if compaction succeeded (summarization returned content);
    /// <c>false</c> if there was nothing to compact or summarization produced no content.
    /// Exceptions from the summarization client are propagated to the caller.
    /// </returns>
    public async Task<bool> ForceCompactAsync(
        AgentSession session,
        AgentOptions options,
        CancellationToken ct = default)
    {
        var messages = session.MessageHistory;

        if (messages.Count <= options.CompactionRetainRecent + 1)
            return false; // Not enough messages to compact

        var tokensBefore = session.EstimatedContextTokens;
        var messagesBefore = messages.Count;

        _logger.LogInformation(
            "Force context compaction: ~{Tokens} tokens, {Messages} messages",
            tokensBefore, messagesBefore);

        // Exceptions from the summarization client propagate to the caller.
        var (compacted, compactedMessages, oldCount) = await CompactMessageSliceAsync(
            messages, 0, options.CompactionRetainRecent, options,
            substituteNullSummary: false, ct);

        if (!compacted) return false;

        session.MessageHistory = new List<ChatMessage>(compactedMessages);

        _logger.LogInformation(
            "Force-compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
            oldCount, session.MessageHistory.Count, session.EstimatedContextTokens);

        options.OnCompacted?.Invoke(new CompactionResult(
            tokensBefore, session.EstimatedContextTokens,
            messagesBefore, session.MessageHistory.Count));

        session.LastKnownContextTokens = 0;
        return true;
    }

    /// <summary>
    /// Compact-if-needed overload that operates on a live <see cref="IList{ChatMessage}"/>
    /// that may have diverged from <see cref="AgentSession.MessageHistory"/> during a streaming
    /// tool loop. When compaction occurs, both the <paramref name="messages"/> list and
    /// <see cref="AgentSession.MessageHistory"/> are updated to reflect the new (compacted) state.
    /// </summary>
    /// <param name="session">The session to update if compaction occurs; may be <c>null</c>.</param>
    /// <param name="messages">
    /// The live message list (with system prompt at index 0) to estimate tokens from and compact.
    /// </param>
    /// <param name="options">Agent options governing thresholds and callbacks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if compaction was performed; otherwise <c>false</c>.</returns>
    public async Task<bool> CompactIfNeededAsync(
        AgentSession? session,
        IList<ChatMessage> messages,
        AgentOptions options,
        CancellationToken ct = default)
    {
        if (!options.EnableAutoCompaction) return false;

        // Use a safe comparison for the threshold check:
        // - When LastKnownContextTokens is available (>0), use it as the base.
        // - In mid-loop scenarios, the live messages list may have grown since the
        //   last API response (assistant reply + tool results appended), so we take the
        //   maximum of the exact count and the current heuristic estimate.
        // - When no exact count is available, fall back to heuristic estimation.
        long estimated;
        int startIndex = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;

        // Compute heuristic estimate from current message content
        long chars = 0;
        for (int i = startIndex; i < messages.Count; i++)
        {
            foreach (var content in messages[i].Contents)
            {
                if (content is TextContent tc)
                    chars += tc.Text?.Length ?? 0;
                else if (content is FunctionCallContent fc)
                    chars += (fc.Name?.Length ?? 0) + EstimateArgumentsLength(fc);
                else if (content is FunctionResultContent fr)
                    chars += EstimateResultLength(fr);
            }
        }
        var heuristicEstimate = chars / 4;

        if (session != null && session.LastKnownContextTokens > 0)
        {
            // Prefer exact count, but cap upward with current heuristic in case the
            // live list has grown past what the previous response reflected.
            estimated = Math.Max(session.LastKnownContextTokens, heuristicEstimate);
        }
        else
        {
            estimated = heuristicEstimate;
        }

        var threshold = (long)(options.MaxContextTokens * options.CompactionThreshold);
        if (estimated < threshold) return false;

        // Count non-system messages
        int nonSystemCount = messages.Count - startIndex;
        if (nonSystemCount <= options.CompactionRetainRecent + 1)
            return false;

        var tokensBefore = estimated;
        var messagesBefore = nonSystemCount;

        _logger.LogInformation(
            "Mid-loop context compaction triggered: ~{Tokens} tokens (threshold: {Threshold}), {Messages} non-system messages",
            estimated, threshold, messagesBefore);

        try
        {
            var (compacted, compactedMessages, oldCount) = await CompactMessageSliceAsync(
                messages, startIndex, options.CompactionRetainRecent, options,
                substituteNullSummary: true, ct);

            if (!compacted) return false;

            // Rebuild the live messages list in-place
            // Keep system prompt if present, then append compacted messages (summary + recent)
            while (messages.Count > startIndex)
                messages.RemoveAt(messages.Count - 1);

            foreach (var msg in compactedMessages)
                messages.Add(msg);

            // Sync session.MessageHistory to match
            if (session != null)
            {
                session.MessageHistory = new List<ChatMessage>(messages.Skip(startIndex));
            }

            var messagesAfter = messages.Count - startIndex;

            _logger.LogInformation(
                "Mid-loop compacted {OldCount} messages into summary. {NewCount} messages remaining",
                oldCount, messagesAfter);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session?.EstimatedContextTokens ?? 0,
                messagesBefore, messagesAfter));

            if (session != null)
                session.LastKnownContextTokens = 0;
            return true;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Mid-loop context compaction failed, continuing with full history");
            return false;
        }
    }

    /// <summary>
    /// Core compaction logic: computes the split, invokes the LLM to summarize the old messages,
    /// and returns the assembled compacted list (summary followed by recent messages).
    /// </summary>
    /// <param name="messages">The full message list to compact.</param>
    /// <param name="startIndex">
    /// Number of leading messages to skip (0 for session-based methods,
    /// 1 when a system prompt is present at index 0 in the mid-loop overload).
    /// </param>
    /// <param name="retainCount">Number of recent messages to preserve verbatim.</param>
    /// <param name="options">Agent options (used for <see cref="AgentOptions.OnCompacting"/>).</param>
    /// <param name="substituteNullSummary">
    /// When <c>true</c>, a null/whitespace LLM response is replaced with <c>"No summary available."</c>.
    /// When <c>false</c>, a null/whitespace LLM response causes this method to return
    /// <c>(false, empty)</c> so the caller can signal failure without mutating state.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of <c>(compacted, compactedMessages, oldCount)</c>. When <c>compacted</c> is
    /// <c>true</c>, <c>compactedMessages</c> contains a single summary message followed by the
    /// retained recent messages, and <c>oldCount</c> is the number of messages that were summarized.
    /// </returns>
    private async Task<(bool compacted, IList<ChatMessage> compactedMessages, int oldCount)>
        CompactMessageSliceAsync(
            IList<ChatMessage> messages,
            int startIndex,
            int retainCount,
            AgentOptions options,
            bool substituteNullSummary,
            CancellationToken ct)
    {
        int nonSkippedCount = messages.Count - startIndex;
        if (nonSkippedCount <= retainCount + 1)
            return (false, Array.Empty<ChatMessage>(), 0);

        var slice = messages.Skip(startIndex).ToList();
        var splitPoint = AdjustSplitPoint(slice, slice.Count - retainCount);
        if (splitPoint <= 0)
            return (false, Array.Empty<ChatMessage>(), 0);

        var oldMessages = slice.Take(splitPoint).ToList();
        var recentMessages = slice.Skip(splitPoint).ToList();

        var summaryPromptText = BuildSummaryPrompt(oldMessages);
        var summaryPromptMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, summaryPromptText)
        };

        options.OnCompacting?.Invoke();

        var summaryResponse = await _client.GetResponseAsync(summaryPromptMessages, cancellationToken: ct);
        var rawSummary = summaryResponse.Text;

        string summary;
        if (string.IsNullOrWhiteSpace(rawSummary))
        {
            if (!substituteNullSummary)
                return (false, Array.Empty<ChatMessage>(), 0);
            summary = "No summary available.";
        }
        else
        {
            summary = rawSummary!;
        }

        var compactedMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant,
                $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted]\n{summary}")
        };

        foreach (var msg in recentMessages)
            compactedMessages.Add(msg);

        return (true, compactedMessages, oldMessages.Count);
    }

    private static long EstimateArgumentsLength(FunctionCallContent fc)
    {
        if (fc.Arguments == null) return 0;
        long len = 0;
        foreach (var kvp in fc.Arguments)
        {
            len += kvp.Key?.Length ?? 0;
            len += kvp.Value?.ToString()?.Length ?? 0;
        }
        return len;
    }

    private static long EstimateResultLength(FunctionResultContent fr)
    {
        if (fr.Result == null) return 0;
        return fr.Result.ToString()?.Length ?? 0;
    }

    private static string BuildSummaryPrompt(IList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize the following conversation history concisely. Preserve:");
        sb.AppendLine("- Key decisions made and their rationale");
        sb.AppendLine("- Important findings and discoveries");
        sb.AppendLine("- Current state and progress");
        sb.AppendLine("- Any unresolved issues or pending work");
        sb.AppendLine("- File paths and code changes mentioned");
        sb.AppendLine();
        sb.AppendLine("Omit tool call details (read_file, grep, etc.) — only keep their results if important.");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var text = msg.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                // Summarize tool calls briefly
                var toolCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
                if (toolCalls.Count > 0)
                {
                    var names = string.Join(", ", toolCalls.Select(tc => tc.Name));
                    sb.AppendLine($"[{msg.Role}] Tool calls: {names}");
                    continue;
                }

                var toolResults = msg.Contents.OfType<FunctionResultContent>().ToList();
                if (toolResults.Count > 0)
                {
                    sb.AppendLine($"[{msg.Role}] Tool results returned");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Truncate very long messages
                var truncated = text.Length > 2000 ? text.Substring(0, 2000) + "..." : text;
                sb.AppendLine($"[{msg.Role}] {truncated}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Adjusts the split point so that tool result messages at the start of the "recent"
    /// portion are moved into the "old" portion. This prevents orphaned tool results
    /// (role=tool without a preceding assistant tool_calls) which crash some LLM APIs.
    /// </summary>
    public static int AdjustSplitPoint(IList<ChatMessage> messages, int splitPoint)
    {
        if (splitPoint < 0) splitPoint = 0;
        if (splitPoint >= messages.Count) return messages.Count;

        // Move the split forward past any tool result messages at the boundary
        while (splitPoint < messages.Count &&
               messages[splitPoint].Contents.OfType<FunctionResultContent>().Any())
        {
            splitPoint++;
        }

        return splitPoint;
    }
}
