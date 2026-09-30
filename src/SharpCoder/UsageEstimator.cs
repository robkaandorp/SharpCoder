using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SharpCoder;

/// <summary>
/// Computes the per-call <see cref="EstimatedTokenBreakdown"/>: measures character weights of the
/// request (input) and response (output) of one model call and distributes the call's exact
/// reported token totals over the categories in proportion to those weights.
/// </summary>
/// <remarks>
/// <para>
/// This is also the single home of the character-length heuristics shared with
/// <see cref="AgentSession.EstimatedContextTokens"/>: <see cref="EstimateArgumentsLength"/>,
/// <see cref="EstimateResultLength"/> and the image/PDF classification with its flat
/// <see cref="ImageCharacterWeight"/>.
/// </para>
/// <para>
/// Distribution uses largest-remainder rounding: each category first receives
/// <c>floor(total × weight / totalWeight)</c> (computed exactly, without floating point); the
/// tokens still missing to reach <c>total</c> are then handed out one each to the categories with
/// the largest remainders, ties broken by the fixed category order (for input:
/// SystemPrompt, ToolDefinitions, UserText, AssistantText, ToolCalls, ToolResults, Reasoning,
/// Images; for output: OutputText, OutputToolCalls, OutputReasoning). The categories therefore sum
/// exactly to the distributed total.
/// </para>
/// </remarks>
internal static class UsageEstimator
{
    /// <summary>Index of the system-prompt input category.</summary>
    internal const int SystemPromptIndex = 0;
    /// <summary>Index of the tool-definitions input category.</summary>
    internal const int ToolDefinitionsIndex = 1;
    /// <summary>Index of the user-text input category.</summary>
    internal const int UserTextIndex = 2;
    /// <summary>Index of the assistant-text input category.</summary>
    internal const int AssistantTextIndex = 3;
    /// <summary>Index of the replayed-tool-calls input category.</summary>
    internal const int ToolCallsIndex = 4;
    /// <summary>Index of the tool-results input category.</summary>
    internal const int ToolResultsIndex = 5;
    /// <summary>Index of the replayed-reasoning input category.</summary>
    internal const int ReasoningIndex = 6;
    /// <summary>Index of the images input category.</summary>
    internal const int ImagesIndex = 7;
    /// <summary>Number of input categories.</summary>
    internal const int InputCategoryCount = 8;

    /// <summary>
    /// Character weight of one image or PDF attachment: the flat
    /// <see cref="AgentSession.ImageTokenEstimate"/> expressed in characters (~4 chars per token).
    /// </summary>
    internal const long ImageCharacterWeight = AgentSession.ImageTokenEstimate * 4;

    /// <summary>
    /// Character length of a function call's arguments: the sum of each argument's key length and
    /// its value's <c>ToString()</c> length.
    /// </summary>
    internal static long EstimateArgumentsLength(FunctionCallContent fc)
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

    /// <summary>Character length of a function result: its result's <c>ToString()</c> length.</summary>
    internal static long EstimateResultLength(FunctionResultContent fr)
    {
        if (fr.Result == null) return 0;
        return fr.Result.ToString()?.Length ?? 0;
    }

    /// <summary>Character length of a function call: its name plus <see cref="EstimateArgumentsLength"/>.</summary>
    internal static long EstimateCallLength(FunctionCallContent fc)
        => (fc.Name?.Length ?? 0) + EstimateArgumentsLength(fc);

    /// <summary>True when the data content is an image (<c>image/*</c>) or a PDF.</summary>
    internal static bool IsImageOrPdf(DataContent dc)
    {
        var mt = dc.MediaType ?? string.Empty;
        return mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
               mt.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Measures the input character weights of a request, indexed by the input category constants.
    /// </summary>
    /// <param name="messages">The messages sent to the model; <c>null</c> contributes nothing.</param>
    /// <param name="options">The chat options sent to the model; <c>null</c> contributes nothing.</param>
    internal static long[] MeasureInput(IEnumerable<ChatMessage>? messages, ChatOptions? options)
    {
        var weights = new long[InputCategoryCount];

        if (options is not null)
        {
            weights[SystemPromptIndex] = checked(weights[SystemPromptIndex] + (options.Instructions?.Length ?? 0));

            if (options.Tools is { } tools)
            {
                foreach (var tool in tools)
                {
                    if (tool is null) continue;
                    long toolWeight = (tool.Name?.Length ?? 0) + (tool.Description?.Length ?? 0);
                    if (tool is AIFunctionDeclaration function && function.JsonSchema.ValueKind != JsonValueKind.Undefined)
                        toolWeight += function.JsonSchema.GetRawText().Length;
                    weights[ToolDefinitionsIndex] = checked(weights[ToolDefinitionsIndex] + toolWeight);
                }
            }
        }

        if (messages is not null)
        {
            foreach (var message in messages)
            {
                if (message is null) continue;
                var role = message.Role;
                foreach (var content in message.Contents)
                {
                    int index;
                    long weight;
                    switch (content)
                    {
                        case TextContent tc:
                            if (role == ChatRole.System) index = SystemPromptIndex;
                            else if (role == ChatRole.User) index = UserTextIndex;
                            else if (role == ChatRole.Assistant) index = AssistantTextIndex;
                            else if (role == ChatRole.Tool) index = ToolResultsIndex;
                            else continue;
                            weight = tc.Text?.Length ?? 0;
                            break;
                        case FunctionCallContent fc:
                            index = ToolCallsIndex;
                            weight = EstimateCallLength(fc);
                            break;
                        case FunctionResultContent fr:
                            index = ToolResultsIndex;
                            weight = EstimateResultLength(fr);
                            break;
                        case TextReasoningContent rc:
                            index = ReasoningIndex;
                            weight = rc.Text?.Length ?? 0;
                            break;
                        case DataContent dc when IsImageOrPdf(dc):
                            index = ImagesIndex;
                            weight = ImageCharacterWeight;
                            break;
                        default:
                            continue;
                    }

                    weights[index] = checked(weights[index] + weight);
                }
            }
        }

        return weights;
    }

    /// <summary>
    /// Builds the estimate of ONE call. Input is estimated only when <paramref name="inputTokens"/>
    /// was reported (non-negative) and the input weights sum to more than zero; output is estimated
    /// whenever <paramref name="outputTokens"/> was reported (non-negative).
    /// </summary>
    /// <param name="inputWeights">Input weights from <see cref="MeasureInput"/>; <c>null</c> means no input estimate.</param>
    /// <param name="inputTokens">The call's reported input token count, or <c>null</c> when not reported.</param>
    /// <param name="outputTokens">The call's reported output token count, or <c>null</c> when not reported.</param>
    /// <param name="reasoningTokens">The call's reported reasoning token count, or <c>null</c> when not reported.</param>
    /// <param name="outputWeights">Output weights measured from the response.</param>
    internal static EstimatedTokenBreakdown Estimate(
        long[]? inputWeights, long? inputTokens, long? outputTokens, long? reasoningTokens, OutputWeights outputWeights)
    {
        var estimate = new EstimatedTokenBreakdown();

        if (inputWeights is not null && inputTokens is long input && input >= 0 && Sum(inputWeights) > 0)
        {
            var shares = Distribute(input, inputWeights);
            estimate.SystemPrompt = shares[SystemPromptIndex];
            estimate.ToolDefinitions = shares[ToolDefinitionsIndex];
            estimate.UserText = shares[UserTextIndex];
            estimate.AssistantText = shares[AssistantTextIndex];
            estimate.ToolCalls = shares[ToolCallsIndex];
            estimate.ToolResults = shares[ToolResultsIndex];
            estimate.Reasoning = shares[ReasoningIndex];
            estimate.Images = shares[ImagesIndex];
            estimate.InputEstimatedCalls = 1;
        }

        if (outputTokens is long output && output >= 0)
        {
            if (reasoningTokens is long reasoning)
            {
                // The provider measured reasoning exactly: use it (clamped to [0, output]) and split
                // only the remainder over text and tool calls.
                var exactReasoning = Math.Min(Math.Max(reasoning, 0), output);
                var shares = Distribute(output - exactReasoning, new[] { outputWeights.Text, outputWeights.ToolCalls });
                estimate.OutputText = shares[0];
                estimate.OutputToolCalls = shares[1];
                estimate.OutputReasoning = exactReasoning;
            }
            else
            {
                var shares = Distribute(output, new[] { outputWeights.Text, outputWeights.ToolCalls, outputWeights.Reasoning });
                estimate.OutputText = shares[0];
                estimate.OutputToolCalls = shares[1];
                estimate.OutputReasoning = shares[2];
            }

            estimate.OutputEstimatedCalls = 1;
        }

        return estimate;
    }

    /// <summary>
    /// Distributes <paramref name="total"/> (non-negative) over the categories in proportion to
    /// <paramref name="weights"/> (non-negative) with largest-remainder rounding, ties broken by
    /// ascending category index. When every weight is zero, the whole total goes to category 0.
    /// The result always sums exactly to <paramref name="total"/>.
    /// </summary>
    internal static long[] Distribute(long total, long[] weights)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));

        var result = new long[weights.Length];
        long weightSum = 0;
        foreach (var w in weights)
        {
            if (w < 0) throw new ArgumentOutOfRangeException(nameof(weights));
            weightSum = checked(weightSum + w);
        }

        if (weightSum == 0)
        {
            result[0] = total;
            return result;
        }

        var denominator = new BigInteger(weightSum);
        var remainders = new BigInteger[weights.Length];
        long assigned = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            var product = new BigInteger(total) * weights[i];
            var quotient = BigInteger.DivRem(product, denominator, out var remainder);
            result[i] = (long)quotient;
            remainders[i] = remainder;
            assigned += result[i];
        }

        // At most weights.Length - 1 tokens remain; each goes to the largest remainder not yet
        // served, the lowest index winning ties.
        var served = new bool[weights.Length];
        for (long left = total - assigned; left > 0; left--)
        {
            int best = -1;
            for (int i = 0; i < weights.Length; i++)
            {
                if (served[i]) continue;
                if (best < 0 || remainders[i] > remainders[best]) best = i;
            }

            served[best] = true;
            result[best]++;
        }

        return result;
    }

    private static long Sum(long[] values)
    {
        long sum = 0;
        foreach (var v in values) sum = checked(sum + v);
        return sum;
    }

    /// <summary>
    /// Output character weights of one call, accumulated from response messages or streamed
    /// updates: text, tool calls (name + arguments) and reasoning content.
    /// </summary>
    internal sealed class OutputWeights
    {
        /// <summary>Characters of <see cref="TextContent"/> received.</summary>
        internal long Text { get; private set; }

        /// <summary>Characters of <see cref="FunctionCallContent"/> (name + arguments) received.</summary>
        internal long ToolCalls { get; private set; }

        /// <summary>Characters of <see cref="TextReasoningContent"/> received.</summary>
        internal long Reasoning { get; private set; }

        /// <summary>Adds the weight of every relevant item in <paramref name="contents"/>.</summary>
        internal void Add(IEnumerable<AIContent>? contents)
        {
            if (contents is null) return;
            foreach (var content in contents)
            {
                switch (content)
                {
                    case TextContent tc:
                        Text = checked(Text + (tc.Text?.Length ?? 0));
                        break;
                    case FunctionCallContent fc:
                        ToolCalls = checked(ToolCalls + EstimateCallLength(fc));
                        break;
                    case TextReasoningContent rc:
                        Reasoning = checked(Reasoning + (rc.Text?.Length ?? 0));
                        break;
                }
            }
        }

        /// <summary>Adds the weight of every message in <paramref name="messages"/>.</summary>
        internal void Add(IEnumerable<ChatMessage>? messages)
        {
            if (messages is null) return;
            foreach (var message in messages)
            {
                if (message is null) continue;
                Add(message.Contents);
            }
        }
    }
}
