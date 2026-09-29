using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder;

/// <summary>
/// Token counts summed over one or more model calls.
/// </summary>
/// <remarks>
/// <para>
/// Every count here is an exact sum of values the providers reported; nothing is estimated.
/// </para>
/// <para>
/// "Not reported" is deliberately distinguishable from zero: <see cref="CachedInputTokens"/> and
/// <see cref="ReasoningTokens"/> sum only the calls that actually reported a value, and
/// <see cref="CachedInputReportedCalls"/> / <see cref="ReasoningReportedCalls"/> say how many calls
/// did. A provider that never reports cached tokens (Ollama, for example) therefore leaves both the
/// sum and the counter at zero, while a provider that reports a cached count of zero increments the
/// counter. Consumers must check the counters before treating a zero sum as a measurement.
/// </para>
/// <para>
/// A call that returned no usage information at all still counts in <see cref="Calls"/> (with zero
/// tokens) and does not increment the reported-calls counters.
/// </para>
/// </remarks>
public sealed class TokenUsage
{
    /// <summary>Input tokens summed over the calls being counted.</summary>
    public long InputTokens { get; set; }

    /// <summary>Output tokens summed over the calls being counted.</summary>
    public long OutputTokens { get; set; }

    /// <summary>
    /// Cached (prompt-cache) input tokens summed over the calls that reported a cached count.
    /// Check <see cref="CachedInputReportedCalls"/> to tell "not reported" apart from "zero".
    /// </summary>
    public long CachedInputTokens { get; set; }

    /// <summary>
    /// Reasoning tokens summed over the calls that reported a reasoning count.
    /// Check <see cref="ReasoningReportedCalls"/> to tell "not reported" apart from "zero".
    /// </summary>
    public long ReasoningTokens { get; set; }

    /// <summary>
    /// Number of model calls being counted, including calls that reported no usage information.
    /// </summary>
    public int Calls { get; set; }

    /// <summary>
    /// Number of calls that reported a cached input token count — that is, the number of calls
    /// whose cached input count contributed to <see cref="CachedInputTokens"/>.
    /// </summary>
    public int CachedInputReportedCalls { get; set; }

    /// <summary>
    /// Number of calls that reported a reasoning token count — that is, the number of calls
    /// whose reasoning count contributed to <see cref="ReasoningTokens"/>.
    /// </summary>
    public int ReasoningReportedCalls { get; set; }

    /// <summary>
    /// Adds the counts of <paramref name="other"/> into this instance and returns this instance,
    /// so that calls can be chained. <c>null</c> is ignored.
    /// </summary>
    /// <param name="other">The usage to add; may be <c>null</c>.</param>
    /// <returns>This instance, with <paramref name="other"/>'s counts added.</returns>
    public TokenUsage Add(TokenUsage? other)
    {
        if (other is null) return this;

        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CachedInputTokens += other.CachedInputTokens;
        ReasoningTokens += other.ReasoningTokens;
        Calls += other.Calls;
        CachedInputReportedCalls += other.CachedInputReportedCalls;
        ReasoningReportedCalls += other.ReasoningReportedCalls;
        return this;
    }

    /// <summary>Creates a detached copy of this instance; later changes to either do not affect the other.</summary>
    /// <returns>A new <see cref="TokenUsage"/> with the same counts.</returns>
    public TokenUsage Clone() => new TokenUsage
    {
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        CachedInputTokens = CachedInputTokens,
        ReasoningTokens = ReasoningTokens,
        Calls = Calls,
        CachedInputReportedCalls = CachedInputReportedCalls,
        ReasoningReportedCalls = ReasoningReportedCalls
    };

    /// <summary>
    /// Counts one model call described by a single <see cref="UsageDetails"/> (or by no details at
    /// all, for a call that reported nothing): <see cref="Calls"/> is one, the token counts come
    /// from the details, and the reported-calls counters are incremented only when the
    /// corresponding optional count was present.
    /// </summary>
    internal static TokenUsage FromSingleCall(UsageDetails? details) => new TokenUsage
    {
        InputTokens = details?.InputTokenCount ?? 0,
        OutputTokens = details?.OutputTokenCount ?? 0,
        CachedInputTokens = details?.CachedInputTokenCount ?? 0,
        ReasoningTokens = details?.ReasoningTokenCount ?? 0,
        Calls = 1,
        CachedInputReportedCalls = details?.CachedInputTokenCount.HasValue == true ? 1 : 0,
        ReasoningReportedCalls = details?.ReasoningTokenCount.HasValue == true ? 1 : 0
    };

    /// <summary>Renders the counts and their completeness, e.g. <c>2 call(s): input=25000, output=30</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Calls).Append(" call(s): input=").Append(InputTokens);
        sb.Append(", output=").Append(OutputTokens);
        sb.Append(", cachedInput=").Append(CachedInputTokens);
        if (CachedInputReportedCalls == 0) sb.Append(" (not reported)");
        sb.Append(", reasoning=").Append(ReasoningTokens);
        if (ReasoningReportedCalls == 0) sb.Append(" (not reported)");
        return sb.ToString();
    }
}

/// <summary>
/// Classifies the model call a recorded usage event came from, so that the cost of the agent loop
/// can be told apart from the cost of context compaction and (in a later release) sub-agents.
/// </summary>
public enum UsageSource
{
    /// <summary>A call made by the agent loop itself.</summary>
    Agent,

    /// <summary>A context-compaction summarisation call.</summary>
    Compaction,

    /// <summary>
    /// A call made by a sub-agent session. Declared for a later release; not produced by this
    /// version of SharpCoder.
    /// </summary>
    SubAgent,

    /// <summary>
    /// A compaction call made inside a sub-agent session. Declared for a later release; not
    /// produced by this version of SharpCoder.
    /// </summary>
    SubAgentCompaction
}

/// <summary>
/// One aggregated usage bucket of a <see cref="UsageSummary"/>: all recorded calls that share the
/// same <see cref="Source"/> and <see cref="Model"/>.
/// </summary>
public sealed class UsageEntry
{
    /// <summary>The source the calls in this entry came from.</summary>
    public UsageSource Source { get; set; }

    /// <summary>
    /// The model the calls in this entry were sent to, or <c>null</c> when neither the client's
    /// <see cref="ChatClientMetadata"/> nor the responses identified one.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>The summed usage of the calls in this entry.</summary>
    public TokenUsage Usage { get; set; } = new TokenUsage();

    /// <summary>Creates an empty entry; used by serializers.</summary>
    public UsageEntry()
    {
    }

    /// <summary>Creates an entry for the given source, model and summed usage.</summary>
    /// <param name="source">The source the calls came from.</param>
    /// <param name="model">The model the calls were sent to; <c>null</c> when unknown.</param>
    /// <param name="usage">The summed usage; <c>null</c> is treated as empty.</param>
    public UsageEntry(UsageSource source, string? model, TokenUsage? usage)
    {
        Source = source;
        Model = model;
        Usage = usage ?? new TokenUsage();
    }

    /// <summary>Creates a detached copy of this entry.</summary>
    internal UsageEntry Clone()
    {
        var usage = Usage;
        return new UsageEntry(Source, Model, usage is null ? new TokenUsage() : usage.Clone());
    }
}

/// <summary>
/// A thread-safe accumulation of recorded model-call usage, grouped into one
/// <see cref="UsageEntry"/> per (source, model) pair, plus a <see cref="Total"/> over all of them.
/// </summary>
/// <remarks>
/// <para>
/// Entries are keyed by the pair (source, model): the same model used under two sources produces
/// two entries, and two models under one source produce two entries as well.
/// </para>
/// <para>
/// <see cref="Add"/> is thread-safe and loses no updates. Snapshots taken through
/// <see cref="Snapshot"/> or the <see cref="Entries"/> getter are detached deep copies, so later
/// additions never change a snapshot that was already handed out.
/// </para>
/// <para>
/// The type is JSON-serialisable (entries and total are public properties with a public setter or
/// a computed value), which is what <see cref="AgentSession"/> uses to persist cumulative usage.
/// </para>
/// </remarks>
public sealed class UsageSummary
{
    private readonly object _sync = new object();
    private readonly List<UsageEntry> _entries = new List<UsageEntry>();

    /// <summary>Creates an empty summary.</summary>
    public UsageSummary()
    {
    }

    /// <summary>Creates a summary from the given entries, deep-copying each one.</summary>
    /// <param name="entries">The entries to copy; may be <c>null</c>.</param>
    public UsageSummary(IEnumerable<UsageEntry>? entries)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            _entries.Add(entry.Clone());
        }
    }

    /// <summary>
    /// The per-(source, model) entries, as a detached deep copy — mutating the returned list or its
    /// entries does not affect this summary. Setting the property replaces all entries with deep
    /// copies of the supplied entries (serializers use this path).
    /// </summary>
    public IReadOnlyList<UsageEntry> Entries
    {
        get
        {
            lock (_sync) return CopyEntriesUnlocked();
        }
        set
        {
            lock (_sync)
            {
                _entries.Clear();
                if (value is null) return;
                foreach (var entry in value)
                {
                    if (entry is null) continue;
                    _entries.Add(entry.Clone());
                }
            }
        }
    }

    /// <summary>
    /// The sum over all entries, freshly computed and detached. On an empty summary this is an
    /// all-zero <see cref="TokenUsage"/> with <see cref="TokenUsage.Calls"/> = 0.
    /// </summary>
    public TokenUsage Total
    {
        get
        {
            lock (_sync)
            {
                var total = new TokenUsage();
                foreach (var entry in _entries)
                {
                    total.Add(entry.Usage);
                }
                return total;
            }
        }
    }

    /// <summary>
    /// Records one model call: its usage is added to the entry matching its source and model,
    /// creating that entry when it does not exist yet, or to <see cref="Total"/> alone when there
    /// are no entries. Thread-safe; concurrent additions never lose an update.
    /// </summary>
    /// <param name="usageEvent">The recorded call; <c>null</c> is ignored.</param>
    public void Add(UsageEvent usageEvent)
    {
        if (usageEvent is null) return;

        lock (_sync)
        {
            UsageEntry? entry = null;
            foreach (var candidate in _entries)
            {
                if (candidate.Source == usageEvent.Source && string.Equals(candidate.Model, usageEvent.Model, StringComparison.Ordinal))
                {
                    entry = candidate;
                    break;
                }
            }

            if (entry is null)
            {
                entry = new UsageEntry(usageEvent.Source, usageEvent.Model, new TokenUsage());
                _entries.Add(entry);
            }

            entry.Usage ??= new TokenUsage();
            entry.Usage.Add(usageEvent.Usage);
        }
    }

    /// <summary>
    /// Creates a detached deep copy of this summary — entries and totals included. The copy never
    /// changes afterwards, no matter how many events are added to this summary.
    /// </summary>
    /// <returns>A new, independent <see cref="UsageSummary"/>.</returns>
    public UsageSummary Snapshot()
    {
        lock (_sync) return new UsageSummary(CopyEntriesUnlocked());
    }

    /// <summary>Renders the total and the per-(source, model) breakdown for diagnostics.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Total.ToString());
        foreach (var entry in Entries)
        {
            sb.Append(" | ").Append(entry.Source).Append('/').Append(entry.Model ?? "(unknown)").Append(": ").Append(entry.Usage.ToString());
        }
        return sb.ToString();
    }

    private List<UsageEntry> CopyEntriesUnlocked()
    {
        var copy = new List<UsageEntry>(_entries.Count);
        foreach (var entry in _entries)
        {
            copy.Add(entry.Clone());
        }
        return copy;
    }
}

/// <summary>
/// One recorded model call: a single request sent to a model client — one
/// <c>GetResponseAsync</c> call or one <c>GetStreamingResponseAsync</c> enumeration.
/// </summary>
/// <remarks>
/// <para>
/// A call is recorded exactly once, when it ends, whichever way it ends: a completed response is
/// recorded with the usage it reported, and a call that threw, or whose stream was disposed early,
/// is recorded with the usage received so far (zero when none arrived). A call that reported no
/// usage still counts in <see cref="TokenUsage.Calls"/> and does not increment the reported-calls
/// counters.
/// </para>
/// <para>
/// Transport-level retries performed inside a provider's HTTP handler are invisible to SharpCoder
/// and are therefore not counted as separate calls.
/// </para>
/// <para>
/// <see cref="Usage"/> describes exactly this one call, so <see cref="TokenUsage.Calls"/> of a
/// freshly recorded event is always 1.
/// </para>
/// </remarks>
public sealed class UsageEvent
{
    /// <summary>Creates an event for a single recorded call.</summary>
    /// <param name="source">The source of the call.</param>
    /// <param name="model">The model the call was sent to; null/whitespace becomes <c>null</c>.</param>
    /// <param name="usage">
    /// The call's usage; <c>null</c> is treated as a zero-token call. Because an event describes
    /// exactly one call, the stored usage always has <see cref="TokenUsage.Calls"/> set to 1 — this
    /// is a copy of <paramref name="usage"/>, so the caller's instance is never mutated.
    /// </param>
    /// <param name="subAgentId">
    /// The sub-agent the call belongs to, when the call was made by a sub-agent. Always <c>null</c>
    /// in this version of SharpCoder; sub-agent forwarding is a later release.
    /// </param>
    public UsageEvent(UsageSource source, string? model, TokenUsage? usage, string? subAgentId = null)
    {
        Source = source;
        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        SubAgentId = subAgentId;
        Usage = new TokenUsage
        {
            InputTokens = usage?.InputTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            CachedInputTokens = usage?.CachedInputTokens ?? 0,
            ReasoningTokens = usage?.ReasoningTokens ?? 0,
            Calls = 1,
            CachedInputReportedCalls = usage?.CachedInputReportedCalls ?? 0,
            ReasoningReportedCalls = usage?.ReasoningReportedCalls ?? 0
        };
    }

    /// <summary>The source of the recorded call.</summary>
    public UsageSource Source { get; }

    /// <summary>
    /// The model the call was sent to: the calling client's <see cref="ChatClientMetadata"/>
    /// default model when known, otherwise the model the response reported, otherwise <c>null</c>.
    /// </summary>
    public string? Model { get; }

    /// <summary>
    /// The sub-agent session the call belongs to, or <c>null</c> for parent-agent, compaction and
    /// host-initiated calls. Always <c>null</c> in this version of SharpCoder.
    /// </summary>
    public string? SubAgentId { get; }

    /// <summary>The usage of this single call; <see cref="TokenUsage.Calls"/> is 1.</summary>
    public TokenUsage Usage { get; }

    /// <summary>Renders the event for diagnostics, e.g. <c>Agent/model-x: 1 call(s): input=1000, output=10</c>.</summary>
    public override string ToString()
        => $"{Source}/{Model ?? "(unknown)"}: {Usage.ToString()}";
}

/// <summary>
/// The one shared recording mechanism behind every model call SharpCoder makes: callers pass each
/// recorded <see cref="UsageEvent"/> here, and it fans the event out to the execution-local
/// summary, the session (cumulative usage plus the legacy cumulative token counters) and the host's
/// <see cref="AgentOptions.OnUsage"/> callback.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one recorder exists per execution, so two overlapping executions on one agent never mix
/// their usage. Session-level state (cumulative counters and <see cref="AgentSession.Usage"/>) is
/// updated as each call ends, so a host polling a session mid-run sees current totals; two
/// executions sharing ONE <see cref="AgentSession"/> concurrently remain unsupported — exactly as
/// they were before this mechanism existed — and their calls would interleave in that shared
/// session's totals.
/// </para>
/// <para>
/// The host callback is invoked outside every internal lock, and an exception it throws is caught
/// and logged — it can never fail or alter the run.
/// </para>
/// </remarks>
internal sealed class UsageRecorder
{
    private readonly UsageSummary _summary = new UsageSummary();
    private readonly AgentSession? _session;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a recorder that accumulates into its own execution-local summary.</summary>
    /// <param name="options">Agent options; the <see cref="AgentOptions.OnUsage"/> callback is read from it per call.</param>
    /// <param name="session">The session to update, or <c>null</c> for a stateless execution.</param>
    /// <param name="logger">Logger used for callback failures and metadata lookup failures.</param>
    internal UsageRecorder(AgentOptions options, AgentSession? session, ILogger? logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The execution-local summary of every call this execution recorded.</summary>
    internal UsageSummary Summary => _summary;

    /// <summary>
    /// Records one ended model call exactly once: adds it to the execution summary, adds it to the
    /// session's cumulative usage (when there is a session) and raises
    /// <see cref="AgentOptions.OnUsage"/>.
    /// <para>
    /// This method never throws: a host handler that throws is caught, and both the handler
    /// invocation and the best-effort warning written for it are contained, so a failing handler —
    /// or a failing logger — can never fail or alter the model call being recorded.
    /// </para>
    /// </summary>
    /// <param name="usageEvent">The recorded call; <c>null</c> is ignored.</param>
    internal void Record(UsageEvent usageEvent)
    {
        if (usageEvent is null) return;

        _summary.Add(usageEvent);

        var session = _session;
        if (session is not null)
        {
            session.Usage.Add(usageEvent);
            session.InputTokensUsed += usageEvent.Usage.InputTokens;
            session.OutputTokensUsed += usageEvent.Usage.OutputTokens;
        }

        // Never inside a lock: a host handler may call back into SharpCoder.
        var handler = _options.OnUsage;
        if (handler is null) return;
        try
        {
            handler(usageEvent);
        }
        catch (Exception ex)
        {
            // Reporting the host-handler failure is best effort: the logger write gets its OWN
            // guard, because a logger that throws must not turn this diagnostic into a failure of
            // an otherwise successful model call (or of a compaction) — nothing at the recording
            // boundary may ever throw.
            try
            {
                _logger.LogWarning(ex, "CodingAgent.OnUsage handler threw an exception.");
            }
            catch
            {
                // Swallowed deliberately; see above.
            }
        }
    }

    /// <summary>
    /// Resolves the model a client sends its calls to, from the client's
    /// <see cref="ChatClientMetadata"/>; <c>null</c> when the client exposes none. A client whose
    /// metadata lookup throws is reported as unknown rather than failing the run, and the
    /// best-effort debug write for it is contained so a failing logger cannot throw either.
    /// </summary>
    internal string? ResolveClientModel(IChatClient client)
    {
        try
        {
            return (client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;
        }
        catch (Exception ex)
        {
            try
            {
                _logger.LogDebug(ex, "ChatClientMetadata lookup failed while recording token usage.");
            }
            catch
            {
                // Swallowed deliberately: reporting must never fail the run being recorded.
            }

            return null;
        }
    }
}

/// <summary>
/// The shared per-call recorder: a chat-client wrapper that reports every call that passes through
/// it to a <see cref="UsageRecorder"/>, exactly once, when the call ends — however it ends.
/// </summary>
/// <remarks>
/// <para>
/// One instance is created per execution (or per call site that needs its own classification), so
/// overlapping executions never mix their usage. The wrapper also captures
/// <see cref="LastRoundInputTokens"/>, the input token count of the most recent call, which feeds
/// <see cref="AgentSession.LastKnownContextTokens"/> (the latest round's context size — never a sum).
/// </para>
/// <para>
/// For a streaming call, usage is summed over every <see cref="UsageContent"/> received before the
/// enumeration ended; because providers may split usage across several updates, each optional count
/// counts as "reported" when at least one update carried a value, which keeps the reported-calls
/// counters at one per call.
/// </para>
/// </remarks>
internal sealed class UsageRecordingChatClient : DelegatingChatClient
{
    private readonly UsageRecorder _recorder;
    private readonly UsageSource _source;
    private readonly string? _subAgentId;
    private readonly string? _metadataModelId;

    /// <summary>Creates a recording wrapper around <paramref name="inner"/>.</summary>
    /// <param name="inner">The client whose calls are recorded.</param>
    /// <param name="recorder">The recorder that receives one event per ended call.</param>
    /// <param name="source">The source classification for the recorded calls.</param>
    /// <param name="subAgentId">The sub-agent id for the recorded calls; always <c>null</c> today.</param>
    internal UsageRecordingChatClient(IChatClient inner, UsageRecorder recorder, UsageSource source = UsageSource.Agent, string? subAgentId = null)
        : base(inner)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _source = source;
        _subAgentId = subAgentId;
        _metadataModelId = recorder.ResolveClientModel(inner);
    }

    /// <summary>
    /// Input token count of the most recent call, or <c>null</c> while no call has reported one.
    /// </summary>
    internal long? LastRoundInputTokens { get; private set; }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions, CancellationToken ct = default)
    {
        ChatResponse? response = null;
        try
        {
            response = await InnerClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
            return response;
        }
        finally
        {
            var usage = response?.Usage;
            if (usage?.InputTokenCount is long inputTokens)
                LastRoundInputTokens = inputTokens;

            _recorder.Record(new UsageEvent(
                _source, ResolveModel(response?.ModelId), TokenUsage.FromSingleCall(usage), _subAgentId));
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var usage = new CallUsageAccumulator();
        string? responseModelId = null;

        try
        {
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, chatOptions, ct).WithCancellation(ct))
            {
                if (!string.IsNullOrWhiteSpace(update.ModelId)) responseModelId = update.ModelId;
                usage.Add(update);
                yield return update;
            }
        }
        finally
        {
            // Reached on completion, on failure, and when the consumer disposes the enumeration
            // early: either way the call is recorded exactly once, with whatever usage arrived.
            if (usage.InputReported)
                LastRoundInputTokens = usage.InputTokens;

            _recorder.Record(new UsageEvent(
                _source, ResolveModel(responseModelId), usage.ToTokenUsage(), _subAgentId));
        }
    }

    /// <summary>
    /// The client's metadata model wins; the model the response reported is the fallback; otherwise
    /// the call is recorded with no model.
    /// </summary>
    private string? ResolveModel(string? responseModelId)
    {
        if (!string.IsNullOrWhiteSpace(_metadataModelId)) return _metadataModelId;
        return string.IsNullOrWhiteSpace(responseModelId) ? null : responseModelId;
    }

    /// <summary>Sums the usage of ONE streaming call across all of its updates.</summary>
    private sealed class CallUsageAccumulator
    {
        private long _inputTokens;
        private long _outputTokens;
        private long _cachedInputTokens;
        private long _reasoningTokens;
        private bool _inputReported;
        private bool _cachedInputReported;
        private bool _reasoningReported;

        /// <summary>True when at least one update reported an input token count.</summary>
        internal bool InputReported => _inputReported;

        /// <summary>The summed input token count of this call so far.</summary>
        internal long InputTokens => _inputTokens;

        /// <summary>Adds every usage item carried by <paramref name="update"/>.</summary>
        internal void Add(ChatResponseUpdate update)
        {
            foreach (var content in update.Contents)
            {
                var details = (content as UsageContent)?.Details;
                if (details is null) continue;

                if (details.InputTokenCount is long inputTokens)
                {
                    _inputTokens += inputTokens;
                    _inputReported = true;
                }

                if (details.OutputTokenCount is long outputTokens)
                    _outputTokens += outputTokens;

                if (details.CachedInputTokenCount is long cachedInputTokens)
                {
                    _cachedInputTokens += cachedInputTokens;
                    _cachedInputReported = true;
                }

                if (details.ReasoningTokenCount is long reasoningTokens)
                {
                    _reasoningTokens += reasoningTokens;
                    _reasoningReported = true;
                }
            }
        }

        /// <summary>Builds the single-call usage described by everything added so far.</summary>
        internal TokenUsage ToTokenUsage() => new TokenUsage
        {
            InputTokens = _inputTokens,
            OutputTokens = _outputTokens,
            CachedInputTokens = _cachedInputTokens,
            ReasoningTokens = _reasoningTokens,
            Calls = 1,
            CachedInputReportedCalls = _cachedInputReported ? 1 : 0,
            ReasoningReportedCalls = _reasoningReported ? 1 : 0
        };
    }
}
