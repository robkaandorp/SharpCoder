using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>Deterministic category estimates using the usage suites' scripted IChatClient pattern.</summary>
public class UsageBreakdownEstimateTests
{
    private static long[] Input(EstimatedTokenBreakdown e) =>
        [e.SystemPrompt, e.ToolDefinitions, e.UserText, e.AssistantText, e.ToolCalls, e.ToolResults, e.Reasoning, e.Images];
    private static long[] Output(EstimatedTokenBreakdown e) => [e.OutputText, e.OutputToolCalls, e.OutputReasoning];
    private static long[] Values(EstimatedTokenBreakdown e) =>
        [.. Input(e), .. Output(e), e.InputEstimatedCalls, e.OutputEstimatedCalls];
    private static void AssertEmpty(EstimatedTokenBreakdown e) => Assert.All(Values(e), v => Assert.Equal(0, v));
    private static void AssertSums(TokenUsage usage, int calls = 1)
    {
        Assert.Equal(usage.InputTokens, Input(usage.Estimated).Sum());
        Assert.Equal(usage.OutputTokens, Output(usage.Estimated).Sum());
        Assert.Equal(calls, usage.Estimated.InputEstimatedCalls);
        Assert.Equal(calls, usage.Estimated.OutputEstimatedCalls);
    }
    private static UsageEstimator.OutputWeights Weights(params AIContent[] contents)
    {
        var weights = new UsageEstimator.OutputWeights();
        weights.Add(contents);
        return weights;
    }
    private static FunctionCallContent Call(string name = "f", object? argument = null) =>
        new("call-1", name, argument is null ? null : new Dictionary<string, object?> { ["k"] = argument });
    private static UsageDetails Details(long? input = 10, long? output = 10, long? reasoning = null) => new()
    {
        InputTokenCount = input, OutputTokenCount = output, ReasoningTokenCount = reasoning,
        CachedInputTokenCount = 2
    };
    private static ChatResponse Response(IEnumerable<AIContent> contents, UsageDetails? details = null) =>
        new(new ChatMessage(ChatRole.Assistant, contents.ToList()))
        { Usage = details ?? Details(), FinishReason = ChatFinishReason.Stop, ModelId = "estimate-model" };
    private static ChatResponseUpdate Update(params AIContent[] contents) =>
        new(ChatRole.Assistant, contents);
    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(params ChatResponseUpdate[] updates)
    {
        await Task.CompletedTask;
        foreach (var update in updates) yield return update;
    }
    private static AgentOptions Options() => new()
    {
        WorkDirectory = Path.GetTempPath(), EnableBash = false, EnableFileOps = false,
        EnableSkills = false, AutoLoadWorkspaceInstructions = false, SystemPrompt = "System estimate fixture"
    };

    // Estimator: expected vectors are independent of production distribution helpers.
    [Fact, Trait("EstimateArea", "Estimator")]
    public void Estimator_ThreeEqualCategoriesTenTokens_UsesFixedTieOrderAndExactSums()
    {
        var input = UsageEstimator.MeasureInput([
            new(ChatRole.System, "x"), new(ChatRole.User, "y"), new(ChatRole.Assistant, "z")], null);
        var estimate = UsageEstimator.Estimate(input, 10, 10, null,
            Weights(new TextContent("x"), Call(), new TextReasoningContent("z")));
        Assert.Equal(new long[] { 4, 0, 3, 3, 0, 0, 0, 0 }, Input(estimate));
        Assert.Equal(new long[] { 4, 3, 3 }, Output(estimate));
        Assert.Equal(10, Input(estimate).Sum());
        Assert.Equal(10, Output(estimate).Sum());
        Assert.Equal(1, estimate.InputEstimatedCalls);
        Assert.Equal(1, estimate.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(1, 2, 3, 10, 2, 3, 5)]
    [InlineData(3, 2, 1, 10, 5, 3, 2)]
    [InlineData(1, 1, 1, 2, 1, 1, 0)]
    [InlineData(0, 1, 1, 1, 0, 1, 0)]
    public void Estimator_AwkwardRemainders_AwardsLargestNotFirstRemainder(
        int a, int b, int c, int total, int first, int second, int third)
    {
        var e = UsageEstimator.Estimate([a, 0, b, c, 0, 0, 0, 0], total, total, null,
            Weights(new TextContent(new string('t', a)), Call(new string('f', b)), new TextReasoningContent(new string('r', c))));
        Assert.Equal(new long[] { first, 0, second, third, 0, 0, 0, 0 }, Input(e));
        Assert.Equal(new long[] { first, second, third }, Output(e));
        Assert.Equal(total, Input(e).Sum());
        Assert.Equal(total, Output(e).Sum());
    }

    [Fact, Trait("EstimateArea", "Estimator")]
    public void Estimator_AllInputCategoriesTied_OrderMatchesPublicCategoryOrder()
    {
        // For each prefix length, every preceding category must win before the next one.
        for (var total = 1; total <= 8; total++)
        {
            var e = UsageEstimator.Estimate(Enumerable.Repeat(1L, 8).ToArray(), total, null, null, Weights());
            Assert.Equal(Enumerable.Range(0, 8).Select(i => i < total ? 1L : 0L), Input(e));
        }
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Estimator_SingleInputKind_GetsEntireReportedInput(int category)
    {
        var weights = new long[8];
        weights[category] = 7;
        var e = UsageEstimator.Estimate(weights, 101, null, null, Weights());
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i == category ? 101L : 0L), Input(e));
        Assert.Equal(1, e.InputEstimatedCalls);
        Assert.Equal(0, e.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Estimator_SingleOutputKind_GetsEntireReportedOutput(int category)
    {
        AIContent content = category switch
        { 0 => new TextContent("seven!!"), 1 => Call("seven!!"), _ => new TextReasoningContent("seven!!") };
        var e = UsageEstimator.Estimate(null, null, 101, null, Weights(content));
        Assert.Equal(Enumerable.Range(0, 3).Select(i => i == category ? 101L : 0L), Output(e));
        Assert.Equal(1, e.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(0, 5, 5, 0)] [InlineData(3, 4, 3, 3)] [InlineData(15, 0, 0, 10)]
    public void Estimator_ReportedReasoning_ExactAndClampedIgnoringReasoningCharacterWeight(
        long reasoning, long text, long tool, long expectedReasoning)
    {
        var e = UsageEstimator.Estimate(null, null, 10, reasoning,
            Weights(new TextContent("x"), Call(), new TextReasoningContent(new string('r', 500))));
        Assert.Equal(new[] { text, tool, expectedReasoning }, Output(e));
        Assert.Equal(10, Output(e).Sum());
    }

    [Fact, Trait("EstimateArea", "Estimator")]
    public void Estimator_UnreportedReasoning_CharacterWeightSharesOutput()
    {
        var e = UsageEstimator.Estimate(null, null, 12, null,
            Weights(new TextContent("tt"), Call("fff"), new TextReasoningContent("r")));
        Assert.Equal(new long[] { 4, 6, 2 }, Output(e));
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(null, true)] [InlineData(10L, false)] [InlineData(null, false)]
    public void Estimator_InputUnreportedOrUnweighted_DoesNotEstimateInput(long? input, bool hasWeight)
    {
        var e = UsageEstimator.Estimate([hasWeight ? 7 : 0, 0, 0, 0, 0, 0, 0, 0], input, 10, null, Weights());
        Assert.All(Input(e), v => Assert.Equal(0, v));
        Assert.Equal(0, e.InputEstimatedCalls);
        Assert.Equal(new long[] { 10, 0, 0 }, Output(e));
        Assert.Equal(1, e.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(null, 0)] [InlineData(0L, 1)]
    public void Estimator_OutputUnreportedVersusReportedZero_PreservesEstimateCounter(long? output, int calls)
    {
        var e = UsageEstimator.Estimate([1, 0, 0, 0, 0, 0, 0, 0], 0, output, 7, Weights());
        Assert.All(Input(e).Concat(Output(e)), v => Assert.Equal(0, v));
        Assert.Equal(1, e.InputEstimatedCalls);
        Assert.Equal(calls, e.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData(null, 10, 0)] [InlineData(3L, 7, 3)]
    public void Estimator_NoRelevantOutputWeight_RemainderFallsBackToText(long? reasoning, long text, long reason)
    {
        var weights = reasoning.HasValue ? Weights(new TextReasoningContent("reasoning")) : Weights();
        var e = UsageEstimator.Estimate(null, null, 10, reasoning, weights);
        Assert.Equal(new long[] { text, 0, reason }, Output(e));
    }

    [Fact, Trait("EstimateArea", "Estimator")]
    public void Estimator_LargeTotals_UsesExactIntegerArithmetic()
    {
        // Multiplying the total by any nonzero weight overflows Int64; the oracle is still exact.
        var e = UsageEstimator.Estimate([2, 2, 2, 0, 0, 0, 0, 0], long.MaxValue, long.MaxValue, null,
            Weights(new TextContent("xx"), Call("ff"), new TextReasoningContent("zz")));
        Assert.Equal(new long[] { 3074457345618258603, 3074457345618258602, 3074457345618258602 }, Output(e));
        Assert.Equal(long.MaxValue, Input(e).Sum());
        Assert.Equal(long.MaxValue, Output(e).Sum());
    }

    [Theory, Trait("EstimateArea", "Estimator")]
    [InlineData("image/png")] [InlineData("IMAGE/JPEG")] [InlineData("application/pdf")]
    public void MeasureInput_ImageAndPdf_UseFlatWeightIndependentOfByteSize(string mediaType)
    {
        foreach (var bytes in new[] { new byte[] { 1 }, new byte[1000] })
        {
            var weights = UsageEstimator.MeasureInput([new(ChatRole.User, [
                new TextContent(new string('u', checked((int)AgentSession.ImageTokenEstimate * 4))),
                new DataContent(bytes, mediaType), new DataContent(bytes, "audio/wav")])], null);
            Assert.Equal(new long[] { 0, 0, AgentSession.ImageTokenEstimate * 4, 0, 0, 0, 0, AgentSession.ImageTokenEstimate * 4 }, weights);
            var e = UsageEstimator.Estimate(weights, 11, null, null, Weights());
            Assert.Equal(6, e.UserText);
            Assert.Equal(5, e.Images);
        }
    }

    [Fact, Trait("EstimateArea", "Estimator")]
    public void MeasureInput_RolesCallsResultsReasoningAndOptions_CountExactCharacterWeights()
    {
        var function = AIFunctionFactory.Create((string key) => key, "lookup", "Distinct tool description");
        var options = new ChatOptions { Instructions = "instruction", Tools = [function] };
        var weights = UsageEstimator.MeasureInput([
            new(ChatRole.System, "sys"), new(ChatRole.User, "user"), new(ChatRole.Assistant, "assist"),
            new(ChatRole.Assistant, [Call("look", "xyz"), new TextReasoningContent("reason")]),
            new(ChatRole.Tool, [new TextContent("tool"), new FunctionResultContent("call-1", "result")]),
            new(new ChatRole("unknown"), "ignored")], options);
        Assert.Equal(new long[] {
            "instruction".Length + "sys".Length,
            function.Name.Length + function.Description.Length + function.JsonSchema.GetRawText().Length,
            4, 6, 4 + 1 + 3, 4 + 6, 6, 0 }, weights);
        Assert.True(weights[1] > function.Name.Length + function.Description.Length);
    }

    [Fact, Trait("EstimateArea", "Estimator")]
    public void MeasureInput_HostedAndDeclarationTools_SchemaCountedOnlyForFunctionDeclarations()
    {
        // A hosted (non-function) tool has no JSON schema: only its name and description count.
        // A declaration-only function (no invocation) still carries a schema, which is counted.
        var hosted = new HostedWebSearchTool();
        using var schemaDoc = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");
        var declaration = AIFunctionFactory.CreateDeclaration("declared", "Declared-only tool", schemaDoc.RootElement.Clone());
        var hostedOnly = UsageEstimator.MeasureInput([], new ChatOptions { Tools = [hosted] });
        Assert.Equal(new long[] { 0, hosted.Name.Length + (hosted.Description?.Length ?? 0), 0, 0, 0, 0, 0, 0 }, hostedOnly);
        var declared = UsageEstimator.MeasureInput([], new ChatOptions { Tools = [declaration] });
        Assert.Equal("declared".Length + "Declared-only tool".Length + declaration.JsonSchema.GetRawText().Length, declared[1]);
        Assert.True(declaration.JsonSchema.GetRawText().Length > 0);
        Assert.All(declared.Where((_, i) => i != 1), v => Assert.Equal(0, v));
        var both = UsageEstimator.MeasureInput([], new ChatOptions { Tools = [hosted, declaration] });
        Assert.Equal(hostedOnly[1] + declared[1], both[1]);
    }

    // Wrapper tests observe the real event, not a copied estimate computed by the test.
    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_BothModes_EventContainsExpectedInputAndOutputCategories(bool streaming)
    {
        var response = Response([new TextContent("x"), Call(), new TextReasoningContent("r")]);
        var client = new ScriptedChatClient(_ => response, _ => Stream(
            Update(new TextContent("x")), Update(Call()), Update(new TextReasoningContent("r")),
            Update(new UsageContent(Details()))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        ChatMessage[] messages = [new(ChatRole.System, "s"), new(ChatRole.User, "u"), new(ChatRole.Assistant, "a")];
        await Invoke(recording, messages, streaming);
        var recorded = Assert.Single(events);
        Assert.Equal(new long[] { 4, 0, 3, 3, 0, 0, 0, 0 }, Input(recorded.Usage.Estimated));
        Assert.Equal(new long[] { 4, 3, 3 }, Output(recorded.Usage.Estimated));
        AssertSums(recorded.Usage);
        Assert.Equal(2, recorded.Usage.CachedInputTokens);
        Assert.Equal(1, recorded.Usage.CachedInputReportedCalls);
        Assert.Same(messages, client.ReceivedMessages);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_ChatOptionsInstructionsAndTools_CountAsSystemPromptAndToolDefinitions(bool streaming)
    {
        // Weights: instructions (4) + system text (4) = 8 → SystemPrompt; tool = name + description
        // + schema; user text 8. Expected shares are computed by hand from those weights below.
        var function = AIFunctionFactory.Create((string key) => key, "lookup", "desc");
        var toolWeight = "lookup".Length + "desc".Length + function.JsonSchema.GetRawText().Length;
        var options = new ChatOptions { Instructions = "inst", Tools = [function] };
        var totalWeight = 8 + toolWeight + 8;
        var client = new ScriptedChatClient(_ => Response([new TextContent("done")], Details(totalWeight, 4)),
            _ => Stream(Update(new TextContent("done")), Update(new UsageContent(Details(totalWeight, 4)))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        ChatMessage[] messages = [new(ChatRole.System, "sysp"), new(ChatRole.User, "usertext")];
        if (!streaming) await recording.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);
        else await foreach (var _ in recording.GetStreamingResponseAsync(messages, options, TestContext.Current.CancellationToken)) { }
        var usage = Assert.Single(events).Usage;
        // Input tokens equal the character weight, so every share is exact (no rounding).
        Assert.Equal(new long[] { 8, toolWeight, 8, 0, 0, 0, 0, 0 }, Input(usage.Estimated));
        Assert.Equal(new long[] { 4, 0, 0 }, Output(usage.Estimated));
        AssertSums(usage);
        Assert.Same(options, client.ReceivedOptions);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_ReadOnlyCollectionMessages_PassedThroughSameInstanceWithoutCopy(bool streaming)
    {
        // A materialised IReadOnlyCollection that is NOT an IList must reach the inner client as-is.
        var messages = new ReadOnlyOnlyCollection([new(ChatRole.User, "u")]);
        Assert.False(((object)messages) is IList<ChatMessage>);
        var client = new ScriptedChatClient(_ => Response([new TextContent("done")]),
            _ => Stream(Update(new TextContent("done")), Update(new UsageContent(Details()))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, messages, streaming);
        Assert.Same(messages, client.ReceivedMessages);
        Assert.Equal(new long[] { 0, 0, 10, 0, 0, 0, 0, 0 }, Input(Assert.Single(events).Usage.Estimated));
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_OneShotMessages_EnumeratedOnceAndReachInnerIntact(bool streaming)
    {
        ChatMessage[] original = [new(ChatRole.System, "s"), new(ChatRole.User, "u"), new(ChatRole.Assistant, "a")];
        var oneShot = new OneShotMessages(original);
        var client = new ScriptedChatClient(_ => Response([new TextContent("done")]),
            _ => Stream(Update(new TextContent("done")), Update(new UsageContent(Details()))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, oneShot, streaming);
        Assert.Equal(1, oneShot.Enumerations);
        Assert.IsType<List<ChatMessage>>(client.ReceivedMessages);
        Assert.Equal(original.Length, client.Requests[0].Length);
        for (var i = 0; i < original.Length; i++) Assert.Same(original[i], client.Requests[0][i]);
        Assert.Equal(new long[] { 4, 0, 3, 3, 0, 0, 0, 0 }, Input(Assert.Single(events).Usage.Estimated));
        AssertSums(events[0].Usage);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_StreamingEarlyEndOrDisposal_EstimatesOnlyArrivedContent(bool disposeEarly)
    {
        var client = new ScriptedChatClient(stream: _ => Stream(
            Update(new UsageContent(Details()), new TextContent("tt"), Call(), new TextReasoningContent("r")),
            Update(new TextContent(new string('x', 100)), new UsageContent(Details(100, 100)))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        if (disposeEarly)
        {
            await using (var enumerator = recording.GetStreamingResponseAsync([new(ChatRole.User, "u")], null, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken))
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Empty(events); // Recording happens only once the enumeration ends.
            }
            Assert.Equal(1, client.DeliveredUpdates);
        }
        else
        {
            // A naturally early-ended provider stream has the same received prefix.
            client.StreamScript = _ => Stream(Update(new UsageContent(Details()), new TextContent("tt"), Call(), new TextReasoningContent("r")));
            await Invoke(recording, [new(ChatRole.User, "u")], true);
        }
        var usage = Assert.Single(events).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(new long[] { 5, 3, 2 }, Output(usage.Estimated));
        AssertSums(usage);
    }

    [Fact, Trait("EstimateArea", "Recording")]
    public async Task Recording_StreamingSplitUsage_SumsTotalsAndCountsEachEstimateOnce()
    {
        var client = new ScriptedChatClient(stream: _ => Stream(
            Update(new TextContent("x"), new UsageContent(Details(4, null))),
            Update(Call(), new UsageContent(Details(6, 3))),
            Update(new TextReasoningContent("r"), new UsageContent(Details(null, 7)))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, [new(ChatRole.User, "u")], true);
        var usage = Assert.Single(events).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(new long[] { 4, 3, 3 }, Output(usage.Estimated));
        AssertSums(usage);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_ReportedReasoning_UsesExactCountNotCharacterShare(bool streaming)
    {
        var contents = new AIContent[] { new TextContent("x"), Call(), new TextReasoningContent(new string('r', 300)) };
        var client = new ScriptedChatClient(_ => Response(contents, Details(reasoning: 3)),
            _ => Stream(Update(contents), Update(new UsageContent(Details(reasoning: 3)))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, [new(ChatRole.User, "u")], streaming);
        var usage = Assert.Single(events).Usage;
        Assert.Equal(new long[] { 4, 3, 3 }, Output(usage.Estimated));
        Assert.Equal(3, usage.ReasoningTokens);
        Assert.Equal(1, usage.ReasoningReportedCalls);
        AssertSums(usage);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_RepeatedOutputAcrossMessagesOrChunks_AccumulatesEveryCharacter(bool streaming)
    {
        AIContent[] first = [new TextContent("t"), Call(), new TextReasoningContent("r")];
        AIContent[] second = [new TextContent("tt"), Call("ff", "xy"), new TextReasoningContent("rr")];
        var response = new ChatResponse(new List<ChatMessage> {
            new(ChatRole.Assistant, first), new(ChatRole.Assistant, second) }) { Usage = Details() };
        var client = new ScriptedChatClient(_ => response,
            _ => Stream(Update(first), Update(second), Update(new UsageContent(Details()))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, [new(ChatRole.User, "u")], streaming);
        var usage = Assert.Single(events).Usage;
        // Text=3, tool calls=1+(2+1+2)=6, reasoning=3. Text wins the 0.5 remainder tie.
        Assert.Equal(new long[] { 3, 5, 2 }, Output(usage.Estimated));
        AssertSums(usage);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public async Task Recording_MissingOrZeroUsage_DistinguishesReportedCountsInBothModes(bool streaming, int mode)
    {
        UsageDetails? details = mode switch {
            0 => null, 1 => Details(10, null), 2 => Details(null, 10), _ => Details(0, 0) };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")) { Usage = details };
        var client = new ScriptedChatClient(_ => response,
            _ => details is null ? Stream(Update(new TextContent("done"))) :
                Stream(Update(new TextContent("done")), Update(new UsageContent(details))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        await Invoke(recording, [new(ChatRole.User, "u")], streaming);
        var usage = Assert.Single(events).Usage;
        Assert.Equal(1, usage.Calls);
        Assert.Equal(details?.InputTokenCount ?? 0, usage.InputTokens);
        Assert.Equal(details?.OutputTokenCount ?? 0, usage.OutputTokens);
        Assert.Equal(new long[] { 0, 0, mode == 1 ? 10 : 0, 0, 0, 0, 0, 0 }, Input(usage.Estimated));
        Assert.Equal(new long[] { mode == 2 ? 10 : 0, 0, 0 }, Output(usage.Estimated));
        Assert.Equal(mode is 1 or 3 ? 1 : 0, usage.Estimated.InputEstimatedCalls);
        Assert.Equal(mode is 2 or 3 ? 1 : 0, usage.Estimated.OutputEstimatedCalls);
    }

    [Theory, Trait("EstimateArea", "Recording")]
    [InlineData(false)] [InlineData(true)]
    public async Task Recording_StreamingProviderFailsAfterUsage_KeepsEstimateAndOriginalException(bool cancellation)
    {
        using var cts = new CancellationTokenSource();
        Exception error = cancellation ? new OperationCanceledException(cts.Token) : new InvalidOperationException("provider fault");
        async IAsyncEnumerable<ChatResponseUpdate> PartialStream()
        {
            await Task.CompletedTask;
            yield return Update(new TextContent("x"), Call(), new TextReasoningContent("r"), new UsageContent(Details()));
            throw error;
        }
        var client = new ScriptedChatClient(stream: _ => PartialStream());
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events);
        var actual = await Record.ExceptionAsync(() => Invoke(recording, [new(ChatRole.User, "u")], true));
        Assert.Same(error, actual);
        if (cancellation) Assert.Equal(cts.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        var usage = Assert.Single(events).Usage;
        Assert.Equal(new long[] { 4, 3, 3 }, Output(usage.Estimated));
        AssertSums(usage);
    }

    // Actual measurement faults via argument/result ToString; both output containment sites are exercised.
    [Theory, Trait("EstimateArea", "Containment")]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Recording_EstimatorThrows_CallSucceedsAndRecordsWithoutEstimate(
        bool streaming, bool outputFault, bool loggerThrows)
    {
        var fault = new ThrowingValue();
        var logger = new EstimationLogger(loggerThrows);
        AIContent[] output = outputFault ? [Call("f", fault)] : [new TextContent("done")];
        ChatMessage[] input = outputFault ? [new(ChatRole.User, "u")] :
            [new(ChatRole.Tool, [new FunctionResultContent("call-1", fault)])];
        var response = Response(output);
        var client = new ScriptedChatClient(_ => response,
            _ => Stream(Update(output), Update(new UsageContent(Details()))));
        var events = new List<UsageEvent>();
        using var recording = Recording(client, events, logger);
        var returned = await Invoke(recording, input, streaming);
        if (!streaming) Assert.Same(response, returned);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, fault.Attempts); // The failure really occurred inside the estimator.
        Assert.Same(fault.Error, Assert.Single(logger.Warnings));
        var usage = Assert.Single(events).Usage;
        Assert.Equal(1, usage.Calls);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(2, usage.CachedInputTokens);
        AssertEmpty(usage.Estimated);
        if (streaming) Assert.Equal(2, client.DeliveredUpdates);
    }

    // End-to-end: all three real agent execution paths, including tool-result consumption.
    [Theory, Trait("EstimateArea", "EndToEnd")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task CodingAgent_ToolLoop_AllThreePathsCarryExactSumBreakdown(int path)
    {
        var toolInvocations = 0;
        var events = new List<UsageEvent>();
        var options = Options();
        options.ShowToolCallsInStream = path == 2;
        options.OnUsage = events.Add;
        options.CustomTools = [AIFunctionFactory.Create((string id) =>
        { toolInvocations++; return "distinct-tool-result:" + id; }, "do_thing", "Estimate fixture tool")];
        Assert.Equal(path == 2, options.ShowToolCallsInStream); // Select the manual loop, not just a named case.
        ChatResponse Round(int index) => index == 0
            ? new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("tool-1", "do_thing", new Dictionary<string, object?> { ["id"] = "x" })]))
              { Usage = Details(10000, 10), FinishReason = ChatFinishReason.ToolCalls, ModelId = "estimate-model" }
            : Response([new TextContent("done")], Details(15000, 20));
        var client = new ScriptedChatClient(Round, index =>
        {
            var round = Round(index);
            return Stream(Update(round.Messages[0].Contents.ToArray()), Update(new UsageContent(round.Usage!)),
                new ChatResponseUpdate { FinishReason = round.FinishReason, ModelId = round.ModelId });
        });
        await using var agent = new CodingAgent(client, options);
        var session = AgentSession.Create("estimate-tool-loop");
        AgentResult? result = null;
        var deltas = new List<string>();
        if (path == 0) result = await agent.ExecuteAsync(session, "Do the thing", TestContext.Current.CancellationToken);
        else await foreach (var update in agent.ExecuteStreamingAsync(session, "Do the thing", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.Completed) result = update.Result;
            if (update.Kind == StreamingUpdateKind.TextDelta) deltas.Add(update.Text ?? "");
        }
        Assert.NotNull(result);
        Assert.Equal("Success", result.Status);
        Assert.Equal(1, toolInvocations);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(25000, result.TokenUsage.Total.InputTokens);
        Assert.Equal(30, result.TokenUsage.Total.OutputTokens);
        AssertSums(result.TokenUsage.Total, 2);
        Assert.True(result.TokenUsage.Total.Estimated.ToolDefinitions > 0);
        Assert.True(result.TokenUsage.Total.Estimated.ToolResults > 0);
        AssertSums(session.Usage.Total, 2);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => AssertSums(e.Usage));
        Assert.Equal(0, events[0].Usage.Estimated.ToolResults);
        Assert.True(events[1].Usage.Estimated.ToolResults > 0);
        Assert.True(events[1].Usage.Estimated.ToolCalls > 0);
        Assert.Equal(10, events[0].Usage.Estimated.OutputToolCalls);
        var expected = Values(events[0].Usage.Estimated).Zip(Values(events[1].Usage.Estimated), (a, b) => a + b).ToArray();
        Assert.Equal(expected, Values(result.TokenUsage.Total.Estimated));
        Assert.Equal(expected, Values(session.Usage.Total.Estimated));
        Assert.Equal(expected, Values(Assert.Single(result.TokenUsage.Entries).Usage.Estimated));
        Assert.Contains(client.Requests[1].SelectMany(m => m.Contents), c => c is FunctionResultContent);
        if (path == 2) Assert.Contains("\U0001F527 do_thing(", string.Concat(deltas));
        if (path == 1) Assert.DoesNotContain("\U0001F527 do_thing(", string.Concat(deltas));
    }

    [Fact, Trait("EstimateArea", "PassThrough")]
    public async Task Compaction_RealSummaryCall_EventAndSessionCarryEstimate()
    {
        var client = new ScriptedChatClient(_ => Response([new TextContent("Summary fixture")], Details(1200, 40, 5)));
        var compactor = new ContextCompactor(client);
        var options = Options();
        options.CompactionRetainRecent = 2;
        var events = new List<UsageEvent>();
        options.OnUsage = events.Add;
        var session = AgentSession.Create("estimate-compaction");
        for (var i = 0; i < 12; i++) session.MessageHistory.Add(new(ChatRole.User, $"Message {i}: " + new string('x', 200)));
        Assert.True(await compactor.ForceCompactAsync(session, options, TestContext.Current.CancellationToken));
        Assert.Equal(1, client.CallCount);
        var recorded = Assert.Single(events);
        Assert.Equal(UsageSource.Compaction, recorded.Source);
        AssertSums(recorded.Usage);
        // The compactor sends its summarisation prompt as a user message, not a system message.
        Assert.Equal(1200, recorded.Usage.Estimated.UserText);
        Assert.Equal(5, recorded.Usage.Estimated.OutputReasoning);
        AssertSums(Assert.Single(session.Usage.Entries).Usage);
        Assert.Equal(Values(recorded.Usage.Estimated), Values(session.Usage.Total.Estimated));
        Assert.Equal(Values(recorded.Usage.Estimated), Values(Assert.Single(session.Usage.Entries).Usage.Estimated));
    }

    [Theory, Trait("EstimateArea", "PassThrough")]
    [InlineData(UsageSource.Agent, UsageSource.SubAgent)]
    [InlineData(UsageSource.Compaction, UsageSource.SubAgentCompaction)]
    public void MapChildUsage_PreservesEveryCategoryDetached(UsageSource source, UsageSource mappedSource)
    {
        var child = new UsageEvent(source, "child-model", SeedUsage());
        var expected = Values(child.Usage.Estimated);
        var mapped = SubAgentManager.MapChildUsage("sub-7", child);
        Assert.Equal(mappedSource, mapped.Source);
        Assert.Equal("child-model", mapped.Model);
        Assert.Equal("sub-7", mapped.SubAgentId);
        Assert.Equal(expected, Values(mapped.Usage.Estimated));
        Assert.NotSame(child.Usage.Estimated, mapped.Usage.Estimated);
        ChangeAll(child.Usage.Estimated);
        Assert.Equal(expected, Values(mapped.Usage.Estimated));
    }

    [Fact, Trait("EstimateArea", "PassThrough")]
    public async Task SubAgent_RealChildCall_ForwardsBreakdownAndSubAgentInfoIncludesIt()
    {
        var client = new ScriptedChatClient(_ => Response([new TextContent("child done")], Details(10000, 10)));
        var events = new List<UsageEvent>();
        var options = Options();
        options.OnUsage = events.Add;
        await using var manager = new SubAgentManager(new SubAgentOptions { DefaultClient = client }, client, options, logger: null);
        var started = await manager.StartAsync(new SubAgentRequest { Task = "answer fixture" }, TestContext.Current.CancellationToken);
        var child = Assert.Single(await manager.AwaitAsync([started.Id], TestContext.Current.CancellationToken));
        Assert.Equal(SubAgentStatus.Completed, child.Status);
        var recorded = Assert.Single(events);
        Assert.Equal(UsageSource.SubAgent, recorded.Source);
        Assert.Equal(started.Id, recorded.SubAgentId);
        AssertSums(recorded.Usage);
        Assert.NotNull(child.Usage);
        AssertSums(child.Usage);
        Assert.Equal(Values(recorded.Usage.Estimated), Values(child.Usage.Estimated));
    }

    [Fact, Trait("EstimateArea", "PassThrough")]
    public async Task SubAgentInfo_Usage_EstimateIsDetachedFromManagerState()
    {
        var client = new ScriptedChatClient(_ => Response([new TextContent("child done")], Details(10000, 10)));
        var options = Options();
        await using var manager = new SubAgentManager(new SubAgentOptions { DefaultClient = client }, client, options, logger: null);
        var started = await manager.StartAsync(new SubAgentRequest { Task = "answer fixture" }, TestContext.Current.CancellationToken);
        var first = Assert.Single(await manager.AwaitAsync([started.Id], TestContext.Current.CancellationToken));
        Assert.NotNull(first.Usage);
        var expected = Values(first.Usage.Estimated);
        Assert.Equal(1, first.Usage.Estimated.InputEstimatedCalls);
        ChangeAll(first.Usage.Estimated);
        var second = Assert.Single(manager.GetStatus(started.Id));
        Assert.NotNull(second.Usage);
        Assert.NotSame(first.Usage.Estimated, second.Usage.Estimated);
        Assert.Equal(expected, Values(second.Usage.Estimated));
    }

    [Theory, Trait("EstimateArea", "PassThrough")]
    [InlineData(false)] [InlineData(true)]
    public async Task CodingAgent_PreRunCompaction_CompactionEntryCarriesExactSumEstimate(bool streaming)
    {
        var compaction = new ScriptedChatClient(_ => Response([new TextContent("Summary fixture")], Details(900, 33)));
        var main = new ScriptedChatClient(_ => Response([new TextContent("done")], Details(1000, 10)),
            _ => Stream(Update(new TextContent("done")), Update(new UsageContent(Details(1000, 10)))));
        var options = Options();
        options.CompactionClient = compaction;
        options.MaxContextTokens = 1_000;
        options.CompactionThreshold = 0.5;
        options.CompactionRetainRecent = 1;
        var session = AgentSession.Create("estimate-agent-compaction");
        for (var i = 0; i < 12; i++)
            session.MessageHistory.Add(new(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"Message {i}: " + new string('a', 200)));
        session.LastKnownContextTokens = 900; // above the 500-token threshold
        await using var agent = new CodingAgent(main, options);
        AgentResult? result = null;
        if (!streaming) result = await agent.ExecuteAsync(session, "go", TestContext.Current.CancellationToken);
        else await foreach (var update in agent.ExecuteStreamingAsync(session, "go", TestContext.Current.CancellationToken))
            if (update.Kind == StreamingUpdateKind.Completed) result = update.Result;
        Assert.NotNull(result);
        Assert.Equal("Success", result.Status);
        Assert.Equal(1, compaction.CallCount);
        var entry = Assert.Single(result.TokenUsage.Entries, e => e.Source == UsageSource.Compaction);
        Assert.Equal(900, entry.Usage.InputTokens);
        AssertSums(entry.Usage);
        Assert.True(entry.Usage.Estimated.UserText > 0);
        var agentEntry = Assert.Single(result.TokenUsage.Entries, e => e.Source == UsageSource.Agent);
        AssertSums(agentEntry.Usage);
        AssertSums(result.TokenUsage.Total, 2);
        Assert.Equal(Values(entry.Usage.Estimated),
            Values(Assert.Single(session.Usage.Entries, e => e.Source == UsageSource.Compaction).Usage.Estimated));
    }

    [Fact, Trait("EstimateArea", "PassThrough")]
    public void UsageEvent_Constructor_CopiesEveryCategoryDetached()
    {
        var source = SeedUsage();
        var expected = Values(source.Estimated);
        var recorded = new UsageEvent(UsageSource.Agent, "model", source);
        Assert.NotSame(source.Estimated, recorded.Usage.Estimated);
        ChangeAll(source.Estimated);
        Assert.Equal(expected, Values(recorded.Usage.Estimated));
    }

    [Fact, Trait("EstimateArea", "Detachment")]
    public void EstimatedBreakdown_AddAndClone_SumEveryFieldDetachedAndIgnoreNull()
    {
        var source = SeedUsage().Estimated;
        var expected = Values(source);
        var clone = source.Clone();
        var total = new EstimatedTokenBreakdown();
        Assert.Same(total, total.Add(source));
        total.Add(source);
        Assert.Same(total, total.Add(null));
        Assert.Equal(expected.Select(v => v * 2), Values(total));
        Assert.NotSame(source, clone);
        ChangeAll(source);
        Assert.Equal(expected, Values(clone));
        Assert.Equal(expected.Select(v => v * 2), Values(total));
        ChangeAll(clone);
        Assert.Equal(expected.Select(v => v * 2), Values(total));
    }

    [Fact, Trait("EstimateArea", "Detachment")]
    public void TokenUsage_AddAndClone_DeepCopyAllEstimatedFields()
    {
        var source = SeedUsage();
        var expected = Values(source.Estimated);
        var clone = source.Clone();
        var total = new TokenUsage();
        Assert.Same(total, total.Add(source));
        total.Add(source).Add(null);
        Assert.Equal(expected.Select(v => v * 2), Values(total.Estimated));
        Assert.NotSame(source.Estimated, clone.Estimated);
        Assert.NotSame(source.Estimated, total.Estimated);
        ChangeAll(source.Estimated);
        Assert.Equal(expected, Values(clone.Estimated));
        Assert.Equal(expected.Select(v => v * 2), Values(total.Estimated));
        ChangeAll(clone.Estimated);
        Assert.Equal(expected.Select(v => v * 2), Values(total.Estimated));
    }

    [Fact, Trait("EstimateArea", "Detachment")]
    public void UsageSummary_SnapshotAndReadbacks_DetachEstimatedFromEventsAndLaterAdds()
    {
        var recorded = new UsageEvent(UsageSource.Agent, "model", SeedUsage());
        var expected = Values(recorded.Usage.Estimated);
        var summary = new UsageSummary();
        summary.Add(recorded);
        var snapshot = summary.Snapshot();
        ChangeAll(recorded.Usage.Estimated);
        summary.Add(new UsageEvent(UsageSource.Agent, "model", SeedUsage()));
        Assert.Equal(expected, Values(snapshot.Total.Estimated));
        Assert.Equal(expected, Values(Assert.Single(snapshot.Entries).Usage.Estimated));
        Assert.Equal(expected.Select(v => v * 2), Values(summary.Total.Estimated));
        var entries = snapshot.Entries;
        ChangeAll(entries[0].Usage.Estimated);
        Assert.Equal(expected, Values(snapshot.Total.Estimated)); // Getter is detached.
        snapshot.Entries = entries; // Actually mutate the snapshot, not just its detached getter.
        Assert.All(Values(snapshot.Total.Estimated), v => Assert.Equal(999, v));
        entries[0].Usage.Estimated.UserText = 1234;
        Assert.Equal(999, snapshot.Total.Estimated.UserText); // Setter copies its input too.
        Assert.Equal(expected.Select(v => v * 2), Values(summary.Total.Estimated));
        ChangeAll(summary.Total.Estimated);
        Assert.Equal(expected.Select(v => v * 2), Values(summary.Total.Estimated));
    }

    [Fact, Trait("EstimateArea", "Persistence")]
    public async Task Session_SaveLoad_RoundTripsEveryEstimatedField()
    {
        var session = AgentSession.Create("estimate-persist");
        session.Usage.Add(new UsageEvent(UsageSource.Agent, "model-a", SeedUsage()));
        session.Usage.Add(new UsageEvent(UsageSource.Compaction, "model-b", SeedUsage()));
        var path = Path.Combine(Path.GetTempPath(), $"estimate-{Guid.NewGuid():N}.json");
        try
        {
            await session.SaveAsync(path, TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("estimated", json, StringComparison.OrdinalIgnoreCase);
            var loaded = await AgentSession.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(Values(session.Usage.Total.Estimated), Values(loaded.Usage.Total.Estimated));
            Assert.Equal(2, loaded.Usage.Entries.Count);
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(session.Usage.Entries[i].Source, loaded.Usage.Entries[i].Source);
                Assert.Equal(session.Usage.Entries[i].Model, loaded.Usage.Entries[i].Model);
                Assert.Equal(Values(session.Usage.Entries[i].Usage.Estimated), Values(loaded.Usage.Entries[i].Usage.Estimated));
            }
        }
        finally { File.Delete(path); }
    }

    [Theory, Trait("EstimateArea", "Persistence")]
    [InlineData(false)] [InlineData(true)]
    public async Task Session_LegacyWithoutEstimatedOrUsage_LoadsEmptyBreakdown(bool removeUsage)
    {
        var session = AgentSession.Create("estimate-legacy");
        session.InputTokensUsed = 123;
        session.OutputTokensUsed = 45;
        session.Usage.Add(new UsageEvent(UsageSource.Agent, "model", SeedUsage()));
        var path = Path.Combine(Path.GetTempPath(), $"estimate-legacy-{Guid.NewGuid():N}.json");
        try
        {
            await session.SaveAsync(path, TestContext.Current.CancellationToken);
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            RemoveProperty(node, removeUsage ? "usage" : "estimated");
            var legacy = node.ToJsonString();
            Assert.DoesNotContain(removeUsage ? "\"usage\":" : "\"estimated\":", legacy, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, legacy, TestContext.Current.CancellationToken);
            var loaded = await AgentSession.LoadAsync(path, TestContext.Current.CancellationToken);
            AssertEmpty(loaded.Usage.Total.Estimated);
            Assert.Equal(123, loaded.InputTokensUsed);
            Assert.Equal(45, loaded.OutputTokensUsed);
            if (removeUsage) Assert.Empty(loaded.Usage.Entries);
            else
            {
                Assert.Equal(36, loaded.Usage.Total.InputTokens);
                AssertEmpty(Assert.Single(loaded.Usage.Entries).Usage.Estimated);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact, Trait("EstimateArea", "Persistence")]
    public void TokenUsage_NullEstimated_AlwaysReturnsNonNullEmptyBreakdown()
    {
        var usage = JsonSerializer.Deserialize<TokenUsage>("{\"Estimated\":null}")!;
        AssertEmpty(usage.Estimated);
        usage.Estimated = null!;
        AssertEmpty(usage.Estimated);
    }

    private static void RemoveProperty(JsonNode node, string name)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) obj.Remove(key);
                else if (obj[key] is { } child) RemoveProperty(child, name);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) RemoveProperty(child, name);
    }
    private static TokenUsage SeedUsage() => new()
    {
        InputTokens = 36, OutputTokens = 30, Calls = 1,
        Estimated = new()
        {
            SystemPrompt = 1, ToolDefinitions = 2, UserText = 3, AssistantText = 4,
            ToolCalls = 5, ToolResults = 6, Reasoning = 7, Images = 8,
            OutputText = 9, OutputToolCalls = 10, OutputReasoning = 11,
            InputEstimatedCalls = 1, OutputEstimatedCalls = 1
        }
    };
    private static void ChangeAll(EstimatedTokenBreakdown estimate)
    {
        foreach (var property in typeof(EstimatedTokenBreakdown).GetProperties())
            property.SetValue(estimate, property.PropertyType == typeof(long) ? (object)999L : 999);
    }
    private static UsageRecordingChatClient Recording(ScriptedChatClient client, List<UsageEvent> events, ILogger? logger = null)
    {
        var options = Options();
        options.OnUsage = events.Add;
        return new(client, new UsageRecorder(options, session: null, logger ?? NullLogger.Instance));
    }
    private static async Task<ChatResponse?> Invoke(UsageRecordingChatClient client, IEnumerable<ChatMessage> messages, bool streaming)
    {
        if (!streaming) return await client.GetResponseAsync(messages, null, TestContext.Current.CancellationToken);
        await foreach (var _ in client.GetStreamingResponseAsync(messages, null, TestContext.Current.CancellationToken)) { }
        return null;
    }

    // Scripted delegates and request readback follow UsageAccountingTests, with request inspection
    // necessary for the one-shot and real tool-result acceptance criteria.
    private sealed class ScriptedChatClient(
        Func<int, ChatResponse>? response = null,
        Func<int, IAsyncEnumerable<ChatResponseUpdate>>? stream = null) : IChatClient
    {
        internal Func<int, IAsyncEnumerable<ChatResponseUpdate>>? StreamScript { get; set; } = stream;
        internal int CallCount { get; private set; }
        internal int DeliveredUpdates { get; private set; }
        internal IEnumerable<ChatMessage>? ReceivedMessages { get; private set; }
        internal ChatOptions? ReceivedOptions { get; private set; }
        internal List<ChatMessage[]> Requests { get; } = [];
        private int Receive(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            ReceivedMessages = messages;
            ReceivedOptions = options;
            Requests.Add(messages.ToArray());
            return CallCount++;
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult((response ?? throw new NotSupportedException())(Receive(messages, options)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Receive(messages, options);
            await foreach (var update in (StreamScript ?? throw new NotSupportedException())(index).WithCancellation(cancellationToken))
            {
                DeliveredUpdates++;
                yield return update;
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class OneShotMessages(ChatMessage[] messages) : IEnumerable<ChatMessage>
    {
        internal int Enumerations { get; private set; }
        public IEnumerator<ChatMessage> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("One-shot sequence enumerated twice");
            return ((IEnumerable<ChatMessage>)messages).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ReadOnlyOnlyCollection(ChatMessage[] messages) : IReadOnlyCollection<ChatMessage>
    {
        public int Count => messages.Length;
        public IEnumerator<ChatMessage> GetEnumerator() => ((IEnumerable<ChatMessage>)messages).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ThrowingValue
    {
        internal Exception Error { get; } = new InvalidOperationException("estimator measurement fault");
        internal int Attempts { get; private set; }
        public override string ToString() { Attempts++; throw Error; }
    }
    private sealed class EstimationLogger(bool throws) : ILogger
    {
        internal List<Exception?> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Warnings.Add(exception);
            if (throws) throw new InvalidOperationException("estimation logger fault");
        }
    }
}
