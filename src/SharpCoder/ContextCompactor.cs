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
        var estimated = session.EstimatedContextTokens;

        if (estimated < threshold) return false;
        if (session.MessageHistory.Count <= options.CompactionRetainRecent + 1)
            return false; // Not enough messages to compact

        var tokensBefore = estimated;
        var messagesBefore = session.MessageHistory.Count;

        _logger.LogInformation(
            "Context compaction triggered: ~{Tokens} tokens (threshold: {Threshold}), {Messages} messages",
            estimated, threshold, session.MessageHistory.Count);

        var messages = session.MessageHistory;
        var retainCount = options.CompactionRetainRecent;

        // Split: old messages to summarize, recent messages to keep.
        // Adjust the split point so we never orphan tool results — a tool result
        // message must always be preceded by an assistant message with tool_calls.
        var splitPoint = AdjustSplitPoint(messages, messages.Count - retainCount);
        if (splitPoint <= 0)
            return false; // No messages to compact after adjustment
        var oldMessages = messages.Take(splitPoint).ToList();
        var recentMessages = messages.Skip(splitPoint).ToList();

        // Build summary of old messages
        var summaryText = BuildSummaryPrompt(oldMessages);
        var summaryMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, summaryText)
        };

        try
        {
            var summaryResponse = await _client.GetResponseAsync(summaryMessages, cancellationToken: ct);
            var summary = summaryResponse.Text ?? "No summary available.";

            // Replace old messages with a single summary message
            session.MessageHistory = new List<ChatMessage>();
            session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
                $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted]\n{summary}"));

            foreach (var msg in recentMessages)
                session.MessageHistory.Add(msg);

            _logger.LogInformation(
                "Compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
                oldMessages.Count, session.MessageHistory.Count, session.EstimatedContextTokens);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session.EstimatedContextTokens,
                messagesBefore, session.MessageHistory.Count));

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
    /// contains the string <c>model_max_prompt_tokens_exceeded</c>, which indicates
    /// the request was rejected because the context window was too large.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> if a context-overflow error was found; otherwise <c>false</c>.</returns>
    public static bool IsContextOverflowError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex.Message.Contains("model_max_prompt_tokens_exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
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

        var retainCount = options.CompactionRetainRecent;
        var splitPoint = AdjustSplitPoint(messages, messages.Count - retainCount);
        if (splitPoint <= 0)
            return false;

        var oldMessages = messages.Take(splitPoint).ToList();
        var recentMessages = messages.Skip(splitPoint).ToList();

        var summaryText = BuildSummaryPrompt(oldMessages);
        var summaryMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, summaryText)
        };

        // Allow exceptions to propagate — the caller decides how to handle failures.
        var summaryResponse = await _client.GetResponseAsync(summaryMessages, cancellationToken: ct);
        var summary = summaryResponse.Text;

        if (string.IsNullOrWhiteSpace(summary))
            return false;

        session.MessageHistory = new List<ChatMessage>();
        session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted]\n{summary}"));
        foreach (var msg in recentMessages)
            session.MessageHistory.Add(msg);

        _logger.LogInformation(
            "Force-compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
            oldMessages.Count, session.MessageHistory.Count, session.EstimatedContextTokens);

        options.OnCompacted?.Invoke(new CompactionResult(
            tokensBefore, session.EstimatedContextTokens,
            messagesBefore, session.MessageHistory.Count));

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

        // Estimate tokens from the live messages list (excluding system prompt at index 0)
        long chars = 0;
        int startIndex = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
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
        var estimated = chars / 4;

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
            estimated, threshold, nonSystemCount);

        var retainCount = options.CompactionRetainRecent;
        // Work on non-system slice of the list
        var nonSystemMessages = messages.Skip(startIndex).ToList();
        var splitPoint = AdjustSplitPoint(nonSystemMessages, nonSystemMessages.Count - retainCount);
        if (splitPoint <= 0)
            return false;

        var oldMessages = nonSystemMessages.Take(splitPoint).ToList();
        var recentMessages = nonSystemMessages.Skip(splitPoint).ToList();

        var summaryText = BuildSummaryPrompt(oldMessages);
        var summaryPromptMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, summaryText)
        };

        try
        {
            var summaryResponse = await _client.GetResponseAsync(summaryPromptMessages, cancellationToken: ct);
            var summary = summaryResponse.Text ?? "No summary available.";

            var summaryMessage = new ChatMessage(ChatRole.Assistant,
                $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted]\n{summary}");

            // Rebuild the live messages list in-place
            // Keep system prompt if present, then summary, then recent
            while (messages.Count > startIndex)
                messages.RemoveAt(messages.Count - 1);

            messages.Add(summaryMessage);
            foreach (var msg in recentMessages)
                messages.Add(msg);

            // Sync session.MessageHistory to match
            if (session != null)
            {
                session.MessageHistory = new List<ChatMessage>(messages.Skip(startIndex));
            }

            var messagesAfter = messages.Count - startIndex;

            _logger.LogInformation(
                "Mid-loop compacted {OldCount} messages into summary. {NewCount} messages remaining",
                oldMessages.Count, messagesAfter);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session?.EstimatedContextTokens ?? 0,
                messagesBefore, messagesAfter));

            return true;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Mid-loop context compaction failed, continuing with full history");
            return false;
        }
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
