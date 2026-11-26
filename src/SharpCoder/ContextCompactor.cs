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

    /// <summary>Creates a compactor that uses the specified chat client for summaries.</summary>
    /// <param name="client">The chat client used to generate summaries.</param>
    /// <param name="logger">Optional logger for compaction diagnostics; when <see langword="null"/>, logging is disabled.</param>
    public ContextCompactor(IChatClient client, ILogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Routes a compaction operation's summary calls through the single shared recording mechanism:
    /// every call the compaction model answers — or fails — is recorded exactly once with
    /// <see cref="UsageSource.Compaction"/> and the compaction client's model.
    /// </summary>
    /// <param name="recorder">
    /// The execution-local recorder of the <see cref="CodingAgent"/> execution that triggered this
    /// compaction, when there is one, so the compaction shows up in that execution's
    /// <see cref="AgentResult.TokenUsage"/>. <c>null</c> (host-initiated or direct compaction) makes
    /// the compactor report through its own recorder instead. A recorder supplied by the caller
    /// already carries that execution's session, so it decides where the calls are recorded; the
    /// <paramref name="session"/> parameter only matters for the compactor's own fallback recorder.
    /// </param>
    /// <param name="session">The session being compacted, so its cumulative usage is updated per call; may be <c>null</c>.</param>
    /// <param name="options">Agent options supplying the host's <see cref="AgentOptions.OnUsage"/> callback.</param>
    /// <returns>The recorder that receives this operation's compaction calls.</returns>
    private UsageRecorder ResolveRecorder(UsageRecorder? recorder, AgentSession? session, AgentOptions options)
        => recorder ?? new UsageRecorder(options, session, _logger);

    /// <summary>
    /// Wraps the compaction client in the shared recording wrapper, so every summary call of one
    /// compaction operation is recorded at call end — completion, failure, or no usage reported at
    /// all (which still counts as a call with zero tokens).
    /// </summary>
    /// <param name="recorder">The recorder that receives the compaction calls.</param>
    private IChatClient CreateRecordingClient(UsageRecorder recorder)
        => new UsageRecordingChatClient(_client, recorder, UsageSource.Compaction);

    /// <summary>
    /// Compact message history if estimated tokens exceed the threshold.
    /// Returns true if compaction was performed.
    /// <para>
    /// The summarisation call (or calls, when the old history is summarized in chunks) is recorded
    /// with <see cref="UsageSource.Compaction"/> into <paramref name="session"/>'s cumulative usage
    /// and the host's <see cref="AgentOptions.OnUsage"/> callback.
    /// </para>
    /// <para>
    /// A single-call summarization failure is logged as a warning and gives <c>false</c>, leaving
    /// the history unchanged. For chunked summarization, behavior depends on
    /// <c>substituteNullSummary</c>: when <c>true</c>, a failed chunk is logged and its section is
    /// replaced with the fallback text "No summary available for this section." before continuing;
    /// the history may include that fallback. When <c>false</c>, a failed chunk gives <c>false</c>
    /// without substituting a fallback.
    /// </para>
    /// </summary>
    /// <param name="session">The session whose history should be compacted.</param>
    /// <param name="options">Agent options governing thresholds and callbacks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if compaction was performed; otherwise <c>false</c>.</returns>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when a summarization call throws while <paramref name="ct"/> is cancelled. In that
    /// case, history, <see cref="AgentSession.LastKnownContextTokens"/> and
    /// <see cref="AgentOptions.OnCompacted"/> are untouched; the attempted call is still recorded
    /// in usage. No-op paths may return <c>false</c> with an already-cancelled token. Exceptions
    /// from <see cref="AgentOptions.OnCompacting"/> or <see cref="AgentOptions.OnCompacted"/> are
    /// not covered by this guarantee. A client-originated <see cref="OperationCanceledException"/>
    /// while <paramref name="ct"/> is not cancelled retains the existing summarization-failure handling.
    /// </exception>
    public Task<bool> CompactIfNeededAsync(
        AgentSession session,
        AgentOptions options,
        CancellationToken ct = default)
        => CompactIfNeededAsync(session, options, recorder: null, ct);

    /// <summary>
    /// Overload of <see cref="CompactIfNeededAsync(AgentSession, AgentOptions, CancellationToken)"/>
    /// that reports this operation's compaction calls to the supplied execution-local
    /// <paramref name="recorder"/> (creating its own when <c>null</c>), so compaction performed
    /// during an agent execution also appears in that execution's <see cref="AgentResult.TokenUsage"/>.
    /// </summary>
    internal async Task<bool> CompactIfNeededAsync(
        AgentSession session,
        AgentOptions options,
        UsageRecorder? recorder,
        CancellationToken ct)
    {
        if (!options.EnableAutoCompaction) return false;

        var threshold = (long)(options.MaxContextTokens * options.CompactionThreshold);
        var estimated = session.LastKnownContextTokens > 0
            ? session.LastKnownContextTokens
            : session.EstimatedContextTokens;

        if (estimated < threshold) return false;

        var startIndex = FindStartIndex(session.MessageHistory);
        if (session.MessageHistory.Count - startIndex <= options.CompactionRetainRecent)
            return false; // Not enough messages to compact beyond the retained floor

        var tokensBefore = estimated;
        var messagesBefore = session.MessageHistory.Count - startIndex;

        _logger.LogInformation(
            "Context compaction triggered: ~{Tokens} tokens (threshold: {Threshold}), {Messages} messages",
            estimated, threshold, messagesBefore);

        try
        {
            var (compacted, compactedMessages, oldCount, tokensElided) = await CompactMessageSliceAsync(
                session.MessageHistory, startIndex, null, options,
                substituteNullSummary: true, ResolveRecorder(recorder, session, options), ct);

            if (!compacted) return false;

            var systemPrefix = session.MessageHistory.Take(startIndex).ToList();
            session.MessageHistory = new List<ChatMessage>(systemPrefix.Concat(compactedMessages));

            _logger.LogInformation(
                "Compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
                oldCount, session.MessageHistory.Count - startIndex, session.EstimatedContextTokens);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session.EstimatedContextTokens,
                messagesBefore, session.MessageHistory.Count - startIndex)
                { TokensElided = tokensElided });

            session.LastKnownContextTokens = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation during summarization is not a compaction failure: propagate it.
            // For summary-call cancellation, history has not been mutated; callback exceptions
            // are not covered by that guarantee.
            throw;
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

    private static int FindStartIndex(IList<ChatMessage> messages)
    {
        int i = 0;
        while (i < messages.Count && messages[i].Role == ChatRole.System)
            i++;
        return i;
    }

    /// <summary>
    /// Compacts the oldest <paramref name="percent"/> percent of the non-system message history,
    /// preserving the remaining recent messages verbatim. Unlike <see cref="ForceCompactAsync(AgentSession, AgentOptions, CancellationToken)"/>,
    /// this method determines the split point by token-weighted percentage rather than by a fixed
    /// recent-message count. This is useful for callers (such as CopilotHive) that want to trim
    /// the oldest portion of the conversation without knowing the model's exact context budget.
    /// </summary>
    /// <param name="session">The session whose history should be compacted.</param>
    /// <param name="options">Agent options (callback, retain-recent safety minimum, etc.).</param>
    /// <param name="percent">Percentage of non-system messages to compact, in the range 1–95.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if compaction succeeded; otherwise <c>false</c> when there are too few messages
    /// to compact, summarization produced no content, or summarization failed. A single-call
    /// summarization failure is logged as a warning. For chunked summarization, behavior depends
    /// on <c>substituteNullSummary</c>: when <c>false</c>, a failed chunk returns <c>false</c>
    /// without logging or substituting a fallback; when <c>true</c>, the failed chunk is logged,
    /// its section is replaced with the fallback text "No summary available for this section.",
    /// and processing continues.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="percent"/> is less than 1 or greater than 95.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when a summarization call throws while <paramref name="ct"/> is cancelled. In that
    /// case, history, <see cref="AgentSession.LastKnownContextTokens"/> and
    /// <see cref="AgentOptions.OnCompacted"/> are untouched; the attempted call is still recorded
    /// in usage. No-op paths may return <c>false</c> with an already-cancelled token. Exceptions
    /// from <see cref="AgentOptions.OnCompacting"/> or <see cref="AgentOptions.OnCompacted"/> are
    /// not covered by this guarantee. A client-originated <see cref="OperationCanceledException"/>
    /// while <paramref name="ct"/> is not cancelled retains the existing summarization-failure handling.
    /// </exception>
    public Task<bool> CompactOldestPercentAsync(
        AgentSession session,
        AgentOptions options,
        int percent,
        CancellationToken ct = default)
        => CompactOldestPercentAsync(session, options, percent, recorder: null, ct);

    /// <summary>
    /// Overload of <see cref="CompactOldestPercentAsync(AgentSession, AgentOptions, int, CancellationToken)"/>
    /// that reports this operation's compaction calls to the supplied execution-local
    /// <paramref name="recorder"/> (creating its own when <c>null</c>), so compaction performed
    /// during an agent execution also appears in that execution's <see cref="AgentResult.TokenUsage"/>.
    /// </summary>
    internal async Task<bool> CompactOldestPercentAsync(
        AgentSession session,
        AgentOptions options,
        int percent,
        UsageRecorder? recorder,
        CancellationToken ct)
    {
        if (percent < 1 || percent > 95)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 1 and 95.");

        var messages = session.MessageHistory;
        var startIndex = FindStartIndex(messages);

        if (messages.Count - startIndex <= options.CompactionRetainRecent)
            return false; // Not enough messages to compact beyond the retained floor

        var tokensBefore = session.EstimatedContextTokens;
        var messagesBefore = messages.Count - startIndex;

        // Estimate per-message tokens for non-system messages
        var perMessageTokens = new List<(int index, long tokens)>();
        long totalTokens = 0;
        for (int i = startIndex; i < messages.Count; i++)
        {
            var estimate = EstimateMessageTokens(messages[i]);
            perMessageTokens.Add((i, estimate));
            totalTokens += estimate;
        }

        // Find the split point where the accumulated token count reaches the requested percentage
        long targetTokens = totalTokens * percent / 100;
        long accumulated = 0;
        int splitPoint = startIndex;
        for (int i = 0; i < perMessageTokens.Count; i++)
        {
            accumulated += perMessageTokens[i].tokens;
            if (accumulated >= targetTokens)
            {
                splitPoint = perMessageTokens[i].index + 1;
                break;
            }
        }

        // Guard (NOT a clamp): if the percent-derived old portion is too small to be
        // worth compacting, return false. We do NOT expand the old portion to satisfy
        // a recent-message minimum — the split point stays purely percent-derived.
        var oldCount = splitPoint - startIndex;
        if (oldCount <= 0 || oldCount < options.CompactionRetainRecent + 1)
            return false;

        _logger.LogInformation(
            "Compacting oldest {Percent}% of context: ~{Tokens} tokens, {Messages} messages, split at index {Split}",
            percent, tokensBefore, messagesBefore, splitPoint);

        try
        {
            var (compacted, compactedMessages, summaryOldCount, tokensElided) = await CompactMessageSliceAsync(
                messages, startIndex, messages.Count - splitPoint, options,
                substituteNullSummary: false, ResolveRecorder(recorder, session, options), ct);

            if (!compacted) return false;

            var systemPrefix = messages.Take(startIndex).ToList();
            session.MessageHistory = new List<ChatMessage>(systemPrefix.Concat(compactedMessages));

            _logger.LogInformation(
                "Compacted oldest {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
                summaryOldCount, session.MessageHistory.Count - startIndex, session.EstimatedContextTokens);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session.EstimatedContextTokens,
                messagesBefore, session.MessageHistory.Count - startIndex)
                { TokensElided = tokensElided });

            session.LastKnownContextTokens = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation during summarization is not a compaction failure: propagate it.
            // For summary-call cancellation, history has not been mutated; callback exceptions
            // are not covered by that guarantee.
            throw;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Oldest-percent context compaction failed, returning false");
            return false;
        }
    }

    private static long EstimateMessageTokens(ChatMessage message)
        => UsageEstimator.EstimateMessagesTokens(new[] { message });

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
    /// <c>false</c> if there was nothing to compact, summarization produced no content, or
    /// summarization failed. A single-call summarization exception is caught, logged as a warning,
    /// and gives <c>false</c>; a failed chunked summarization call (with
    /// <c>substituteNullSummary</c> set to <c>false</c>) is caught and gives <c>false</c> without
    /// logging.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when a summarization call throws while <paramref name="ct"/> is cancelled. In that
    /// case, history, <see cref="AgentSession.LastKnownContextTokens"/> and
    /// <see cref="AgentOptions.OnCompacted"/> are untouched; the attempted call is still recorded
    /// in usage. No-op paths may return <c>false</c> with an already-cancelled token. Exceptions
    /// from <see cref="AgentOptions.OnCompacting"/> or <see cref="AgentOptions.OnCompacted"/> are
    /// not covered by this guarantee. A client-originated <see cref="OperationCanceledException"/>
    /// while <paramref name="ct"/> is not cancelled retains the existing summarization-failure handling.
    /// </exception>
    public Task<bool> ForceCompactAsync(
        AgentSession session,
        AgentOptions options,
        CancellationToken ct = default)
        => ForceCompactAsync(session, options, recorder: null, ct);

    /// <summary>
    /// Overload of <see cref="ForceCompactAsync(AgentSession, AgentOptions, CancellationToken)"/>
    /// that reports this operation's compaction calls to the supplied execution-local
    /// <paramref name="recorder"/> (creating its own when <c>null</c>), so the force-compaction of a
    /// context-overflow retry appears in the triggering execution's <see cref="AgentResult.TokenUsage"/>.
    /// </summary>
    internal async Task<bool> ForceCompactAsync(
        AgentSession session,
        AgentOptions options,
        UsageRecorder? recorder,
        CancellationToken ct)
    {
        var messages = session.MessageHistory;

        var startIndex = FindStartIndex(messages);
        if (messages.Count - startIndex <= options.CompactionRetainRecent)
            return false; // Not enough messages to compact beyond the retained floor

        var tokensBefore = session.EstimatedContextTokens;
        var messagesBefore = messages.Count - startIndex;

        _logger.LogInformation(
            "Force context compaction: ~{Tokens} tokens, {Messages} messages",
            tokensBefore, messagesBefore);

        try
        {
            // Single-call summarization exceptions are caught and logged instead of
            // propagating to the caller. A failed chunk returns false without logging;
            // an OperationCanceledException while ct is cancelled is rethrown by the
            // filtered catch below.
            var (compacted, compactedMessages, oldCount, tokensElided) = await CompactMessageSliceAsync(
                messages, startIndex, null, options,
                substituteNullSummary: false, ResolveRecorder(recorder, session, options), ct);

            if (!compacted) return false;

            session.MessageHistory = new List<ChatMessage>(messages.Take(startIndex).Concat(compactedMessages));

            _logger.LogInformation(
                "Force-compacted {OldCount} messages into summary. {NewCount} messages remaining (~{Tokens} tokens)",
                oldCount, session.MessageHistory.Count - startIndex, session.EstimatedContextTokens);

            options.OnCompacted?.Invoke(new CompactionResult(
                tokensBefore, session.EstimatedContextTokens,
                messagesBefore, session.MessageHistory.Count - startIndex)
                { TokensElided = tokensElided });

            session.LastKnownContextTokens = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation during summarization is not a compaction failure: propagate it.
            // For summary-call cancellation, history has not been mutated; callback exceptions
            // are not covered by that guarantee.
            throw;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Force context compaction failed, returning false");
            return false;
        }
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
    /// <returns>
    /// <c>true</c> if compaction was performed. For a single-call summarization failure, the
    /// failure is logged as a warning and the method returns <c>false</c>. For chunked
    /// summarization, behavior depends on <c>substituteNullSummary</c>: when <c>true</c>, a failed
    /// chunk is logged, its section is replaced with the fallback text
    /// "No summary available for this section.", and processing continues; the method may return
    /// <c>true</c> with that fallback in the compacted history. When <c>false</c>, a failed chunk
    /// returns <c>false</c> without substituting a fallback.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when a summarization call throws while <paramref name="ct"/> is cancelled. In that
    /// case, the history, <paramref name="messages"/>, the session's
    /// <see cref="AgentSession.LastKnownContextTokens"/> and <see cref="AgentOptions.OnCompacted"/>
    /// are untouched; the attempted call is still recorded in usage. No-op paths may return
    /// <c>false</c> with an already-cancelled token. Exceptions from
    /// <see cref="AgentOptions.OnCompacting"/> or <see cref="AgentOptions.OnCompacted"/> are not
    /// covered by this guarantee. A client-originated <see cref="OperationCanceledException"/>
    /// while <paramref name="ct"/> is not cancelled retains the existing summarization-failure handling.
    /// </exception>
    public Task<bool> CompactIfNeededAsync(
        AgentSession? session,
        IList<ChatMessage> messages,
        AgentOptions options,
        CancellationToken ct = default)
        => CompactIfNeededAsync(session, messages, options, recorder: null, ct);

    /// <summary>
    /// Overload of <see cref="CompactIfNeededAsync(AgentSession?, IList{ChatMessage}, AgentOptions, CancellationToken)"/>
    /// that reports this operation's compaction calls to the supplied execution-local
    /// <paramref name="recorder"/> (creating its own when <c>null</c>). When
    /// <paramref name="session"/> is <c>null</c> the calls are reported only to
    /// <see cref="AgentOptions.OnUsage"/> and the recorder's own summary — there is no session to
    /// update.
    /// </summary>
    internal async Task<bool> CompactIfNeededAsync(
        AgentSession? session,
        IList<ChatMessage> messages,
        AgentOptions options,
        UsageRecorder? recorder,
        CancellationToken ct)
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

        // Compute heuristic estimate from current message content (skipping any system prefix)
        var heuristicEstimate = UsageEstimator.EstimateMessagesTokens(messages.Skip(startIndex));

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
        if (nonSystemCount <= options.CompactionRetainRecent)
            return false;

        var tokensBefore = estimated;
        var messagesBefore = nonSystemCount;

        _logger.LogInformation(
            "Mid-loop context compaction triggered: ~{Tokens} tokens (threshold: {Threshold}), {Messages} non-system messages",
            estimated, threshold, messagesBefore);

        try
        {
            var (compacted, compactedMessages, oldCount, tokensElided) = await CompactMessageSliceAsync(
                messages, startIndex, null, options,
                substituteNullSummary: true, ResolveRecorder(recorder, session, options), ct);

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
                messagesBefore, messagesAfter)
                { TokensElided = tokensElided });

            if (session != null)
                session.LastKnownContextTokens = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation during summarization is not a compaction failure: propagate it.
            // For summary-call cancellation, history and the live list have not been mutated;
            // callback exceptions are not covered by that guarantee.
            throw;
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
    /// <param name="retainCountOverride">
    /// Exact number of recent messages to keep verbatim (used by percentage-based compaction);
    /// when <c>null</c>, the tail is sized by the token budget (<see cref="AgentOptions.CompactionRetainTokens"/>),
    /// never smaller than <see cref="AgentOptions.CompactionRetainRecent"/> messages.
    /// </param>
    /// <param name="options">Agent options (used for <see cref="AgentOptions.OnCompacting"/>).</param>
    /// <param name="substituteNullSummary">
    /// When <c>true</c>, a null/whitespace LLM response is replaced with <c>"No summary available."</c>.
    /// When <c>false</c>, a null/whitespace LLM response causes this method to return
    /// <c>(false, empty)</c> so the caller can signal failure without mutating state.
    /// </param>
    /// <param name="recorder">
    /// The recorder for this compaction operation's summary calls; every call (each chunk
    /// included) is recorded exactly once with <see cref="UsageSource.Compaction"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of <c>(compacted, compactedMessages, oldCount, tokensElided)</c>. When <c>compacted</c> is
    /// <c>true</c>, <c>compactedMessages</c> contains the compacted history followed by the
    /// retained recent messages, <c>oldCount</c> is the number of messages compacted away, and
    /// <c>tokensElided</c> is how many tokens were reclaimed by tool-result elision (0 when elision
    /// is disabled, found nothing, or was not reached because summarization produced the history).
    /// When elision alone brings the context under the threshold, no summary message is produced.
    /// </returns>
    private async Task<(bool compacted, IList<ChatMessage> compactedMessages, int oldCount, long tokensElided)>
        CompactMessageSliceAsync(
            IList<ChatMessage> messages,
            int startIndex,
            int? retainCountOverride,
            AgentOptions options,
            bool substituteNullSummary,
            UsageRecorder recorder,
            CancellationToken ct)
    {
        var slice = messages.Skip(startIndex).ToList();
        if (slice.Count <= options.CompactionRetainRecent)
            return (false, Array.Empty<ChatMessage>(), 0, 0);

        int splitPoint;
        if (retainCountOverride is int retainCount)
        {
            splitPoint = AdjustSplitPoint(slice, Math.Max(0, slice.Count - retainCount));
        }
        else
        {
            splitPoint = AdjustSplitPoint(slice, TokenBudgetSplitIndex(
                slice, RetainBudget(options), options.CompactionRetainRecent));
        }
        if (splitPoint <= 0)
            return (false, Array.Empty<ChatMessage>(), 0, 0);

        var oldMessages = slice.Take(splitPoint).ToList();
        var recentMessages = slice.Skip(splitPoint).ToList();

        // Stage 1 (optional): elide large tool-result bodies and attachments from the old slice.
        // When elision alone brings the context back under the threshold, no summary call is
        // needed at all: the history is rewritten with stubs in place of the bulky results.
        // Otherwise the slice is summarized exactly as before — the summarizer's prompt drops
        // result bodies anyway, so pre-eliding it would not help.
        var threshold = (long)(options.MaxContextTokens * options.CompactionThreshold);
        long tokensElided = 0;
        if (options.EnableToolResultElision)
        {
            var (elidedMessages, reclaimed, elidedItems) = ElideBulkyContents(oldMessages);
            if (reclaimed > 0
                && EstimateTokens(elidedMessages) + EstimateTokens(recentMessages) <= threshold)
            {
                var elidedOnly = new List<ChatMessage>(elidedMessages.Count + recentMessages.Count);
                elidedOnly.AddRange(elidedMessages);
                elidedOnly.AddRange(recentMessages);
                _logger.LogInformation(
                    "Elided {Items} bulky items from the compacted range (~{Tokens:n0} tokens reclaimed); no summary call needed",
                    elidedItems, reclaimed);
                options.OnCompacting?.Invoke();
                return (true, elidedOnly, oldMessages.Count, reclaimed);
            }
        }

        options.OnCompacting?.Invoke();

        // Budget is 75% of the compaction model's context window. When no dedicated
        // compaction limit is configured, fall back to the main model's limit.
        var budget = (long)((options.CompactionMaxTokens ?? options.MaxContextTokens) * 0.75);
        var oldTokens = EstimateTokens(oldMessages);

        int chunkCount;
        string summary;

        if (oldTokens > budget)
        {
            var (success, chunkedSummary, chunksUsed) = await SummarizeInChunksAsync(
                oldMessages, (int)budget, options, substituteNullSummary, recorder, ct);

            if (!success)
                return (false, Array.Empty<ChatMessage>(), 0, 0);

            summary = chunkedSummary;
            chunkCount = chunksUsed;
        }
        else
        {
            // Single-call path: preserve exact existing behavior and output format.
            chunkCount = 1;
            var summaryPromptText = BuildSummaryPrompt(oldMessages);
            var summaryPromptMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, summaryPromptText)
            };

            // The summary call goes through the shared recording wrapper (Source = Compaction), so
            // it is recorded exactly once when it ends — completion or failure alike.
            var summaryResponse = await CreateRecordingClient(recorder)
                .GetResponseAsync(summaryPromptMessages, cancellationToken: ct);
            var rawSummary = summaryResponse.Text;

            if (string.IsNullOrWhiteSpace(rawSummary))
            {
                if (!substituteNullSummary)
                    return (false, Array.Empty<ChatMessage>(), 0, 0);
                summary = "No summary available.";
            }
            else
            {
                summary = rawSummary!;
            }
        }

        string header = chunkCount > 1
            ? $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted in {chunkCount} chunks]"
            : $"[CONTEXT SUMMARY — {oldMessages.Count} messages compacted]";

        var compactedMessages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.Assistant, $"{header}\n{summary}")
        };

        foreach (var msg in recentMessages)
            compactedMessages.Add(msg);

        return (true, compactedMessages, oldMessages.Count, tokensElided);
    }

    /// <summary>
    /// Token budget for the verbatim tail retained by compaction: the smaller of
    /// <see cref="AgentOptions.CompactionRetainTokens"/> and half the compaction threshold
    /// (which guarantees a full half-threshold of headroom after compacting). At least 1.
    /// </summary>
    private static long RetainBudget(AgentOptions options)
    {
        long threshold = (long)(options.MaxContextTokens * options.CompactionThreshold);
        long cap = Math.Max(1, threshold / 2);
        return Math.Max(1, Math.Min(options.CompactionRetainTokens, cap));
    }

    /// <summary>
    /// Computes the split index for a token-budgeted retained tail: walks from the newest
    /// message backwards until adding one more would exceed <paramref name="budgetTokens"/>,
    /// but never keeps fewer than <paramref name="minKeep"/> messages. Returns 0 when the whole
    /// slice fits the budget (nothing worth compacting).
    /// </summary>
    private static int TokenBudgetSplitIndex(IList<ChatMessage> slice, long budgetTokens, int minKeep)
    {
        long tokens = 0;
        int kept = 0;
        for (int i = slice.Count - 1; i >= 0; i--)
        {
            long t = UsageEstimator.EstimateMessagesTokens(new[] { slice[i] });
            if (kept >= minKeep && tokens + t > budgetTokens) break;
            tokens += t;
            kept++;
        }
        return slice.Count - kept;
    }

    /// <summary>Smallest tool-result body (in characters) considered worthwhile to elide.</summary>
    private const int MinElideChars = 256;

    /// <summary>
    /// Replaces bulky, re-fetchable content in <paramref name="slice"/> with one-line stubs:
    /// tool-result bodies of at least <see cref="MinElideChars"/> characters become a stub naming
    /// the tool (and a truncated argument hint taken from the matching call) and the original size;
    /// image/PDF attachments become a one-line stub. Call ids are preserved, so tool-call pairing
    /// stays valid and the model can re-run a tool to recover the content. User text, assistant
    /// text and reasoning content are never touched. The input list is not mutated.
    /// Returns the rewritten list, the estimated tokens reclaimed, and the number of items elided.
    /// </summary>
    private static (List<ChatMessage> Messages, long TokensReclaimed, int ItemsElided) ElideBulkyContents(
        IReadOnlyList<ChatMessage> slice)
    {
        // Map call id -> (tool name, argument hint) so stubs can say what was omitted and how to get it back.
        var callInfo = new Dictionary<string, (string Name, string? Hint)>(StringComparer.Ordinal);
        foreach (var msg in slice)
        foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
            if (fc.CallId is { } id && !callInfo.ContainsKey(id))
                callInfo[id] = (fc.Name ?? "tool", FormatArgumentsHint(fc.Arguments));

        var result = new List<ChatMessage>(slice.Count);
        long reclaimed = 0;
        int elided = 0;
        foreach (var msg in slice)
        {
            List<AIContent>? rebuilt = null;
            for (int i = 0; i < msg.Contents.Count; i++)
            {
                var content = msg.Contents[i];
                switch (content)
                {
                    case FunctionResultContent fr when UsageEstimator.EstimateResultLength(fr) >= MinElideChars:
                        var (name, hint) = callInfo.TryGetValue(fr.CallId ?? string.Empty, out var info)
                            ? info
                            : ("tool", (string?)null);
                        long original = UsageEstimator.EstimateResultLength(fr);
                        string stub = $"[tool result elided: {name}{(hint is null ? string.Empty : "(" + hint + ")")} - {original} chars - re-run the tool to see the contents]";
                        reclaimed += Math.Max(0, original - stub.Length) / 4;
                        elided++;
                        (rebuilt ??= new List<AIContent>(msg.Contents))[i] = new FunctionResultContent(fr.CallId ?? string.Empty, stub);
                        break;
                    case DataContent dc when UsageEstimator.IsImageOrPdf(dc):
                        reclaimed += AgentSession.ImageTokenEstimate;
                        elided++;
                        (rebuilt ??= new List<AIContent>(msg.Contents))[i] =
                            new TextContent($"[{dc.MediaType} attachment elided - re-attach if needed]");
                        break;
                }
            }

            if (rebuilt is null)
            {
                result.Add(msg);
            }
            else
            {
                result.Add(new ChatMessage(msg.Role, rebuilt)
                {
                    AuthorName = msg.AuthorName,
                    CreatedAt = msg.CreatedAt,
                });
            }
        }

        return (result, reclaimed, elided);
    }

    /// <summary>Renders a short, single-line argument hint (truncated at 80 characters) for elision stubs.</summary>
    private static string? FormatArgumentsHint(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var (key, value) in arguments)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(key).Append('=').Append(value);
            if (sb.Length >= 80) break;
        }
        var text = sb.ToString();
        return text.Length <= 80 ? text : text[..80] + "...";
    }

    private static long EstimateTokens(IList<ChatMessage> messages)
        => UsageEstimator.EstimateMessagesTokens(messages);

    private async Task<(bool success, string summary, int chunkCount)> SummarizeInChunksAsync(
        IList<ChatMessage> oldMessages,
        int budgetTokens,
        AgentOptions options,
        bool substituteNullSummary,
        UsageRecorder recorder,
        CancellationToken ct)
    {
        var chunks = new List<List<ChatMessage>>();
        var currentChunk = new List<ChatMessage>();

        foreach (var msg in oldMessages)
        {
            // Check whether this single message alone exceeds the budget.
            var msgTokens = EstimateTokens(new[] { msg });

            if (currentChunk.Count == 0)
            {
                // Always start a new chunk with this message. If the message itself
                // is over budget, it'll land in its own single-element chunk —
                // unavoidable since we have to send it to the compaction model.
                currentChunk.Add(msg);
            }
            else
            {
                // Greedy: would adding this message exceed the budget?
                var candidateTokens = EstimateTokens(currentChunk.Append(msg).ToList());
                if (candidateTokens <= budgetTokens)
                {
                    currentChunk.Add(msg);
                }
                else
                {
                    // Close current chunk (it's within budget) and start a new one
                    // with this message.
                    chunks.Add(currentChunk);
                    currentChunk = new List<ChatMessage> { msg };
                }
            }
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(currentChunk);
        }

        // One recording wrapper for the whole chunked operation: every chunk's call is recorded
        // exactly once (a throwing chunk call records a zero-token call) under
        // UsageSource.Compaction with the compaction client's model.
        var recordingClient = CreateRecordingClient(recorder);

        var summaries = new List<string>();
        foreach (var chunk in chunks)
        {
            string? raw;
            try
            {
                var promptText = BuildSummaryPrompt(chunk);
                var promptMessages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.User, promptText)
                };

                var response = await recordingClient.GetResponseAsync(promptMessages, cancellationToken: ct);
                raw = response.Text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation stops the chunk loop immediately: no fallback text and
                // no further chunk calls. A client-originated OperationCanceledException while
                // ct is NOT cancelled (e.g. an HTTP timeout) still falls through to the
                // failure handling below.
                throw;
            }
            catch (System.Exception ex)
            {
                if (!substituteNullSummary)
                    return (false, string.Empty, 0);

                _logger.LogWarning(ex, "Chunk summarization failed, using fallback");
                raw = "No summary available for this section.";
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (!substituteNullSummary)
                    return (false, string.Empty, 0);
                raw = "No summary available for this section.";
            }

            summaries.Add(raw!);
        }

        var combinedSummary = string.Join("\n\n", summaries);
        return (true, combinedSummary, chunks.Count);
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
    /// <param name="messages">Message sequence whose split boundary is to be adjusted.</param>
    /// <param name="splitPoint">Requested boundary index; negative values become zero.</param>
    /// <returns>The adjusted boundary, advanced past tool-result messages at its start and capped at the message count.</returns>
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
