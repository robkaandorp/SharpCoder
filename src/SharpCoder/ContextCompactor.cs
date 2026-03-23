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

        // Split: old messages to summarize, recent messages to keep
        var splitPoint = messages.Count - retainCount;
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
}
