namespace SharpCoder;

/// <summary>
/// An ESTIMATED split of reported token counts into categories — how much of the input went to the
/// system prompt, tool definitions, conversation text, replayed tool calls and results, reasoning
/// and images, and how much of the output was text, tool calls or reasoning.
/// </summary>
/// <remarks>
/// <para>
/// Every value in this type is an estimate. Providers report only totals (input, output and
/// optionally cached-input and reasoning tokens); SharpCoder does not run a provider tokenizer.
/// Instead, for each model call, the exact reported total is distributed over the categories in
/// proportion to a character weight measured from the request and the response (images count as a
/// fixed per-image weight). The per-category values are therefore approximations of where the
/// tokens went, not measurements.
/// </para>
/// <para>
/// Two properties still hold exactly for every call that contributed an estimate: the input
/// categories of that call sum to its reported <see cref="TokenUsage.InputTokens"/>, and the output
/// categories sum to its reported <see cref="TokenUsage.OutputTokens"/>. Calls that did not report
/// an input (or output) count, or whose request carried no measurable input, contribute no input
/// (or output) estimate; <see cref="InputEstimatedCalls"/> and <see cref="OutputEstimatedCalls"/>
/// say how many calls did contribute, so consumers can tell "no estimate" apart from "zero".
/// Summed over several calls, the input categories therefore add up to the input tokens of the
/// estimated calls only, which can be less than <see cref="TokenUsage.InputTokens"/>.
/// </para>
/// <para>
/// The type is JSON-serialisable (every member is a public property with a public setter), so it is
/// persisted together with <see cref="AgentSession.Usage"/>.
/// </para>
/// </remarks>
public sealed class EstimatedTokenBreakdown
{
    /// <summary>
    /// Estimated input tokens spent on the system prompt: the request's
    /// <c>ChatOptions.Instructions</c> and the text of system-role messages. This is an estimate.
    /// </summary>
    public long SystemPrompt { get; set; }

    /// <summary>
    /// Estimated input tokens spent on the tool definitions sent with the request (each tool's
    /// name, description and, for functions, parameter JSON schema). This is an estimate.
    /// </summary>
    public long ToolDefinitions { get; set; }

    /// <summary>Estimated input tokens spent on the text of user-role messages. This is an estimate.</summary>
    public long UserText { get; set; }

    /// <summary>
    /// Estimated input tokens spent on the text of replayed assistant-role messages (earlier model
    /// output sent back as conversation history). This is an estimate.
    /// </summary>
    public long AssistantText { get; set; }

    /// <summary>
    /// Estimated input tokens spent on replayed tool calls — the function names and arguments of
    /// earlier model tool calls sent back as conversation history. This is an estimate.
    /// </summary>
    public long ToolCalls { get; set; }

    /// <summary>
    /// Estimated input tokens spent on tool results sent to the model (function results and the
    /// text of tool-role messages). This is an estimate.
    /// </summary>
    public long ToolResults { get; set; }

    /// <summary>
    /// Estimated input tokens spent on replayed reasoning content sent back as conversation
    /// history. This is an estimate.
    /// </summary>
    public long Reasoning { get; set; }

    /// <summary>
    /// Estimated input tokens spent on images and PDF attachments, each weighted as a flat
    /// per-image amount regardless of its size. This is an estimate.
    /// </summary>
    public long Images { get; set; }

    /// <summary>Estimated output tokens the model spent on text. This is an estimate.</summary>
    public long OutputText { get; set; }

    /// <summary>
    /// Estimated output tokens the model spent on tool calls (function names and arguments). This
    /// is an estimate.
    /// </summary>
    public long OutputToolCalls { get; set; }

    /// <summary>
    /// Estimated output tokens the model spent on reasoning. When the provider reported a reasoning
    /// token count, a call's value here is that count (capped at the call's output tokens) rather
    /// than a character-share estimate; otherwise it is estimated from the reasoning content
    /// received. Summed over calls, it remains part of an estimate.
    /// </summary>
    public long OutputReasoning { get; set; }

    /// <summary>
    /// Number of calls that contributed an input estimate — that is, calls that reported an input
    /// token count and sent a request with measurable content. Calls not counted here contributed
    /// nothing to the input categories.
    /// </summary>
    public int InputEstimatedCalls { get; set; }

    /// <summary>
    /// Number of calls that contributed an output estimate — that is, calls that reported an output
    /// token count. Calls not counted here contributed nothing to the output categories.
    /// </summary>
    public int OutputEstimatedCalls { get; set; }

    /// <summary>
    /// Adds the estimated values and counters of <paramref name="other"/> into this instance and
    /// returns this instance, so that calls can be chained. <c>null</c> is ignored. The two
    /// instances stay detached: no reference to <paramref name="other"/> is kept.
    /// </summary>
    /// <param name="other">The estimate to add; may be <c>null</c>.</param>
    /// <returns>This instance, with <paramref name="other"/>'s estimated values added.</returns>
    public EstimatedTokenBreakdown Add(EstimatedTokenBreakdown? other)
    {
        if (other is null) return this;

        SystemPrompt += other.SystemPrompt;
        ToolDefinitions += other.ToolDefinitions;
        UserText += other.UserText;
        AssistantText += other.AssistantText;
        ToolCalls += other.ToolCalls;
        ToolResults += other.ToolResults;
        Reasoning += other.Reasoning;
        Images += other.Images;
        OutputText += other.OutputText;
        OutputToolCalls += other.OutputToolCalls;
        OutputReasoning += other.OutputReasoning;
        InputEstimatedCalls += other.InputEstimatedCalls;
        OutputEstimatedCalls += other.OutputEstimatedCalls;
        return this;
    }

    /// <summary>Creates a detached copy of this estimate; later changes to either do not affect the other.</summary>
    /// <returns>A new <see cref="EstimatedTokenBreakdown"/> with the same estimated values and counters.</returns>
    public EstimatedTokenBreakdown Clone() => new EstimatedTokenBreakdown
    {
        SystemPrompt = SystemPrompt,
        ToolDefinitions = ToolDefinitions,
        UserText = UserText,
        AssistantText = AssistantText,
        ToolCalls = ToolCalls,
        ToolResults = ToolResults,
        Reasoning = Reasoning,
        Images = Images,
        OutputText = OutputText,
        OutputToolCalls = OutputToolCalls,
        OutputReasoning = OutputReasoning,
        InputEstimatedCalls = InputEstimatedCalls,
        OutputEstimatedCalls = OutputEstimatedCalls
    };
}
