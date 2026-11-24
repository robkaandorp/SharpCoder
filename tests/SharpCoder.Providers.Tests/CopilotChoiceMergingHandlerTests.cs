using Microsoft.Extensions.AI;

using OpenAI;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using CopilotChoiceMergingHandler = SharpCoder.Providers.ChatClientFactory.CopilotChoiceMergingHandler;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Tests for the CopilotChoiceMergingHandler in ChatClientFactory.
/// Validates that the handler correctly handles edge cases in the Copilot API responses.
/// </summary>
public sealed class CopilotChoiceMergingHandlerTests
{
    /// <summary>
    /// When the Copilot API returns an empty choices array, the handler must synthesize
    /// a minimal stop choice so the OpenAI SDK doesn't crash with "Index was out of range".
    /// </summary>
    [Fact]
    public async Task EmptyChoicesArray_SynthesizesStopChoice()
    {
        var responseBody = """{"choices":[],"created":1774365638,"id":"test","model":"claude-opus-4.6"}""";
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);
        Assert.Equal("stop", choices[0]?["finish_reason"]?.GetValue<string>());
        Assert.Equal("assistant", choices[0]?["message"]?["role"]?.GetValue<string>());
    }

    /// <summary>
    /// When there's exactly one choice, the handler passes it through unchanged.
    /// </summary>
    [Fact]
    public async Task SingleChoice_PassesThrough()
    {
        var responseBody = """{"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"model":"test"}""";
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);
        Assert.Equal("hello", choices[0]?["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>
    /// When there are two choices (text + tool_calls), they get merged into one.
    /// </summary>
    [Fact]
    public async Task TwoChoices_TextAndToolCalls_MergesIntoOne()
    {
        var responseBody = """
        {
            "choices": [
                {"index":0,"message":{"role":"assistant","content":"I'll do that"},"finish_reason":"tool_calls"},
                {"index":1,"message":{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"test","arguments":"{}"}}]},"finish_reason":"tool_calls"}
            ],
            "model":"test"
        }
        """;
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);

        var merged = choices[0]?["message"];
        Assert.NotNull(merged);
        Assert.Equal("I'll do that", merged["content"]?.GetValue<string>());
        Assert.NotNull(merged["tool_calls"]);
    }

    /// <summary>
    /// When an assistant message has tool_calls with arguments "null",
    /// FixToolCallArguments must replace them with "{}".
    /// </summary>
    [Fact]
    public void FixToolCallArguments_NullString_ReplacedWithEmptyObject()
    {
        var body = """{"messages":[{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"list_goals","arguments":"null"}}]}]}""";
        var result = CopilotChoiceMergingHandler.FixToolCallArguments(body);
        var json = JsonNode.Parse(result);
        var args = json?["messages"]?[0]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>();
        Assert.Equal("{}", args);
    }

    /// <summary>
    /// When arguments is an empty string, it must be replaced with "{}".
    /// </summary>
    [Fact]
    public void FixToolCallArguments_EmptyString_ReplacedWithEmptyObject()
    {
        var body = """{"messages":[{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"list_goals","arguments":""}}]}]}""";
        var result = CopilotChoiceMergingHandler.FixToolCallArguments(body);
        var json = JsonNode.Parse(result);
        var args = json?["messages"]?[0]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>();
        Assert.Equal("{}", args);
    }

    /// <summary>
    /// When arguments is a valid JSON object string, it must pass through unchanged.
    /// </summary>
    [Fact]
    public void FixToolCallArguments_ValidArgs_PassesThrough()
    {
        var body = """{"messages":[{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"get_goal","arguments":"{\"id\":\"my-goal\"}"}}]}]}""";
        var result = CopilotChoiceMergingHandler.FixToolCallArguments(body);
        Assert.Equal(body, result); // No modification
    }

    /// <summary>
    /// Messages without tool_calls are left untouched.
    /// </summary>
    [Fact]
    public void FixToolCallArguments_NoToolCalls_PassesThrough()
    {
        var body = """{"messages":[{"role":"user","content":"hello"}]}""";
        var result = CopilotChoiceMergingHandler.FixToolCallArguments(body);
        Assert.Equal(body, result);
    }

    private static CopilotChoiceMergingHandler CreateHandler(string responseBody) =>
        new(new FakeResponseHandler(responseBody));

    private static HttpRequestMessage CreatePostRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test.com/chat/completions");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Fake inner handler that returns a canned response.</summary>
    private sealed class FakeResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Focused tests for the explicit <c>finish_reason="error"</c> provider failure: a syntactically
/// valid <b>successful</b> Copilot chat-completions response whose choice carries that finish
/// reason is an explicit provider failure, so <see cref="CopilotChoiceMergingHandler"/> raises a
/// controlled transient <see cref="HttpRequestException"/> instead of handing the response to the
/// OpenAI SDK — whose <c>ChatFinishReason</c> parser throws
/// <c>ArgumentOutOfRangeException: Unknown ChatFinishReason value. Actual value was error.</c>
/// and thereby turns a retryable provider failure into an opaque agent-loop error.
/// </summary>
/// <remarks>
/// <para>
/// Every retry scenario drives the REAL production handler chain —
/// <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler, out DelegatingHandler, TimeSpan?)"/>
/// (resilience → Copilot choice merging → reasoning-effort mapping → terminal) — with the same
/// near-zero retry-delay override the repository's other retry tests use, so the genuine resilience
/// pipeline (identical retry count and backoff type to production) is what performs the retries. No
/// network access and no sleeps are involved.
/// </para>
/// <para>
/// <b>Removal-proof.</b> The retry test fails if the detection or the throw is removed (the terminal
/// would succeed on attempt 1 and the caller would observe a 200 with an <c>error</c> finish
/// reason), and the persistent test fails the same way (a 200 instead of the exception). The control
/// tests fail if detection is broadened to a missing, null or non-string <c>finish_reason</c>, to a
/// non-error string, or to an empty <c>choices</c> array.
/// </para>
/// </remarks>
public sealed class CopilotErrorFinishReasonTests
{
    /// <summary>The exact safe message the caller must observe after the retries are exhausted.</summary>
    private const string ExpectedMessage =
        "GitHub Copilot returned an explicit provider error finish reason (finish_reason=\"error\").";

    /// <summary>Near-zero base retry delay, mirroring the repository's retry-test pattern.</summary>
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The error response body. It carries a marker in the content and in an extra field so the
    /// tests can prove that neither the response body nor any provider-supplied text is echoed into
    /// the exception message (and therefore never into logs or user-facing errors).
    /// </summary>
    private const string ErrorBody =
        """
        {
            "id":"chatcmpl-error",
            "model":"claude-sonnet-4.6",
            "provider_note":"INTERNAL-PROVIDER-DETAIL-must-not-leak",
            "choices":[
                {"index":0,"message":{"role":"assistant","content":"SECRET-BODY-TEXT-must-not-leak"},"finish_reason":"error"}
            ]
        }
        """;

    /// <summary>The ordinary success the retry test falls through to.</summary>
    private const string SuccessBody =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":"recovered"},"finish_reason":"stop"}],"model":"test"}""";

    /// <summary>
    /// The production chain retries the transient provider error and the caller observes the
    /// eventual normal response, untouched.
    /// </summary>
    [Fact]
    public async Task ErrorFinishReason_RetriedByRealChain_ThenSucceedsWithNormalResponse()
    {
        var terminal = new ScriptedTerminalHandler(failures: 2, SuccessBody);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, terminal.Attempts);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The successful attempt's body is what the caller sees — not a synthesized stop and not
        // the error body.
        var json = JsonNode.Parse(body);
        var choice = Assert.Single(json!["choices"]!.AsArray());
        Assert.Equal("stop", choice!["finish_reason"]!.GetValue<string>());
        Assert.Equal("recovered", choice["message"]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("INTERNAL-PROVIDER-DETAIL", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A persistent error finish reason exhausts the existing retry policy (1 attempt + 3 retries)
    /// and the caller receives the fixed safe <see cref="HttpRequestException"/> — never an SDK
    /// <c>Unknown ChatFinishReason</c> failure and never a fabricated success.
    /// </summary>
    [Fact]
    public async Task PersistentErrorFinishReason_ExhaustsExistingRetries_WithSafeFailure()
    {
        var terminal = new ScriptedTerminalHandler(failures: int.MaxValue, SuccessBody);
        using var client = CreateProductionClient(terminal);

        using var request = CreateCompletionsRequest();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        // 1 attempt + the resilience policy's 3 retries — the exact existing budget, unchanged.
        Assert.Equal(4, terminal.Attempts);
        Assert.Equal(ExpectedMessage, error.Message);

        // A fixed message: no response body, no provider-supplied text, no inner provider exception.
        Assert.DoesNotContain("SECRET-BODY-TEXT", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("INTERNAL-PROVIDER-DETAIL", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);

        // The safe failure names the signal, never a successful completion.
        Assert.DoesNotContain("stop", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_calls", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The error finish reason is never rewritten: the handler (without a resilience layer above it)
    /// throws, and no body carrying a <c>stop</c> or <c>tool_calls</c> finish reason is produced.
    /// The rejected response's content is released too — it never reaches a caller, so this handler
    /// is its last owner.
    /// </summary>
    [Fact]
    public async Task ErrorFinishReason_IsNotRewrittenToStopOrToolCall()
    {
        var terminal = new ScriptedTerminalHandler(failures: int.MaxValue, SuccessBody);
        using var client = new HttpClient(new CopilotChoiceMergingHandler(terminal));

        using var request = CreateCompletionsRequest();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(ExpectedMessage, error.Message);
        Assert.Equal(1, terminal.Attempts);

        // Nothing was synthesized: the response was rejected, not converted into a completion.
        Assert.DoesNotContain("finish_reason=\"stop\"", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("assistant", error.Message, StringComparison.OrdinalIgnoreCase);

        // The rejected response body was released exactly once by the handler.
        Assert.Equal(1, terminal.ErrorContentDisposals);
    }

    /// <summary>
    /// The signal is detected on ANY choice, case-insensitively — so an error reported alongside a
    /// text/tool_calls split is still a provider failure and is never merged into a tool call.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("ERROR")]
    [InlineData("Error")]
    public async Task ErrorFinishReason_OnAnyChoice_CaseInsensitive_IsAProviderFailure(string spelling)
    {
        var terminal = new SingleShotTerminalHandler(
            """
            {
                "choices": [
                    {"index":0,"message":{"role":"assistant","content":"thinking"},"finish_reason":"stop"},
                    {"index":1,"message":{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"test","arguments":"{}"}}]},"finish_reason":"SPELLING"}
                ],
                "model":"test"
            }
            """.Replace("SPELLING", spelling, StringComparison.Ordinal));

        using var client = new HttpClient(new CopilotChoiceMergingHandler(terminal));
        using var request = CreateCompletionsRequest();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(ExpectedMessage, error.Message);
        Assert.Equal(1, terminal.Attempts);
    }

    /// <summary>
    /// Controls: a missing, JSON-<c>null</c>, non-string or non-error <c>finish_reason</c> is NOT an
    /// error signal. Each body passes through the production chain unchanged, on the first attempt.
    /// </summary>
    [Theory]
    // Missing finish_reason entirely.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}],"model":"test"}""")]
    // JSON null finish_reason.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":null}],"model":"test"}""")]
    // Non-string finish_reason values: number, boolean, object, array.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":7}],"model":"test"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":true}],"model":"test"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":{"value":"error"}}],"model":"test"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":["error"]}],"model":"test"}""")]
    // Ordinary and non-error finish reasons.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"model":"test"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"length"}],"model":"test"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"content_filter"}],"model":"test"}""")]
    // Not a chat-completions shape at all: no choices array, choices is not an array, a null choice element.
    [InlineData("""{"model":"test"}""")]
    [InlineData("""{"choices":"error","model":"test"}""")]
    [InlineData("""{"choices":[null],"model":"test"}""")]
    // A non-object root.
    [InlineData("""["error"]""")]
    public async Task NonErrorFinishReason_PassesThroughUnchanged(string responseBody)
    {
        var terminal = new ScriptedTerminalHandler(failures: 0, responseBody);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Byte-for-byte passthrough: single-choice bodies are never rewritten.
        Assert.Equal(responseBody, body);
    }

    /// <summary>
    /// The empty-<c>choices</c> compatibility behaviour is preserved: it still synthesizes the
    /// minimal stop choice rather than being treated as an error signal.
    /// </summary>
    [Fact]
    public async Task EmptyChoices_StillSynthesizesStop_NotTreatedAsError()
    {
        var terminal = new ScriptedTerminalHandler(
            failures: 0, """{"choices":[],"created":1774365638,"id":"test","model":"claude-opus-4.6"}""");
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var choice = Assert.Single(JsonNode.Parse(body)!["choices"]!.AsArray());
        Assert.Equal("stop", choice!["finish_reason"]!.GetValue<string>());
    }

    /// <summary>
    /// Ordinary multi-choice merging is unchanged by the new check: a text choice plus a tool_calls
    /// choice still merges into one choice carrying both, and is not retried.
    /// </summary>
    [Fact]
    public async Task OrdinaryMultiChoiceMerge_IsUnchanged()
    {
        var terminal = new ScriptedTerminalHandler(
            failures: 0,
            """
            {
                "choices": [
                    {"index":0,"message":{"role":"assistant","content":"I'll do that"},"finish_reason":"stop"},
                    {"index":1,"message":{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"test","arguments":"{}"}}]},"finish_reason":"tool_calls"}
                ],
                "model":"test"
            }
            """);

        using var client = CreateProductionClient(terminal);
        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var merged = Assert.Single(JsonNode.Parse(body)!["choices"]!.AsArray());
        Assert.Equal("I'll do that", merged!["message"]!["content"]!.GetValue<string>());
        Assert.NotNull(merged["message"]!["tool_calls"]);
    }

    /// <summary>
    /// Malformed JSON keeps its existing behaviour: it passes through untouched (the handler never
    /// turns a body it cannot parse into an error finish reason), on the first attempt.
    /// </summary>
    [Fact]
    public async Task MalformedJson_PassesThroughUnchanged()
    {
        const string malformed = "{\"choices\":[{\"finish_reason\":\"error\"";
        var terminal = new ScriptedTerminalHandler(failures: 0, malformed);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);
        Assert.Equal(malformed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Existing non-success HTTP handling is unchanged: a 5xx is still handed back as a response
    /// (retried by the resilience policy) rather than being converted into the error-finish-reason
    /// exception — even when the failure body happens to mention the same finish reason. The error
    /// signal is only meaningful inside a <b>successful</b> response.
    /// </summary>
    [Theory]
    [InlineData("""{"error":"server-side"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    public async Task NonSuccessHttp_IsStillHandledAsBefore(string failureBody)
    {
        var terminal = new HttpStatusTerminalHandler(HttpStatusCode.InternalServerError, failureBody);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // 1 attempt + the resilience policy's 3 retries; the failure body is preserved verbatim.
        Assert.Equal(4, terminal.Attempts);
        Assert.Equal(
            failureBody,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Cancellation is preserved by the handler ITSELF, isolated from both the resilience layer and
    /// <see cref="HttpClient"/>: a caller-cancelled request must surface as
    /// <see cref="OperationCanceledException"/>, never as the error-finish-reason
    /// <see cref="HttpRequestException"/>.
    /// </summary>
    /// <remarks>
    /// <b>Why <see cref="HttpMessageInvoker"/> and not <see cref="HttpClient"/>.</b> An
    /// <see cref="HttpClient"/> races the caller token against the handler's own task and completes
    /// the call with its own <see cref="OperationCanceledException"/> even when the handler wrongly
    /// converted the cancellation into something else — so an <see cref="HttpClient"/>-driven
    /// cancellation test passes even against a handler that swallows cancellation, which is exactly
    /// the mask this test exists to prevent. <see cref="HttpMessageInvoker"/> simply returns the
    /// handler's task, so the exception the handler raises IS what this test observes. The terminal
    /// handler is the handler's direct inner link, making the cancellation the call's terminal
    /// decision point.
    /// </remarks>
    [Fact]
    public async Task Cancellation_ThroughHandlerAlone_IsPreserved()
    {
        var terminal = new BlockingTerminalHandler();
        using var invoker = new HttpMessageInvoker(new CopilotChoiceMergingHandler(terminal));

        using var cts = new CancellationTokenSource();
        using var request = CreateCompletionsRequest();

        var send = invoker.SendAsync(request, cts.Token);

        await terminal.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => send);

        // The handler's own exception, unwrapped: never the provider-failure type.
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(
            error is HttpRequestException,
            "The handler must never convert a cancellation into the provider-failure HttpRequestException.");
    }

    /// <summary>
    /// Cancellation is preserved end-to-end through the REAL production chain as well: the surfaced
    /// exception is an <see cref="OperationCanceledException"/> whose token is really cancelled, not
    /// the error-finish-reason <see cref="HttpRequestException"/>.
    /// </summary>
    [Fact]
    public async Task Cancellation_ThroughProductionChain_IsPreserved()
    {
        var terminal = new BlockingTerminalHandler();
        using var client = CreateProductionClient(terminal);

        using var cts = new CancellationTokenSource();
        using var request = CreateCompletionsRequest();

        var send = client.SendAsync(request, cts.Token);

        // Wait until the request is really in flight beneath the production chain, then cancel.
        await terminal.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => send);
        var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(
            error is HttpRequestException,
            $"Cancellation must not surface as {nameof(HttpRequestException)}.");

        // The cancellation really was a cancellation: the token behind the surfaced exception is
        // cancelled (the resilience layer may hand the inner handler a linked token, so identity is
        // not asserted — the cancellation state is).
        Assert.True(
            cancellation.CancellationToken.IsCancellationRequested,
            "The surfaced OperationCanceledException must carry an actually-cancelled token.");
    }

    /// <summary>
    /// The real production chat-completions SDK client: the SAME
    /// <see cref="OpenAIClient"/> → <c>GetChatClient(model).AsIChatClient()</c> adapter and the SAME
    /// full production handler chain that <see cref="ChatClientFactory"/> hands to callers, over an
    /// injectable terminal transport. This is the layer where the original incident's
    /// <c>ArgumentOutOfRangeException: Unknown ChatFinishReason value … error</c> was thrown after
    /// the SDK parsed the response, so it is the layer that must now surface the controlled
    /// provider failure instead. Token/endpoint-discovery wiring is deliberately not involved: a
    /// fixed safe dummy credential is used and no discovery handler is inserted.
    /// </summary>
    /// <remarks>
    /// The SDK's OWN outer <c>ClientRetryPolicy</c> is disabled here (test-side SDK option, not a
    /// change to the production handler chain): that policy wraps and re-retries <em>every</em>
    /// transport failure — a pre-existing behaviour unrelated to this goal — and would otherwise
    /// multiply the 4 resilience attempts by its own 4 and add randomized multi-second delays. The
    /// production <see cref="ChatClientFactory.CreateCopilotClientForTest(bool, string, HttpMessageHandler, out DelegatingHandler, TimeSpan?)"/>
    /// seam builds the exact same chain as <see cref="ChatClientFactory.CreateCopilotClientForTestFull"/>
    /// (resilience → choice merging → reasoning-effort mapping → terminal), with only the near-zero
    /// base delay overridden, so the 1 + 3 retries asserted here are the real ResilienceHandler's.
    /// </remarks>
    private static IChatClient CreateProductionChatClient(HttpMessageHandler terminal)
    {
        var httpClient = ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false,
            ChatClientFactory.CopilotExtraHighMapping,
            terminal,
            out _,
            FastRetry);

        try
        {
            // The production ownership wrapper takes care of disposing both the SDK adapter and
            // the owned HttpClient/chain when the test finishes.
            return new ChatClientFactory.OwnedCopilotChatClient(
                new OpenAIClient(
                    new ApiKeyCredential("test-only-non-secret-credential"),
                    new OpenAIClientOptions
                    {
                        Endpoint = ChatClientFactory.DefaultCopilotApiEndpoint,
                        Transport = new HttpClientPipelineTransport(httpClient),
                        NetworkTimeout = TimeSpan.FromMinutes(30),
                        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                    })
                    .GetChatClient("claude-sonnet-4.6")
                    .AsIChatClient(),
                httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the REAL production chat-completions chain over <paramref name="terminal"/> with the
    /// near-zero retry delay, so the actual resilience policy (3 retries) performs the retries.
    /// </summary>
    private static HttpClient CreateProductionClient(HttpMessageHandler terminal) =>
        ChatClientFactory.CreateCopilotClientForTest(
            useResponsesApi: false,
            ChatClientFactory.CopilotExtraHighMapping,
            terminal,
            out _,
            FastRetry);

    private static HttpRequestMessage CreateCompletionsRequest() =>
        new(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"claude-sonnet-4.6","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

    /// <summary>
    /// The exact production failure path: a persistent explicit <c>finish_reason="error"</c> response
    /// driven through <see cref="IChatClient.GetResponseAsync"/> (which is what CopilotHive's
    /// DistributedBrain calls) must surface the fixed safe <see cref="HttpRequestException"/> after
    /// the standard 4 attempts — NEVER the OpenAI SDK's
    /// <c>ArgumentOutOfRangeException("Unknown ChatFinishReason value … error")</c>, and never a
    /// fabricated successful completion. This test deliberately fails against the pre-fix handler:
    /// without the interception the SDK parser throws on attempt 1.
    /// </summary>
    [Fact]
    public async Task PersistentErrorFinishReason_ThroughSdkChatClient_IsASafeProviderFailure()
    {
        var terminal = new ScriptedTerminalHandler(failures: int.MaxValue, SuccessBody);
        using var chatClient = CreateProductionChatClient(terminal);

        // The OpenAI SDK's transport layer wraps an HttpClient exception in a
        // ClientResultException carrying the provider failure message; that wrapper is SDK behaviour
        // applied to ALL transport failures, not something this handler fabricates. What matters is
        // that the precise pre-fix SDK parser failure is gone and the safe fixed message survives.
        var error = await Assert.ThrowsAsync<ClientResultException>(() => chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ExpectedMessage, error.Message);
        var transportFailure = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(ExpectedMessage, transportFailure.Message);
        Assert.Null(transportFailure.InnerException);
        Assert.Equal(4, terminal.Attempts);
        Assert.DoesNotContain("Unknown ChatFinishReason", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentOutOfRangeException", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-BODY-TEXT", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("INTERNAL-PROVIDER-DETAIL", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A transient explicit error finish reason is retried by the real resilience chain and the
    /// SDK returns the eventual ordinary <c>stop</c> completion with its real text — no synthesized
    /// stop, no empty content and no tool call. This is the SDK-level positive control for the
    /// persistent-failure row: it proves the production success path remains viable after the
    /// interception is added.
    /// </summary>
    [Fact]
    public async Task TransientErrorFinishReason_ThroughSdkChatClient_RetriesThenReturnsRealCompletion()
    {
        var terminal = new ScriptedTerminalHandler(failures: 2, SuccessBody);
        using var chatClient = CreateProductionChatClient(terminal);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, terminal.Attempts);
        Assert.Equal("recovered", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.DoesNotContain(
            response.Messages.SelectMany(m => m.Contents),
            c => c is FunctionCallContent);
    }

    /// <summary>
    /// The request-side tool-call fix-up is still applied on the REAL production chain: a
    /// parameterless tool call whose arguments arrive as the string <c>"null"</c> must reach the
    /// terminal as <c>"{}"</c>, while the ordinary completion passes through unchanged. This pins
    /// the request path that the new response-side scan must not have moved or skipped.
    /// </summary>
    [Fact]
    public async Task FixToolCallArguments_ThroughProductionChain_ReachesTerminalAndKeepsResponse()
    {
        const string completion =
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"model":"test"}""";
        var terminal = new RecordingRequestTerminalHandler(completion);
        using var client = CreateProductionClient(terminal);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.githubcopilot.com/chat/completions")
        {
            Content = new StringContent(
                """{"model":"test","messages":[{"role":"assistant","tool_calls":[{"id":"t1","type":"function","function":{"name":"f","arguments":"null"}}]}]}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);
        Assert.Equal(completion, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Read the body the TERMINAL actually received, not the request object the test created:
        // the handler replaces request.Content with the rewritten StringContent.
        var sent = JsonNode.Parse(terminal.LastRequestBody!)!;
        var arguments = sent["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.Equal("{}", arguments);
        Assert.DoesNotContain("\"null\"", terminal.LastRequestBody, StringComparison.Ordinal);
    }

    // ── Tolerant scan: duplicate properties and undecodable strings ─────────────────────────────

    /// <summary>
    /// Direct structural rows for the tolerant scanner itself. Each row pins one contract of the
    /// forward-only <c>Utf8JsonReader</c> walk: it must never throw, must visit ALL duplicate
    /// properties (including a duplicate root <c>choices</c>) and ALL choices, must ignore
    /// undecodable strings only for the token they appear on, must resolve escapes before
    /// comparison, and must reject trailing/truncated content even after seeing an error signal.
    /// The rows assert the exact expected boolean, so both a false positive and a false negative
    /// are detected on the precise shape that produces them.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("""{"choices":[{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"ERROR"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"\u0065rror"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"error"}]} trailing-garbage""", false)]
    [InlineData("""{"choices":[{"finish_reason":"error"}""", false)]
    [InlineData("""{"choices":[{"finish_reason":"\ud800"},{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"model":"a","model":"b","choices":[{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[{"index":0,"index":1,"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"stop"}],"choices":[{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"stop"},{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[null,{"finish_reason":"error"}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"error"}],"extra":{"nested":[1,2,{"x":"y"}]}}""", true)]
    [InlineData("""{"choices":[]}""", false)]
    [InlineData("""{"choices":"error"}""", false)]
    [InlineData("""["error"]""", false)]
    [InlineData("""{"choices":[{"finish_reason":null},{"finish_reason":7},{"finish_reason":true},{"finish_reason":{}},{"finish_reason":[]}]}""", false)]
    public void HasProviderErrorFinishReason_TolerantScan_ReturnsExactExpectedResult(string? body, bool expected)
    {
        Assert.Equal(expected, CopilotChoiceMergingHandler.HasProviderErrorFinishReason(body));
    }

    /// <summary>
    /// A normal <c>stop</c> completion carrying a DUPLICATE property is usable by the OpenAI SDK
    /// (it reads the document with <c>JsonDocument</c>, which tolerates duplicates). It must pass
    /// through the production chain untouched on the first attempt and parse through the SDK
    /// <see cref="IChatClient"/> without throwing — the finish-reason scan must never turn a
    /// previously usable completion into an uncontrolled failure.
    /// </summary>
    [Fact]
    public async Task DuplicatePropertyInStopCompletion_PassesThroughAndParsesThroughSdk()
    {
        const string duplicateIndexStop =
            """{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"model":"test"}""";

        // HTTP level: byte-for-byte passthrough, one attempt.
        var httpTerminal = new SingleShotTerminalHandler(duplicateIndexStop);
        using (var client = CreateProductionClient(httpTerminal))
        using (var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, httpTerminal.Attempts);
            Assert.Equal(
                duplicateIndexStop,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // SDK level: the same body parses to the real completion.
        var sdkTerminal = new SingleShotTerminalHandler(duplicateIndexStop);
        using var chatClient = CreateProductionChatClient(sdkTerminal);

        var chatResponse = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, sdkTerminal.Attempts);
        Assert.Equal("hi", chatResponse.Text);
        Assert.Equal(ChatFinishReason.Stop, chatResponse.FinishReason);
    }

    /// <summary>
    /// Bodies that are NOT error responses but exercise the shapes a lazy <c>JsonNode</c> walk
    /// throws on (duplicate properties, unpaired Unicode escapes): each is handed back
    /// byte-for-byte, on the first attempt — the merge block's existing "return the original
    /// response unchanged" fallback is what keeps the multi-choice rows untouched.
    /// </summary>
    [Theory]
    // Duplicate property inside the only choice's object (index).
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"model":"test"}""")]
    // Duplicate root property.
    [InlineData("""{"model":"a","model":"b","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""")]
    // Duplicate property in a two-choice (merge-eligible) body: the merge fails and falls back.
    [InlineData("""{"choices":[{"index":0,"index":9,"message":{"role":"assistant","content":"a"},"finish_reason":"stop"},{"index":1,"message":{"role":"assistant","tool_calls":[{"id":"t","type":"function","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")]
    // Unpaired surrogate escape in an ordinary content string.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"\ud800"},"finish_reason":"stop"}],"model":"test"}""")]
    // Unpaired escape in the finish_reason value itself (undecodable, so it cannot be "error").
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"\ud800"}]}""")]
    // Unpaired escape in a two-choice (merge-eligible) body.
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"\udc00"},"finish_reason":"stop"},{"index":1,"message":{"role":"assistant","tool_calls":[{"id":"t","type":"function","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")]
    // An error spelling that is only syntactically valid up to a trailing syntax error: malformed
    // JSON is never interpreted, so it keeps its passthrough.
    [InlineData("""{"choices":[{"finish_reason":"error"}]} trailing-garbage""")]
    public async Task DuplicateOrUndecodableNonErrorBody_PassesThroughUnchanged(string responseBody)
    {
        var terminal = new SingleShotTerminalHandler(responseBody);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Attempts);
        Assert.Equal(
            responseBody,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An explicit <c>finish_reason="error"</c> is still a controlled provider failure when the
    /// document ALSO contains duplicate properties or undecodable strings — on the same choice, on
    /// an earlier choice's other fields, or on a later choice. The scan neither throws an
    /// uncontrolled exception nor stops looking, and the real resilience chain performs its
    /// standard 1 + 3 attempts.
    /// </summary>
    [Theory]
    // Duplicate property on the same choice as the error.
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    // Duplicate property on an earlier (stop) choice, error on a later choice.
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"x"},"finish_reason":"stop"},{"index":1,"message":{"role":"assistant","content":"y"},"finish_reason":"error"}]}""")]
    // Duplicate root property.
    [InlineData("""{"model":"a","model":"b","choices":[{"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    // Duplicate finish_reason itself: ambiguous documents fail closed whichever duplicate is last.
    [InlineData("""{"choices":[{"finish_reason":"stop","finish_reason":"error"}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"error","finish_reason":"stop"}]}""")]
    // Error on an EARLIER choice, an ordinary stop later: still an error response.
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":"x"},"finish_reason":"error"},{"message":{"role":"assistant","content":"y"},"finish_reason":"stop"}]}""")]
    // Unpaired escape in an earlier property of the SAME choice.
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":"\ud800"},"finish_reason":"error"}]}""")]
    // Unpaired escape in an earlier choice, error on a later choice.
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":"\ud800"},"finish_reason":"stop"},{"message":{"role":"assistant","content":"y"},"finish_reason":"error"}]}""")]
    // Undecodable finish_reason on an earlier choice, error on a later one.
    [InlineData("""{"choices":[{"finish_reason":"\ud800"},{"finish_reason":"error"}]}""")]
    // Undecodable PROPERTY NAME earlier in the same choice.
    [InlineData("""{"choices":[{"\ud800x":1,"finish_reason":"error"}]}""")]
    // Undecodable string in an earlier ROOT property.
    [InlineData("""{"note":"\udc00","choices":[{"finish_reason":"error"}]}""")]
    // Duplicate AND undecodable together.
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"content":"\ud800"},"finish_reason":"error"}]}""")]
    // The value is compared after escape resolution: "\u0065rror" is "error".
    [InlineData("""{"choices":[{"finish_reason":"\u0065rror"}]}""")]
    public async Task ErrorFinishReason_AmongDuplicateOrUndecodableContent_IsAProviderFailure(string responseBody)
    {
        var terminal = new SingleShotTerminalHandler(responseBody);
        using var client = CreateProductionClient(terminal);
        using var request = CreateCompletionsRequest();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(ExpectedMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(4, terminal.Attempts);
    }

    /// <summary>
    /// The same tolerant-scan shapes at the layer that actually broke in production: the REAL
    /// OpenAI SDK <see cref="IChatClient"/> over the real handler chain. A duplicate property or an
    /// undecodable escape before the error must still fail closed with the controlled provider
    /// failure, never an uncontrolled <see cref="ArgumentException"/>, never
    /// <c>Unknown ChatFinishReason</c>, and never a fabricated completion. The SDK's outer retry is
    /// disabled test-side so the 4 attempts here are exactly the ResilienceHandler's.
    /// </summary>
    [Theory]
    // Duplicate property on the same choice as the error.
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    // Duplicate property on an earlier choice; the error is on a later choice.
    [InlineData("""{"choices":[{"index":0,"index":1,"finish_reason":"stop"},{"finish_reason":"error"}]}""")]
    // Duplicate root property before choices.
    [InlineData("""{"model":"a","model":"b","choices":[{"finish_reason":"error"}]}""")]
    // Unpaired escape in an earlier property of the same choice.
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":"\ud800"},"finish_reason":"error"}]}""")]
    // Unpaired escape in an earlier choice; the error is on a later choice.
    [InlineData("""{"choices":[{"content":"\ud800","finish_reason":"stop"},{"finish_reason":"error"}]}""")]
    // Escaped spelling of "error".
    [InlineData("""{"choices":[{"finish_reason":"\u0065rror"}]}""")]
    public async Task DuplicateOrUndecodableErrorBody_ThroughSdkChatClient_FailsSafely(string responseBody)
    {
        var terminal = new SingleShotTerminalHandler(responseBody);
        using var chatClient = CreateProductionChatClient(terminal);

        // The SDK wraps the handler's HttpRequestException in ClientResultException; the safe
        // message must survive verbatim and no SDK enum-parser failure may appear anywhere.
        var error = await Assert.ThrowsAsync<ClientResultException>(() => chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken));

        var transportFailure = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(ExpectedMessage, transportFailure.Message);
        Assert.Equal(4, terminal.Attempts);
        Assert.DoesNotContain("Unknown ChatFinishReason", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentOutOfRangeException", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary-completion counterpart for the SDK-level duplicate rows: an irrelevant
    /// duplicate on an earlier choice or root property, and an unpaired escape in an UNKNOWN root
    /// property that the SDK never decodes, still parse through the real SDK to a genuine
    /// <c>stop</c> completion with its real text. (An unpaired escape inside the message CONTENT is
    /// NOT a viable SDK success control: the SDK's own <c>JsonDocument.GetString</c> throws there.
    /// Such bodies are covered by the HTTP passthrough rows instead.) This proves the tolerant scan
    /// did not silently turn usable completions into failures at the SDK layer.
    /// </summary>
    [Theory]
    [InlineData("""{"choices":[{"index":0,"index":1,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"model":"test"}""")]
    [InlineData("""{"model":"a","model":"b","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""")]
    [InlineData("""{"note":"\ud800","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""")]
    public async Task DuplicateOrUndecodableStopBody_ThroughSdkChatClient_ParsesNormally(string responseBody)
    {
        var terminal = new SingleShotTerminalHandler(responseBody);
        using var chatClient = CreateProductionChatClient(terminal);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, terminal.Attempts);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    // ── Declared SSE responses are never buffered ───────────────────────────────────────────────

    /// <summary>
    /// A SUCCESSFUL response declared <c>text/event-stream</c> (any casing, with or without
    /// parameters) is returned with its ORIGINAL content instance and media type and the handler
    /// performs ZERO reads of it — including when the body is JSON-shaped and even when it carries
    /// <c>finish_reason="error"</c>, which this handler never agreed to interpret on a stream.
    /// Driven under <see cref="HttpCompletionOption.ResponseHeadersRead"/> like a streaming caller.
    /// </summary>
    [Theory]
    [InlineData("text/event-stream", "data: {}\n\n")]
    [InlineData("TEXT/EVENT-STREAM", "data: {}\n\n")]
    [InlineData("Text/Event-Stream; charset=utf-8", "data: {}\n\n")]
    // JSON-shaped body declared as SSE: would be rewritten (empty choices) or rejected (error).
    [InlineData("text/event-stream", """{"choices":[]}""")]
    [InlineData("TEXT/EVENT-STREAM", """{"choices":[{"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    public async Task SuccessfulEventStream_IsReturnedWithZeroContentReads(string mediaType, string body)
    {
        // The handler alone: the direct inner link is the tracking terminal.
        var directTerminal = new EventStreamTerminalHandler(mediaType, body);
        using (var client = new HttpClient(new CopilotChoiceMergingHandler(directTerminal)))
        using (var response = await client.SendAsync(
            CreateCompletionsRequest(), HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken))
        {
            AssertStreamUntouched(directTerminal, response, mediaType, body);
        }

        // The full production chain (resilience → merging → mapping → terminal).
        var chainTerminal = new EventStreamTerminalHandler(mediaType, body);
        using (var client = CreateProductionClient(chainTerminal))
        using (var response = await client.SendAsync(
            CreateCompletionsRequest(), HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, chainTerminal.Attempts);
            AssertStreamUntouched(chainTerminal, response, mediaType, body);
        }
    }

    private static void AssertStreamUntouched(
        EventStreamTerminalHandler terminal, HttpResponseMessage response, string mediaType, string body)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Nothing read the content on the way out, and it is the very same instance.
        Assert.Equal(0, terminal.Content.ReadCount);
        Assert.Same(terminal.Content, response.Content);
        // The declared media type is untouched, casing included (the media type proper, and the
        // complete header value with any parameters).
        Assert.Equal(mediaType.Split(';')[0], response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(mediaType, response.Content.Headers.ContentType.ToString());
        Assert.False(terminal.Content.Disposed, "The handler must not dispose a stream it passes through.");

        // And it is still fully readable by the real consumer afterwards.
        Assert.Equal(body, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Assert.Equal(1, terminal.Content.ReadCount);
    }

    /// <summary>
    /// A declared SSE stream that NEVER ENDS is the real reason for a zero-read, headers-only
    /// bypass: under <see cref="HttpCompletionOption.ResponseHeadersRead"/> the caller must receive
    /// the response while the body is still in flight, and the ORIGINAL stream instance must be
    /// returned. Any buffering attempt in the handler would hang here. The rendezvous and cleanup
    /// are driven by the SHARED <see cref="RunHeldSseScenarioAsync"/> harness, whose cleanup
    /// contract is exercised adversarially by the failure-path tests below.
    /// </summary>
    [Fact]
    public async Task SuccessfulEventStream_WithNonTerminatingBody_DoesNotBufferBeforeReturningHeaders()
    {
        const string mediaType = "text/event-stream";
        var terminal = new HeldEventStreamTerminalHandler(mediaType);
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            await RunHeldSseScenarioAsync(
                chainTerminal: terminal,
                heldTerminal: terminal,
                scenarioCts: cts,
                transportToken: TestContext.Current.CancellationToken,
                report: report,
                scenario: async ctx =>
                {
                    // Park the terminal INSIDE the real production chain while the body is held. Without
                    // this gate a correctly behaving handler returns before the assertion below can
                    // observe the in-flight state, which would make the rendezvous vacuous.
                    await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), ctx.Token);
                    Assert.False(ctx.Send.IsCompleted,
                        "The terminal is parked; the call must not have completed yet.");
                    ctx.Terminal.AllowReturn();

                    ctx.Response = await ctx.Send.WaitAsync(TimeSpan.FromSeconds(30), ctx.Token);

                    Assert.Equal(HttpStatusCode.OK, ctx.Response.StatusCode);
                    Assert.Equal(1, ctx.Terminal.Attempts);
                    Assert.Equal(0, ctx.Terminal.Content.GetReadCount());
                    Assert.Same(ctx.Terminal.Content, ctx.Response.Content);
                    Assert.Equal(mediaType, ctx.Response.Content.Headers.ContentType?.MediaType);
                    Assert.False(ctx.Terminal.Content.Disposed);

                    // ReadAsStreamAsync is the real SSE consumer path (ReadAsStringAsync first buffers
                    // via SerializeToStreamAsync). The read stays pending until the harness releases it.
                    ctx.StreamRead = ctx.Response.Content.ReadAsStreamAsync(ctx.Token);
                    var stream = await ctx.StreamRead.WaitAsync(TimeSpan.FromSeconds(30), ctx.Token);
                    ctx.CapturedStream = stream;
                    ctx.PendingByte = stream.ReadAsync(new byte[1], 0, 1, ctx.Token);
                    await ctx.Terminal.Content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ctx.Token);
                    Assert.False(ctx.PendingByte.IsCompleted);
                });

            // Healthy control: every cleanup step ran, nothing faulted, and every resource was released.
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.Empty(report.JoinExpiries);
            Assert.True(report.GatesReleased && report.SendJoined && report.StreamDrained
                && report.ReadJoined && report.ResponseDisposed);
            Assert.True(report.CapturedStreamDisposed,
                "The captured read stream must be disposed by the cleanup step itself.");
            Assert.True(terminal.Content.IsReleased);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.Equal(1, terminal.Content.DisposedStreamCount);
        });
    }

    /// <summary>
    /// FAILURE PATH 1. A fault injected BEFORE the held-SSE terminal is reached (so its
    /// <c>Content</c> was NEVER assigned) must surface as the ORIGINAL exception instance, never
    /// masked by a cleanup <see cref="NullReferenceException"/>. The null-safe
    /// <c>ReleaseHeldContent()</c> must be a no-op on this path and all cleanup steps must still run.
    /// This row is what fails if cleanup dereferences <c>Content</c> unguarded.
    /// </summary>
    [Fact]
    public async Task PreTerminalFault_SurfacesOriginalFault_WithoutCleanupNullReference()
    {
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream");
        // An HttpRequestException is a transient transport fault the real ResilienceHandler
        // retries, so this row also proves the fault occurred beneath the real chain (4 attempts).
        var primary = new HttpRequestException("PRE-TERMINAL-FAULT-before-content-exists");
        var faultHandler = new PreTerminalFaultHandler(terminal, primary);
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var caught = await Assert.ThrowsAsync<HttpRequestException>(() =>
                RunHeldSseScenarioAsync(faultHandler, terminal, cts, CancellationToken.None, report,
                    async ctx => await ctx.Send.WaitAsync(TimeSpan.FromSeconds(30), ctx.Token)));

            Assert.Same(primary, caught);
            Assert.Equal(0, terminal.Attempts);
            Assert.Equal(4, faultHandler.Attempts); // real ResilienceHandler 1 + 3 retries
            // Content really was never assigned: the accessor is designed to say so explicitly.
            var unassigned = Assert.Throws<InvalidOperationException>(() => _ = terminal.Content);
            Assert.Contains("never reached the terminal", unassigned.Message);

            Assert.True(report.AllStepsRan);
            Assert.False(report.GatesStepFaulted,
                "Null-safe gate release must not fault when the terminal was never reached.");
            Assert.False(report.StreamStepFaulted);
            Assert.False(report.ReadStepFaulted);
            Assert.False(report.ResponseStepFaulted);
            Assert.True(report.GatesReleased);
            Assert.False(report.LateResponseDisposed);
        });
    }

    /// <summary>
    /// FAILURE PATH 2a — CANCELLATION BEFORE RESPONSE TRANSFER. Transport cancellation wins while
    /// the terminal has ALLOCATED its response but has not yet handed it over (it is parked at its
    /// return gate). Until the hand-off succeeds the TERMINAL owns that response: nothing above it
    /// ever saw it, so nothing else can release it. The original cancellation must surface, and the
    /// content must be disposed EXACTLY ONCE — observed here, inside the probe, BEFORE the outer
    /// safety net runs, so a leak cannot be hidden by the fallback cleanup. The harness never
    /// captured a response (<c>ctx.Response</c> stays null), so it cannot have disposed it either.
    /// </summary>
    [Fact]
    public async Task CancellationBeforeResponseTransfer_TerminalDisposesTheResponseItOwns()
    {
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream");
        using var scenarioCts = new CancellationTokenSource();
        using var transportCts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, scenarioCts, transportCts.Token, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

                        // PROOF OF THE PRECONDITION, taken BEFORE the cancellation: the response is
                        // allocated, nothing was transferred, and nothing has disposed it yet.
                        Assert.True(terminal.ResponseAllocated);
                        Assert.False(terminal.ResponseTransferred);
                        Assert.Equal(0, terminal.HandoffCount);
                        Assert.False(ctx.Send.IsCompleted);
                        Assert.Equal(0, terminal.Content.DisposeCount);
                        Assert.False(transportCts.IsCancellationRequested);

                        transportCts.Cancel();

                        // The ORIGINAL cancellation surfaces from the send, unmodified by cleanup.
                        await ctx.Send;
                    }));

            // The original cancellation surfaced — not an HttpRequestException and not a cleanup fault.
            Assert.IsNotType<HttpRequestException>(caught);
            Assert.True(transportCts.IsCancellationRequested);
            var observed = Assert.IsAssignableFrom<OperationCanceledException>(terminal.ObservedFault);
            Assert.True(observed.CancellationToken.IsCancellationRequested);
            // HttpClient may normalize cancellation to the original caller token. It must
            // retain the terminal exception, directly or as the cause of that normalization.
            Assert.Equal(transportCts.Token, caught.CancellationToken);
            Assert.True(ReferenceEquals(caught, observed) || ReferenceEquals(caught.InnerException, observed));

            // Still allocated, never transferred, never retried.
            Assert.Equal(1, terminal.Attempts);
            Assert.True(terminal.ResponseAllocated);
            Assert.False(terminal.ResponseTransferred);
            Assert.Equal(0, terminal.HandoffCount);

            // EXACTLY-ONCE DISPOSAL, observed BEFORE the safety net: the harness's own cleanup never
            // saw a response to dispose, so this disposal can only have come from the terminal.
            Assert.Null(report.Ctx!.Response);
            Assert.False(report.LateResponseDisposed);
            Assert.Equal(1, terminal.Content.DisposeCount);

            Assert.True(report.AllStepsRan);
            Assert.Empty(report.JoinExpiries);
        });
    }

    /// <summary>
    /// FAILURE PATH 2b — RELEASE BEFORE PUBLICATION. Cleanup step 1 runs while the terminal has not
    /// yet created its content. A release requested at that moment must PERSIST and be applied at
    /// publication, so the body is ALREADY released when the response reaches the handler above the
    /// terminal — not merely at teardown, when disposing the late response would release it anyway.
    /// </summary>
    [Fact]
    public async Task ReleaseRequestedBeforePublication_PersistsToPublicationAndHandoff()
    {
        var gates = new HeldGates(holdPublication: true);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        var observer = new ResponseObserverHandler(terminal);
        var primary = new InvalidOperationException("PRIMARY-BEFORE-PUBLICATION");
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            // Independent rendezvous: the publication gate opens only AFTER cleanup is observed
            // waiting on the send, i.e. strictly after step 1 asked for the release.
            var releaser = report.Releaser = Task.Run(() => OpenGateAfterCleanupWaitsAsync(report, CleanupWait.Send, gates.Publication));

            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunHeldSseScenarioAsync(observer, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await terminal.PublicationReached.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        Assert.Null(terminal.ContentOrNull); // nothing published yet
                        Assert.False(ctx.Send.IsCompleted);
                        throw primary;
                    }));
            await releaser;

            Assert.Same(primary, caught);

            // Cleanup asked for the release BEFORE any content existed...
            Assert.False(report.PublishedAtGateRelease);
            Assert.True(report.ReleaseRequestedAtGateRelease);
            Assert.True(report.GatesReleased);
            var wait = await report.WaitReached(CleanupWait.Send);
            Assert.True(wait.OperationIncomplete, "The send must still be parked at publication.");
            Assert.Equal(
                new[] { "wait-entered:Send", "gate-opened:Send", "join-completed:Send" },
                report.EventsFor(CleanupWait.Send));

            // ...and the body published LATER was already released when the response left the
            // terminal and when the first handler above it received it — not just at teardown.
            Assert.NotNull(terminal.ContentOrNull);
            Assert.Equal(1, terminal.HandoffCount);
            Assert.True(terminal.ReleasedAtHandoff);
            Assert.True(observer.ContentReleasedWhenSeen,
                "The handler above the terminal must observe an ALREADY-released body.");

            // The late response was joined and disposed exactly once by cleanup, after that.
            Assert.True(report.SendJoined);
            Assert.True(report.LateResponseDisposed);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.Empty(report.JoinExpiries);
        });
    }

    /// <summary>
    /// FAILURE PATH 2c-i — SEND JOIN IS CANCELLATION-INDEPENDENT. The scenario token is ALREADY
    /// cancelled and the send is genuinely still INCOMPLETE when cleanup reaches its send join (the
    /// terminal is held at a gate that cleanup step 1 does not open). The join must keep waiting:
    /// an independent releaser opens that gate only AFTER observing the wait, and the join then
    /// completes and disposes the late response. A join bound to the cancelled scenario token would
    /// throw at once, never join, and leave the late response undisposed.
    /// </summary>
    [Fact]
    public async Task CancelledScenarioToken_SendJoin_StillWaitsForIncompleteSend_ThenDisposesLateResponse()
    {
        var gates = new HeldGates(holdSend: true);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var releaser = report.Releaser = Task.Run(() => OpenGateAfterCleanupWaitsAsync(report, CleanupWait.Send, gates.SendReturn));

            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        cts.Cancel();
                        ctx.Token.ThrowIfCancellationRequested();
                    }));
            await releaser;

            Assert.True(cts.IsCancellationRequested);
            Assert.IsAssignableFrom<OperationCanceledException>(caught);

            var wait = await report.WaitReached(CleanupWait.Send);
            Assert.True(wait.ScenarioTokenCancelled, "The scenario token must already be cancelled at the wait.");
            Assert.True(wait.OperationIncomplete, "The send must still be incomplete at the wait.");

            // ORDER: wait reached -> independent release -> join completes. Only a join that really
            // waited on the (uncancelled) token can complete after the release.
            Assert.Equal(
                new[] { "wait-entered:Send", "gate-opened:Send", "join-completed:Send" },
                report.EventsFor(CleanupWait.Send));

            Assert.True(report.SendJoined);
            Assert.True(report.LateResponseDisposed);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.Equal(1, terminal.HandoffCount);
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.Empty(report.JoinExpiries);
        });
    }

    /// <summary>
    /// FAILURE PATH 2c-ii — STREAM-CREATION JOIN IS CANCELLATION-INDEPENDENT. As above, for the
    /// stream-creation task: creation is held inside the content (a gate cleanup does not open),
    /// the scenario token is already cancelled, and the join must wait for the independent release,
    /// then dispose the late-created stream EXPLICITLY, before the response is disposed.
    /// </summary>
    [Fact]
    public async Task CancelledScenarioToken_StreamCreationJoin_StillWaitsForIncompleteCreation_ThenDisposesStream()
    {
        var gates = new HeldGates(holdStreamCreation: true);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var releaser = report.Releaser = Task.Run(() =>
                OpenGateAfterCleanupWaitsAsync(report, CleanupWait.StreamCreation, gates.StreamCreation));

            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.Terminal.AllowReturn();
                        ctx.Response = await ctx.Send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.StreamRead = ctx.Response.Content.ReadAsStreamAsync(CancellationToken.None);
                        Assert.False(ctx.StreamRead.IsCompleted, "Stream creation must be held at its own gate.");
                        Assert.Equal(1, ctx.Terminal.Content.GetReadCount());
                        cts.Cancel();
                        ctx.Token.ThrowIfCancellationRequested();
                    }));
            await releaser;

            Assert.IsAssignableFrom<OperationCanceledException>(caught);

            var wait = await report.WaitReached(CleanupWait.StreamCreation);
            Assert.True(wait.ScenarioTokenCancelled, "The scenario token must already be cancelled at the wait.");
            Assert.True(wait.OperationIncomplete, "Stream creation must still be incomplete at the wait.");
            Assert.Equal(
                new[] { "wait-entered:StreamCreation", "gate-opened:StreamCreation", "join-completed:StreamCreation" },
                report.EventsFor(CleanupWait.StreamCreation));

            // The join's real effects: the late stream was drained and disposed EXPLICITLY by step 3,
            // while the response was still undisposed (explicit stream disposal precedes response
            // disposal), and the response was then disposed exactly once.
            Assert.True(report.StreamDrained);
            Assert.True(report.StreamDisposedByCleanup);
            Assert.False(report.ContentDisposedAtStreamDisposal);
            Assert.Equal(1, terminal.Content.DisposedStreamCount);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.Empty(report.JoinExpiries);
        });
    }

    /// <summary>
    /// FAILURE PATH 2c-iii — PENDING-READ JOIN IS CANCELLATION-INDEPENDENT. The body-release gate
    /// cleanup step 1 opens is NOT enough to finish the read (a second, independent gate holds it),
    /// so the read is genuinely still pending — with the scenario token already cancelled — when
    /// cleanup reaches its read join. The independent release happens only after that wait is
    /// observed, and the join must then really complete the read.
    /// </summary>
    [Fact]
    public async Task CancelledScenarioToken_PendingReadJoin_StillWaitsForIncompleteRead_ThenJoins()
    {
        var gates = new HeldGates(holdRead: true);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var releaser = report.Releaser = Task.Run(() =>
                OpenGateAfterCleanupWaitsAsync(report, CleanupWait.PendingRead, gates.ReadFinish));

            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.Terminal.AllowReturn();
                        ctx.Response = await ctx.Send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.StreamRead = ctx.Response.Content.ReadAsStreamAsync(CancellationToken.None);
                        var stream = await ctx.StreamRead.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.CapturedStream = stream;
                        ctx.PendingByte = stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None);
                        await ctx.Terminal.Content.ReadStarted.Task.WaitAsync(
                            TimeSpan.FromSeconds(30), CancellationToken.None);
                        Assert.False(ctx.PendingByte.IsCompleted);
                        cts.Cancel();
                        ctx.Token.ThrowIfCancellationRequested();
                    }));
            await releaser;

            Assert.IsAssignableFrom<OperationCanceledException>(caught);

            var wait = await report.WaitReached(CleanupWait.PendingRead);
            Assert.True(wait.ScenarioTokenCancelled, "The scenario token must already be cancelled at the wait.");
            Assert.True(wait.OperationIncomplete,
                "The read must still be pending although step 1 already opened the body-release gate.");
            Assert.Equal(
                new[] { "wait-entered:PendingRead", "gate-opened:PendingRead", "join-completed:PendingRead" },
                report.EventsFor(CleanupWait.PendingRead));

            // The join's real effects: the read completed THROUGH the join, the stream was disposed
            // explicitly, and the response exactly once.
            Assert.True(report.PendingByteJoined);
            Assert.True(report.ReadJoined);
            Assert.True(report.CapturedStreamDisposed);
            Assert.Equal(1, terminal.Content.DisposedStreamCount);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.Empty(report.JoinExpiries);
        });
    }

    /// <summary>
    /// FAILURE PATH 3. A SECONDARY cleanup fault must neither mask the primary scenario failure
    /// nor skip later release/join/disposal steps. The fault is injected only AFTER the real work of
    /// its step (gate release or stream disposal), so the row can independently assert that every
    /// later step still ran and every resource was still released.
    /// </summary>
    [Theory]
    [InlineData(1)] // fault in step 1: gate release
    [InlineData(3)] // fault in step 3: stream drain/dispose
    [InlineData(4)] // fault in step 4: pending-read join
    public async Task SecondaryCleanupFault_DoesNotMaskPrimary_OrSkipRemainingSteps(int faultStep)
    {
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream");
        var primary = new InvalidOperationException("PRIMARY-SCENARIO-FAILURE");
        var secondaryInvoked = false;
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.Terminal.AllowReturn();
                        ctx.Response = await ctx.Send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.StreamRead = ctx.Response.Content.ReadAsStreamAsync(CancellationToken.None);
                        var stream = await ctx.StreamRead.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.CapturedStream = stream;
                        ctx.PendingByte = stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None);
                        await ctx.Terminal.Content.ReadStarted.Task.WaitAsync(
                            TimeSpan.FromSeconds(30), CancellationToken.None);

                        ctx.SecondaryFaultStep = faultStep;
                        ctx.SecondaryCleanupFault = () =>
                        {
                            secondaryInvoked = true;
                            throw new NotSupportedException("SECONDARY-CLEANUP-FAULT");
                        };
                        throw primary;
                    }));

            Assert.Same(primary, caught);
            Assert.DoesNotContain("SECONDARY-CLEANUP-FAULT", caught.Message, StringComparison.Ordinal);
            Assert.True(secondaryInvoked, "The injected secondary fault must actually have run.");
            Assert.True(report.AllStepsRan, "A secondary cleanup fault must not skip later steps.");
            Assert.Equal(faultStep == 1, report.GatesStepFaulted);
            Assert.Equal(faultStep == 3, report.StreamStepFaulted);
            Assert.Equal(faultStep == 4, report.ReadStepFaulted);
            Assert.True(report.PendingByteJoined,
                "Step 4 must actually join the pending read, not merely mark the step as run.");
            Assert.True(report.PendingByteJoinIndependentlyObserved,
                "Injected step-4 fault must be reached and must not suppress the remaining cleanup.");
            Assert.False(report.SendStepFaulted);
            Assert.False(report.ResponseStepFaulted);
            Assert.True(report.GatesReleased && report.SendJoined && report.StreamDrained
                && report.ReadJoined && report.ResponseDisposed);
            Assert.True(report.CapturedStreamDisposed,
                "A secondary cleanup fault must not skip direct stream disposal.");
            Assert.True(terminal.Content.IsReleased);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.Equal(1, terminal.Content.DisposedStreamCount);
        });
    }

    /// <summary>
    /// FAILURE PATH 4. When the scenario fails BEFORE it awaits the send, the response the send
    /// eventually produces is unobserved. Cleanup must join that send task and dispose the late
    /// response (and hence its held content) instead of leaking it — without masking the primary.
    /// </summary>
    [Fact]
    public async Task UnobservedLateResponse_IsDisposedByCleanup()
    {
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream");
        var primary = new InvalidOperationException("PRIMARY-BEFORE-SEND-AWAITED");
        using var cts = new CancellationTokenSource();
        var report = new HeldSseCleanupReport();

        await WithHeldSseSafetyNetAsync(terminal, report, async () =>
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunHeldSseScenarioAsync(terminal, terminal, cts, CancellationToken.None, report,
                    async ctx =>
                    {
                        await ctx.Terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                        ctx.Terminal.AllowReturn();
                        throw primary; // send completes later; the scenario never captured its response
                    }));

            Assert.Same(primary, caught);
            Assert.True(report.LateResponseDisposed);
            Assert.True(report.AllStepsRan);
            Assert.False(report.AnyStepFaulted);
            Assert.True(report.GatesReleased && report.SendJoined && report.StreamDrained
                && report.ReadJoined && report.ResponseDisposed);
            Assert.True(terminal.Content.IsReleased);
            Assert.Equal(1, terminal.Content.DisposeCount);
        });
    }

    [Fact]
    public async Task HeldSseSafetyNet_BypassedCleanup_ReleasesAndJoinsParkedSend()
    {
        var gates = new HeldGates(holdPublication: true, holdSend: true);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        var report = new HeldSseCleanupReport();
        var primary = new InvalidOperationException("failed outside ordinary held-SSE cleanup");
        using var client = CreateProductionClient(terminal);
        using var request = CreateCompletionsRequest();

        // Deliberately never enter RunHeldSseScenarioAsync: only the outer finally owns this send.
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        report.Ctx = new HeldSseScenario { Terminal = terminal, Send = send };
        try
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WithHeldSseSafetyNetAsync(terminal, report, async () =>
                {
                    await terminal.PublicationReached.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    Assert.Null(terminal.ContentOrNull);
                    Assert.False(send.IsCompleted);
                    Assert.False(gates.Publication.IsOpen);
                    Assert.False(gates.SendReturn.IsOpen);
                    throw primary;
                }));

            Assert.Same(primary, caught);
            Assert.True(send.IsCompletedSuccessfully);
            Assert.True(terminal.IsReturnAllowed);
            Assert.True(terminal.ReleasedAtHandoff);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.Empty(report.SafetyNetFindings);
            Assert.Equal(0, report.SendStep); // the fallback did not satisfy the ordinary probe
            Assert.False(report.SendJoined);
            Assert.False(report.LateResponseDisposed);
        }
        finally
        {
            terminal.OpenAllGates();
            (await send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)).Dispose();
        }
    }

    [Fact]
    public async Task HeldSseSafetyNet_JoinExpiry_ReportsAndDisposesWithoutMaskingPrimary()
    {
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream");
        var report = new HeldSseCleanupReport();
        var primary = new InvalidOperationException("primary with an unjoinable read");
        var completionGate = new Gate(); // intentionally outside the safety net's control
        using var client = CreateProductionClient(terminal);
        using var request = CreateCompletionsRequest();
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        var ctx = new HeldSseScenario { Terminal = terminal, Send = send };
        report.Ctx = ctx;
        try
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WithHeldSseSafetyNetAsync(terminal, report, async () =>
                {
                    await terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    terminal.AllowReturn();
                    ctx.Response = await send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    ctx.StreamRead = ctx.Response.Content.ReadAsStreamAsync(CancellationToken.None);
                    var stream = await ctx.StreamRead.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    ctx.PendingByte = FinishReadAsync(stream);
                    await terminal.Content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    Assert.False(ctx.PendingByte.IsCompleted);
                    Assert.False(terminal.Content.Disposed);
                    Assert.Equal(0, terminal.Content.DisposedStreamCount);
                    throw primary;
                }, joinBudget: TimeSpan.Zero)); // deterministic expiry, not a race against elapsed time

            Assert.Same(primary, caught);
            Assert.Contains("the pending-read join expired", report.SafetyNetFindings);
            Assert.DoesNotContain("the send join expired", report.SafetyNetFindings);
            Assert.DoesNotContain("the stream-creation join expired", report.SafetyNetFindings);
            Assert.False(ctx.PendingByte!.IsCompleted);
            Assert.True(terminal.Content.IsReleased);
            Assert.Equal(1, terminal.Content.DisposedStreamCount);
            Assert.Equal(1, terminal.Content.DisposeCount);
            Assert.False(report.ReadJoined);
            Assert.False(report.ResponseDisposed);
        }
        finally
        {
            terminal.OpenAllGates();
            completionGate.Open();
            if (ctx.PendingByte is not null)
                await ctx.PendingByte.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            (await send.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)).Dispose();
        }

        async Task<int> FinishReadAsync(Stream stream)
        {
            var read = await stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None);
            await completionGate.Task;
            return read;
        }
    }

    // ── Outer-fallback ordering: the sticky release must land BEFORE any gate lets a parked ─────
    // ── continuation publish or hand the response off ───────────────────────────────────────────

    /// <summary>The two states the outer fallback can take over from.</summary>
    private enum FallbackWindow
    {
        /// <summary>The terminal is parked BEFORE it created its content: nothing is published.</summary>
        ContentNotYetPublished,

        /// <summary>The content is published and the response is parked just before its return.</summary>
        PublishedParkedBeforeReturn,
    }

    /// <summary>Everything a fallback-window run recorded; assertions run on it after cleanup.</summary>
    private sealed class FallbackWindowRun
    {
        public FallbackWindowRun(
            HeldEventStreamTerminalHandler terminal, ResponseObserverHandler observer,
            HeldSseCleanupReport report, InvalidOperationException primary)
        {
            Terminal = terminal;
            Observer = observer;
            Report = report;
            Primary = primary;
        }

        public HeldEventStreamTerminalHandler Terminal { get; }
        public ResponseObserverHandler Observer { get; }
        public HeldSseCleanupReport Report { get; }
        public InvalidOperationException Primary { get; }
        public Exception? Caught { get; set; }
        public bool SendCompletedBeforeEmergencyCleanup { get; set; }
        public int DisposeCountBeforeEmergencyCleanup { get; set; }
        public bool BodyReleasedBeforeEmergencyCleanup { get; set; }

        /// <summary>True when the bounded rendezvous saw the first receiving handler (not a timeout).</summary>
        public bool RendezvousReached { get; set; }

        /// <summary>Probe-time facts, captured BEFORE the fallback ran.</summary>
        public bool? ReleasedAtPublicationBeforeFallback { get; set; }
        public bool ContentExistedBeforeFallback { get; set; }
        public bool PublicationGateOpenBeforeFallback { get; set; }
        public bool ResponseAllocatedBeforeFallback { get; set; }
        public bool SendParkedBeforeFallback { get; set; }
        public bool BodyReleasedBeforeFallback { get; set; }
        public int HandoffsBeforeFallback { get; set; } = -1;
        public bool ReleaseRequestedBeforeFallback { get; set; }
    }

    /// <summary>
    /// Drives ONE fallback window through the REAL production chain and the ACTUAL outer fallback
    /// (<see cref="WithHeldSseSafetyNetAsync"/> → <c>OpenAllGates</c>), never through the ordinary
    /// scenario harness, so only the fallback can open a gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rendezvous.</b> <c>AfterGatesOpenedForTest</c> blocks the fallback's gate-opening call
    /// — AFTER every gate is open — until the response has reached the first handler above the
    /// terminal (bounded, 30 s, on <see cref="CancellationToken.None"/>). By then the released
    /// continuation has published and handed off, so what is recorded is exactly what an
    /// old-order continuation would see: nothing that follows the gate opening in the caller can
    /// have run yet. A release recorded before the gates is therefore already visible; one recorded
    /// after them is provably late. A timeout is recorded (<see cref="FallbackWindowRun.RendezvousReached"/>)
    /// and asserted, so a failure can never masquerade as a missing observation.
    /// </para>
    /// <para>
    /// <b>Independent cleanup.</b> The probe's own send/response are released and joined in a
    /// <c>finally</c> on <see cref="CancellationToken.None"/>, whatever happened above and without
    /// the scenario token. Every step is attempted. A bounded-join expiry or a leftover is REPORTED
    /// (a diagnostic when a primary failure is in flight, otherwise a failure) and never replaces
    /// the primary failure. This cleanup runs after the recorded observations and is never what
    /// satisfies them.
    /// </para>
    /// </remarks>
    private static async Task<FallbackWindowRun> RunFallbackWindowAsync(
        FallbackWindow window, bool legacyOrderControl)
    {
        var gates = new HeldGates(holdPublication: window == FallbackWindow.ContentNotYetPublished);
        var terminal = new HeldEventStreamTerminalHandler("text/event-stream", gates);
        var observer = new ResponseObserverHandler(terminal);
        var report = new HeldSseCleanupReport();
        var primary = new InvalidOperationException("PRIMARY-FALLBACK-WINDOW");
        var run = new FallbackWindowRun(terminal, observer, report, primary);

        using var client = CreateProductionClient(observer);
        using var request = CreateCompletionsRequest();

        // Deliberately never enter RunHeldSseScenarioAsync: only the outer fallback owns this send.
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        report.Ctx = new HeldSseScenario { Terminal = terminal, Send = send };

        terminal.AfterGatesOpenedForTest = () =>
            run.RendezvousReached = observer.Seen.Wait(TimeSpan.FromSeconds(30));

        var completed = false;
        try
        {
            async Task ProbeAsync()
            {
                if (window == FallbackWindow.ContentNotYetPublished)
                    await terminal.PublicationReached.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                else
                    await terminal.Arrived.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

                // Capture the starting state BEFORE anything can change it. These are recorded
                // facts, asserted by the callers; no fallback has run and the relevant gate is closed.
                run.ContentExistedBeforeFallback = terminal.ContentOrNull is not null;
                run.PublicationGateOpenBeforeFallback = gates.Publication.IsOpen;
                run.ResponseAllocatedBeforeFallback = terminal.ResponseAllocated;
                run.SendParkedBeforeFallback = !send.IsCompleted && !terminal.IsReturnAllowed;
                run.BodyReleasedBeforeFallback = terminal.ContentOrNull is { IsReleased: true };
                run.ReleasedAtPublicationBeforeFallback = terminal.ReleasedAtPublication;
                run.HandoffsBeforeFallback = terminal.HandoffCount;
                run.ReleaseRequestedBeforeFallback = terminal.ReleaseRequested;
                throw primary;
            }

            if (!legacyOrderControl)
            {
                run.Caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    WithHeldSseSafetyNetAsync(terminal, report, ProbeAsync));
            }
            else
            {
                // Control: the SAME probe and the SAME rendezvous, but the pre-repair gate order.
                try { await ProbeAsync(); }
                catch (InvalidOperationException ex) { run.Caught = ex; }
                await Task.Run(terminal.OpenAllGatesLegacyOrderForTest, CancellationToken.None);
            }

            // Capture the actual fallback/control effects before emergency cleanup can supply them.
            run.SendCompletedBeforeEmergencyCleanup = send.IsCompletedSuccessfully;
            run.DisposeCountBeforeEmergencyCleanup = terminal.ContentOrNull?.DisposeCount ?? 0;
            run.BodyReleasedBeforeEmergencyCleanup = terminal.ContentOrNull is { IsReleased: true };
            completed = true;
        }
        finally
        {
            // Independent of the scenario token and of anything above: release, join, dispose.
            var leftovers = new List<string>();
            terminal.AfterGatesOpenedForTest = null; // never block this cleanup on the seam
            try { await Task.Run(terminal.OpenAllGates, CancellationToken.None); }
            catch (Exception ex) { leftovers.Add($"opening gates faulted: {ex.GetType().Name}"); }

            try
            {
                if (!await JoinBoundedAsync(send, TimeSpan.FromSeconds(30)))
                    leftovers.Add("the probe's send join expired (30s)");
                else if (send.IsCompletedSuccessfully)
                    send.Result.Dispose(); // idempotent: the fallback may already have disposed it
            }
            catch (Exception ex) { leftovers.Add($"joining/disposing the probe send faulted: {ex.GetType().Name}"); }

            try { terminal.DisposeAllocatedResponseIfLeaked(); }
            catch (Exception ex) { leftovers.Add($"disposing the allocated response faulted: {ex.GetType().Name}"); }

            if (leftovers.Count > 0)
            {
                var message = "Fallback-window probe cleanup found leftover work: " + string.Join("; ", leftovers);
                if (completed) throw new Xunit.Sdk.XunitException(message);
                TestContext.Current.SendDiagnosticMessage(message); // never mask the primary failure
            }
        }

        return run;
    }

    /// <summary>
    /// Asserts the facts that must hold for either window BEFORE the release assertions, so a
    /// failure there names the real cause instead of a downstream symptom.
    /// </summary>
    private static void AssertFallbackRanCleanly(FallbackWindowRun run)
    {
        Assert.Same(run.Primary, run.Caught);
        Assert.True(run.RendezvousReached,
            "The bounded rendezvous timed out: the response never reached the first receiving handler.");
        Assert.True(run.SendCompletedBeforeEmergencyCleanup,
            "The outer fallback must have joined the released send before emergency cleanup.");
        Assert.Equal(1, run.Terminal.HandoffCount);
        Assert.True(run.Observer.Seen.IsCompletedSuccessfully);
    }

    /// <summary>
    /// FALLBACK WINDOW 1 — CONTENT NOT YET PUBLISHED. The outer fallback takes over while the
    /// terminal is parked at its publication gate with NOTHING published. Opening that gate lets a
    /// continuation publish and hand off at once, before the next statement of the caller. The
    /// sticky release must therefore already be recorded, so the body is released AT PUBLICATION
    /// and at the first handoff — observed at the first receiving handler, before any disposal.
    /// </summary>
    [Fact]
    public async Task OuterFallback_ContentNotYetPublished_ReleasesAtPublicationAndFirstHandoff()
    {
        var run = await RunFallbackWindowAsync(FallbackWindow.ContentNotYetPublished, legacyOrderControl: false);

        // The fallback really started from "nothing published, nothing handed off, nothing released".
        Assert.False(run.ContentExistedBeforeFallback);
        Assert.False(run.PublicationGateOpenBeforeFallback);
        Assert.False(run.ResponseAllocatedBeforeFallback);
        Assert.True(run.SendParkedBeforeFallback);
        Assert.Equal(0, run.HandoffsBeforeFallback);
        Assert.False(run.ReleaseRequestedBeforeFallback);

        AssertFallbackRanCleanly(run);

        // RELEASE ASSERTIONS: already released the instant the content was published, when the
        // response left the terminal, and when the first handler above the terminal received it.
        Assert.True(run.Terminal.ReleasedAtPublication,
            "The body must already be released at publication (the sticky release was recorded after the gate opened).");
        Assert.True(run.Terminal.ReleasedAtHandoff,
            "The body must already be released at the first handoff.");
        Assert.True(run.Observer.ContentReleasedWhenSeen,
            "The first receiving handler must observe an already-released body.");

        // ...and that was BEFORE any disposal: disposal is what releases a body anyway.
        Assert.Equal(0, run.Observer.DisposeCountWhenSeen);
        Assert.Equal(1, run.DisposeCountBeforeEmergencyCleanup);
        Assert.Empty(run.Report.SafetyNetFindings);
        Assert.Equal(0, run.Report.SendStep); // the fallback, not the ordinary probe cleanup, did the work
    }

    /// <summary>
    /// FALLBACK WINDOW 2 — CONTENT PUBLISHED, RESPONSE PARKED BEFORE RETURN. The send is parked
    /// with the body published and UNRELEASED. Opening the return gate lets a continuation hand the
    /// response off at once. The release must already be applied to the published content, so it
    /// is observed released at the terminal's handoff and in the first receiving handler, before
    /// disposal.
    /// </summary>
    [Fact]
    public async Task OuterFallback_PublishedParkedBeforeReturn_ReleasesAtHandoffAndInFirstReceiver()
    {
        var run = await RunFallbackWindowAsync(FallbackWindow.PublishedParkedBeforeReturn, legacyOrderControl: false);

        // The fallback really started from "published, parked, unreleased, not handed off".
        Assert.True(run.ContentExistedBeforeFallback);
        Assert.True(run.ResponseAllocatedBeforeFallback);
        Assert.True(run.SendParkedBeforeFallback);
        Assert.False(run.BodyReleasedBeforeFallback);
        Assert.False(run.ReleasedAtPublicationBeforeFallback);
        Assert.Equal(0, run.HandoffsBeforeFallback);
        Assert.False(run.ReleaseRequestedBeforeFallback);

        AssertFallbackRanCleanly(run);

        // RELEASE ASSERTIONS.
        Assert.True(run.Terminal.ReleasedAtHandoff,
            "The body must already be released at the terminal's handoff.");
        Assert.True(run.Observer.ContentReleasedWhenSeen,
            "The first receiving handler must observe an already-released body.");

        // The body was published unreleased, so this release came from the fallback, not from creation.
        Assert.False(run.Terminal.ReleasedAtPublication);
        Assert.Equal(0, run.Observer.DisposeCountWhenSeen);
        Assert.Equal(1, run.DisposeCountBeforeEmergencyCleanup);

        // The probe left a PUBLISHED, undisposed response behind, and the net says so honestly (it
        // takes over after the probe, so this is a report, not a satisfied assertion). Exactly that
        // finding: no join expired, nothing faulted.
        Assert.Equal(
            new[] { "a response was still undisposed when the safety net took over" },
            run.Report.SafetyNetFindings);
        Assert.Equal(0, run.Report.SendStep);
    }

    /// <summary>
    /// SAME-HARNESS CONTROL. The very same runner, probe and rendezvous, but with the PRE-REPAIR
    /// gate order (gates, then release). It must show the defect — the body observed UNRELEASED at
    /// publication / handoff / first receiver — proving the rendezvous genuinely exposes the old
    /// interleaving, so the two rows above cannot be passing vacuously. (The fallback itself is
    /// not involved here; the order-restoring mutant is what proves the fallback's order.)
    /// </summary>
    [Theory]
    [InlineData(true)]  // FallbackWindow.ContentNotYetPublished
    [InlineData(false)] // FallbackWindow.PublishedParkedBeforeReturn
    public async Task LegacyGateOrder_Control_ShowsTheReviewedWindowIsObservable(bool contentNotYetPublished)
    {
        var window = contentNotYetPublished
            ? FallbackWindow.ContentNotYetPublished
            : FallbackWindow.PublishedParkedBeforeReturn;
        var run = await RunFallbackWindowAsync(window, legacyOrderControl: true);

        Assert.Same(run.Primary, run.Caught);
        Assert.True(run.RendezvousReached);
        Assert.Equal(1, run.Terminal.HandoffCount);

        // The defect, observed at the first receiving handler: not released yet.
        Assert.False(run.Terminal.ReleasedAtHandoff);
        Assert.False(run.Observer.ContentReleasedWhenSeen);
        Assert.Equal(0, run.Observer.DisposeCountWhenSeen);
        if (window == FallbackWindow.ContentNotYetPublished)
            Assert.False(run.Terminal.ReleasedAtPublication);

        // The late release still landed afterwards (it just landed too late to matter).
        Assert.True(run.BodyReleasedBeforeEmergencyCleanup);
        Assert.Equal(0, run.DisposeCountBeforeEmergencyCleanup);
    }

    /// <summary>The three cleanup waits a held-SSE scenario can be caught at.</summary>
    private enum CleanupWait { Send, StreamCreation, PendingRead }

    /// <summary>The five cleanup steps, in order.</summary>
    private enum CleanupStep { Gates, Send, Stream, Read, Response }

    /// <summary>What was true at the instant cleanup reached one of its waits.</summary>
    private sealed record WaitObservation(bool ScenarioTokenCancelled, bool OperationIncomplete);

    /// <summary>
    /// Mutable per-scenario state. The scenario records the resources it created; the harness's
    /// finally reads them back. <see cref="SecondaryFaultStep"/> injects a fault into exactly one
    /// cleanup step without changing the harness itself.
    /// </summary>
    private sealed class HeldSseScenario
    {
        public HeldEventStreamTerminalHandler Terminal { get; init; } = null!;
        public Task<HttpResponseMessage> Send { get; init; } = null!;
        public CancellationToken Token { get; init; }
        public HttpResponseMessage? Response { get; set; }
        public Task<Stream>? StreamRead { get; set; }
        public Task<int>? PendingByte { get; set; }
        public Stream? CapturedStream { get; set; }
        public int SecondaryFaultStep { get; set; }
        public Action? SecondaryCleanupFault { get; set; }
    }

    /// <summary>
    /// Observable evidence for every cleanup step: whether it RAN, whether it FAULTED, and whether
    /// its real release/join/disposal effect happened. Asserting the effects (rather than merely the
    /// step counters) is what makes a token-bound or skipped drain detectable. It also carries the
    /// ORDERED event log and the per-wait observations the cancellation-independence rows rely on:
    /// a step-entry signal or a final disposed state alone cannot prove the ordering.
    /// </summary>
    private sealed class HeldSseCleanupReport
    {
        public int GatesStep, SendStep, StreamStep, ReadStep, ResponseStep;
        public bool GatesReleased, SendJoined, LateResponseDisposed, StreamDrained, ReadJoined, ResponseDisposed;
        public bool CapturedStreamDisposed, PendingByteJoined, PendingByteJoinIndependentlyObserved;
        public bool GatesStepFaulted, SendStepFaulted, StreamStepFaulted, ReadStepFaulted, ResponseStepFaulted;

        /// <summary>Whether content already existed when step 1 asked for the release.</summary>
        public bool PublishedAtGateRelease;

        /// <summary>Whether the release request was recorded by the end of step 1.</summary>
        public bool ReleaseRequestedAtGateRelease;

        /// <summary>Whether step 3 itself disposed the stream (observed inside step 3).</summary>
        public bool StreamDisposedByCleanup;

        /// <summary>Whether the content was already disposed when step 3 disposed the stream.</summary>
        public bool ContentDisposedAtStreamDisposal;

        /// <summary>The scenario the harness ran, so an outer safety net can reach its resources.</summary>
        public HeldSseScenario? Ctx { get; set; }
        public Task? Releaser { get; set; }
        public IReadOnlyList<string> SafetyNetFindings { get; set; } = [];

        /// <summary>Ordered, thread-safe event log (<c>wait-entered</c>, <c>gate-opened</c>, <c>join-completed</c>).</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();

        /// <summary>Joins that hit their bounded wait instead of completing.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<CleanupStep> JoinExpiries { get; } = new();

        private readonly System.Collections.Concurrent.ConcurrentDictionary<CleanupWait, TaskCompletionSource<WaitObservation>>
            _waits = new();

        public bool AllStepsRan => GatesStep == 1 && SendStep == 1 && StreamStep == 1
            && ReadStep == 1 && ResponseStep == 1;

        public bool AnyStepFaulted => GatesStepFaulted || SendStepFaulted || StreamStepFaulted
            || ReadStepFaulted || ResponseStepFaulted;

        private TaskCompletionSource<WaitObservation> Slot(CleanupWait wait) =>
            _waits.GetOrAdd(wait, _ => new TaskCompletionSource<WaitObservation>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Completes when cleanup is observed at <paramref name="wait"/>.</summary>
        public Task<WaitObservation> WaitReached(CleanupWait wait) => Slot(wait).Task;

        /// <summary>
        /// Records, BEFORE signalling, the state at the instant cleanup reaches <paramref name="wait"/>.
        /// An independent releaser acts only on this signal, so everything recorded here strictly
        /// precedes the release.
        /// </summary>
        public void NoteWait(CleanupWait wait, WaitObservation observation)
        {
            Events.Enqueue($"wait-entered:{wait}");
            Slot(wait).TrySetResult(observation);
        }

        /// <summary>Wakes every pending rendezvous (used by the safety net so no releaser outlives the test).</summary>
        public void AbandonWaits()
        {
            foreach (var wait in Enum.GetValues<CleanupWait>())
                Slot(wait).TrySetCanceled();
        }

        public IReadOnlyList<string> EventsFor(CleanupWait wait) =>
            Events.Where(e => e.EndsWith(":" + wait, StringComparison.Ordinal)).ToArray();

        /// <summary>Records a cleanup step's fault; a <see cref="TimeoutException"/> is a bounded-join expiry.</summary>
        public void NoteFault(CleanupStep step, Exception ex)
        {
            switch (step)
            {
                case CleanupStep.Gates: GatesStepFaulted = true; break;
                case CleanupStep.Send: SendStepFaulted = true; break;
                case CleanupStep.Stream: StreamStepFaulted = true; break;
                case CleanupStep.Read: ReadStepFaulted = true; break;
                case CleanupStep.Response: ResponseStepFaulted = true; break;
            }

            if (ex is TimeoutException) JoinExpiries.Enqueue(step);
        }
    }

    /// <summary>
    /// Independent releaser body: waits until cleanup is OBSERVED at <paramref name="wait"/>, and
    /// only then opens <paramref name="gate"/>. Run on its own task, so the release can never be a
    /// side effect of the cleanup it is meant to unblock.
    /// </summary>
    private static async Task OpenGateAfterCleanupWaitsAsync(
        HeldSseCleanupReport report, CleanupWait wait, Gate gate)
    {
        try
        {
            await report.WaitReached(wait).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return; // abandoned by the safety net: it opens every gate itself.
        }

        report.Events.Enqueue($"gate-opened:{wait}");
        gate.Open();
    }

    /// <summary>
    /// Outer SAFETY NET for a failure-path probe. Whatever the probe did — passed, failed an
    /// assertion, or was broken by a mutated cleanup — this releases every gate and joins and
    /// disposes everything INDEPENDENTLY of the scenario token and of any injected failure, so no
    /// parked task or undisposed response outlives the test. It attempts every step, never replaces
    /// the probe's own failure, and REPORTS (never silently abandons) a bounded-join expiry or a
    /// resource still undisposed when the net took over. Its effects are never used to satisfy the
    /// probe's assertions: those run inside <paramref name="probe"/>, before this finally.
    /// </summary>
    private static async Task WithHeldSseSafetyNetAsync(
        HeldEventStreamTerminalHandler terminal, HeldSseCleanupReport report, Func<Task> probe,
        TimeSpan? joinBudget = null)
    {
        Exception? primary = null;
        try
        {
            await probe();
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            var findings = await ReleaseAndJoinIndependentlyAsync(terminal, report, joinBudget ?? TimeSpan.FromSeconds(30));
            report.SafetyNetFindings = findings;
            if (findings.Count > 0)
            {
                var message = "Held-SSE safety net found leftover work: " + string.Join("; ", findings);

                // With a primary failure in flight, throwing here would mask it: report instead.
                if (primary is null) throw new Xunit.Sdk.XunitException(message);
                TestContext.Current.SendDiagnosticMessage(message);
            }
        }
    }

    private static async Task<List<string>> ReleaseAndJoinIndependentlyAsync(
        HeldEventStreamTerminalHandler terminal, HeldSseCleanupReport report, TimeSpan joinBudget)
    {
        var findings = new List<string>();

        // A response that was allocated but is still undisposed NOW — before this net touches
        // anything — is a leak the probe's own cleanup left behind (or has not reached yet).
        try
        {
            if (terminal.ResponseAllocated && terminal.ContentOrNull is { DisposeCount: 0 })
                findings.Add("a response was still undisposed when the safety net took over");
        }
        catch (Exception ex) { findings.Add($"leak check faulted: {ex.GetType().Name}"); }

        try { report.AbandonWaits(); }
        catch (Exception ex) { findings.Add($"abandoning waits faulted: {ex.GetType().Name}"); }

        // Offloaded: the window seam may block this call until a released continuation has run, and
        // that continuation must never depend on the caller's (possibly single-threaded) context.
        try { await Task.Run(terminal.OpenAllGates, CancellationToken.None); }
        catch (Exception ex) { findings.Add($"opening gates faulted: {ex.GetType().Name}"); }

        var ctx = report.Ctx;
        if (ctx is not null)
        {
            if (!await JoinBoundedAsync(ctx.Send, joinBudget))
                findings.Add("the send join expired");
            else if (ctx.Send.IsCompletedSuccessfully)
                TryDispose(ctx.Send.Result, findings, "late response");

            if (ctx.StreamRead is not null)
            {
                if (!await JoinBoundedAsync(ctx.StreamRead, joinBudget))
                    findings.Add("the stream-creation join expired");
                else if (ctx.StreamRead.IsCompletedSuccessfully)
                    TryDispose(ctx.StreamRead.Result, findings, "stream");
            }

            if (ctx.PendingByte is not null && !await JoinBoundedAsync(ctx.PendingByte, joinBudget))
                findings.Add("the pending-read join expired");

            TryDispose(ctx.Response, findings, "scenario response");
        }

        if (report.Releaser is { } releaser && !await JoinBoundedAsync(releaser, joinBudget))
            findings.Add("the gate-releaser join expired");

        try { terminal.DisposeAllocatedResponseIfLeaked(); }
        catch (Exception ex) { findings.Add($"disposing the allocated response faulted: {ex.GetType().Name}"); }

        return findings;
    }

    /// <summary>Joins <paramref name="task"/> within the supplied budget on <see cref="CancellationToken.None"/>; its outcome is irrelevant.</summary>
    private static async Task<bool> JoinBoundedAsync(Task task, TimeSpan joinBudget)
    {
        var finished = await Task.WhenAny(task, Task.Delay(joinBudget, CancellationToken.None));
        if (!ReferenceEquals(finished, task)) return false;

        try { await task; }
        catch { /* the outcome (including a deliberate fault) is not the net's concern */ }
        return true;
    }

    private static void TryDispose(IDisposable? resource, List<string> findings, string what)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { findings.Add($"disposing the {what} faulted: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// The SHARED held-SSE fixture harness: one production handler chain, one send task started
    /// OUTSIDE the try, and one finally whose five independent cleanup steps use
    /// <see cref="CancellationToken.None"/> throughout. Every success-path and failure-path row
    /// above runs through this exact implementation, so a scratch mutation of any protection is
    /// load-bearing against at least one row.
    /// </summary>
    private static async Task RunHeldSseScenarioAsync(
        HttpMessageHandler chainTerminal,
        HeldEventStreamTerminalHandler heldTerminal,
        CancellationTokenSource scenarioCts,
        CancellationToken transportToken,
        HeldSseCleanupReport report,
        Func<HeldSseScenario, Task> scenario)
    {
        using var client = CreateProductionClient(chainTerminal);

        // Declared BEFORE the try so the finally can always observe them. The send task is started
        // here, outside the try, so it is always owned and joined even when the scenario fails
        // before awaiting it.
        HttpResponseMessage? response = null;
        Task<Stream>? streamRead = null;
        Task<int>? pendingByte = null;
        var send = client.SendAsync(
            CreateCompletionsRequest(), HttpCompletionOption.ResponseHeadersRead, transportToken);

        var ctx = new HeldSseScenario
        {
            Terminal = heldTerminal,
            Send = send,
            Token = scenarioCts.Token,
        };
        report.Ctx = ctx;

        try
        {
            await scenario(ctx);
        }
        finally
        {
            response = ctx.Response;
            streamRead = ctx.StreamRead;
            pendingByte = ctx.PendingByte;
            var drain = TimeSpan.FromSeconds(30);

            // 1. Release BOTH gates first. The release is a REQUEST that persists: when no content
            //    has been published yet it is applied at publication. It is also null-safe: a fault
            //    before the terminal was reached leaves no content, and must not throw here.
            report.GatesStep++;
            SafeCleanup(() =>
            {
                report.PublishedAtGateRelease = heldTerminal.ContentOrNull is not null;
                heldTerminal.AllowReturn();
                heldTerminal.ReleaseHeldContent();
                report.ReleaseRequestedAtGateRelease = heldTerminal.ReleaseRequested;
                report.GatesReleased = heldTerminal.IsReturnAllowed
                    && heldTerminal.ReleaseRequested
                    && (heldTerminal.ContentOrNull is null || heldTerminal.ContentOrNull.IsReleased);
                InjectSecondaryFault(ctx, 1);
            }, ex => report.NoteFault(CleanupStep.Gates, ex));

            // 2. Join the send. If the scenario never captured its response, the late response
            //    still needs disposal.
            report.SendStep++;
            await SafeCleanupAsync(async () =>
            {
                // The bounded wait is CREATED first and only then observed/signalled, so the
                // independent releaser can never run between "observed incomplete" and the wait
                // being in place: a wait bound to the (already cancelled) scenario token is already
                // faulted here, and a correct wait is still pending until the releaser acts.
                var sendWait = send.WaitAsync(drain, CancellationToken.None);
                report.NoteWait(CleanupWait.Send, new WaitObservation(
                    ctx.Token.IsCancellationRequested, !send.IsCompleted && !sendWait.IsCompleted));
                var late = await sendWait;
                report.Events.Enqueue("join-completed:Send");
                report.SendJoined = true;
                if (!ReferenceEquals(late, response))
                {
                    late.Dispose();
                    report.LateResponseDisposed = true;
                }
            }, ex => report.NoteFault(CleanupStep.Send, ex));

            // 3. Drain the stream-creation task and dispose the stream it produced.
            report.StreamStep++;
            await SafeCleanupAsync(async () =>
            {
                if (streamRead is not null)
                {
                    var streamWait = streamRead.WaitAsync(drain, CancellationToken.None);
                    report.NoteWait(CleanupWait.StreamCreation, new WaitObservation(
                        ctx.Token.IsCancellationRequested, !streamRead.IsCompleted && !streamWait.IsCompleted));
                    var stream = await streamWait;
                    report.Events.Enqueue("join-completed:StreamCreation");
                    report.ContentDisposedAtStreamDisposal = heldTerminal.ContentOrNull is { Disposed: true };
                    stream.Dispose();
                    report.StreamDisposedByCleanup = heldTerminal.ContentOrNull is { DisposedStreamCount: > 0 };
                }
                report.StreamDrained = true;
                // Observed HERE (step 3), before step 5 disposes the response: an indirect
                // disposal by the response later cannot make this flag true, so removing the
                // explicit stream.Dispose() in this step is detectable.
                report.CapturedStreamDisposed = ctx.CapturedStream is not null
                    && heldTerminal.Content.DisposedStreamCount > 0;
                InjectSecondaryFault(ctx, 3);
            }, ex => report.NoteFault(CleanupStep.Stream, ex));

            // 4. Join the pending read (now released to EOF, or faulted by cancellation).
            report.ReadStep++;
            await SafeCleanupAsync(async () =>
            {
                if (pendingByte is not null)
                {
                    var readWait = pendingByte.WaitAsync(drain, CancellationToken.None);
                    report.NoteWait(CleanupWait.PendingRead, new WaitObservation(
                        ctx.Token.IsCancellationRequested, !pendingByte.IsCompleted && !readWait.IsCompleted));
                    // Independent observation of the JOIN itself: a later indirect completion of
                    // the read cannot make PendingByteJoined true here.
                    _ = await readWait;
                    report.Events.Enqueue("join-completed:PendingRead");
                    report.PendingByteJoined = pendingByte.IsCompletedSuccessfully;
                }
                report.ReadJoined = true;
                report.PendingByteJoinIndependentlyObserved = true;
                InjectSecondaryFault(ctx, 4);
            }, ex => report.NoteFault(CleanupStep.Read, ex));

            // 5. Finally dispose the response.
            report.ResponseStep++;
            SafeCleanup(() =>
            {
                response?.Dispose();
                report.ResponseDisposed = true;
            }, ex => report.NoteFault(CleanupStep.Response, ex));
        }
    }

    private static void InjectSecondaryFault(HeldSseScenario ctx, int step)
    {
        if (ctx.SecondaryFaultStep == step)
            ctx.SecondaryCleanupFault?.Invoke();
    }

    /// <summary>
    /// Runs one cleanup step, swallowing ANY failure of that step: cleanup must never mask the
    /// primary scenario failure or prevent the remaining cleanup steps from running.
    /// </summary>
    private static void SafeCleanup(Action step, Action<Exception>? onFault = null)
    {
        try { step(); }
        catch (Exception ex) { onFault?.Invoke(ex); }
    }

    /// <summary>Async form of <see cref="SafeCleanup(Action, Action{Exception})"/>.</summary>
    private static async Task SafeCleanupAsync(Func<Task> step, Action<Exception>? onFault = null)
    {
        try { await step(); }
        catch (Exception ex) { onFault?.Invoke(ex); }
    }

    /// <summary>
    /// Non-success <c>text/event-stream</c> keeps the EXISTING non-success handling: the status
    /// stays, the (finite) body is buffered and preserved verbatim, and the resilience policy still
    /// retries (1 + 3) — the SSE bypass applies to SUCCESSFUL responses only. Both the all-uppercase
    /// media type and a JSON-shaped failure body carrying <c>finish_reason="error"</c> are covered,
    /// so moving the bypass above the status check or narrowing its casing would be caught here.
    /// </summary>
    [Theory]
    [InlineData("text/event-stream", "event: error\ndata: {}\n\n")]
    [InlineData("TEXT/EVENT-STREAM", """{"choices":[{"message":{"role":"assistant","content":"x"},"finish_reason":"error"}]}""")]
    public async Task NonSuccessEventStream_KeepsExistingNonSuccessHandling(string mediaType, string errorStream)
    {
        var terminal = new EventStreamTerminalHandler(
            mediaType, errorStream, HttpStatusCode.InternalServerError);
        using var client = CreateProductionClient(terminal);

        using var response = await client.SendAsync(
            CreateCompletionsRequest(), HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(4, terminal.Attempts);
        Assert.Equal(
            errorStream,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        // The existing handler buffered the error body (so it WAS read) and re-wrapped it.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotSame(terminal.Content, response.Content);
        Assert.Equal(1, terminal.Content.ReadCount);
    }

    /// <summary>
    /// Terminal handler that answers with a retryable body for its first
    /// <c>failures</c> attempts and with its success body afterwards, counting every attempt.
    /// </summary>
    private sealed class ScriptedTerminalHandler : HttpMessageHandler
    {
        private readonly string _successBody;
        private int _remainingFailures;
        private int _errorContentDisposals;

        public ScriptedTerminalHandler(int failures, string successBody)
        {
            _remainingFailures = failures;
            _successBody = successBody;
        }

        /// <summary>Number of attempts that reached the transport.</summary>
        public int Attempts { get; private set; }

        /// <summary>
        /// How many times a rejected error-finish-reason response body was disposed — proving the
        /// handler released the response it refused to hand on.
        /// </summary>
        public int ErrorContentDisposals => Volatile.Read(ref _errorContentDisposals);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            if (_remainingFailures > 0)
            {
                if (_remainingFailures != int.MaxValue) _remainingFailures--;

                // A SUCCESS status carrying an explicit provider error finish reason: exactly the
                // shape CopilotHive observed.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new DisposalTrackingContent(
                        ErrorBody, "application/json", () => Interlocked.Increment(ref _errorContentDisposals)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_successBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// A <see cref="StringContent"/> that reports every disposal, so a test can prove ownership was
    /// correctly taken (and released) by the handler that rejected it.
    /// </summary>
    private sealed class DisposalTrackingContent : StringContent
    {
        private readonly Action _onDispose;

        public DisposalTrackingContent(string body, string mediaType, Action onDispose)
            : base(body, Encoding.UTF8, mediaType)
            => _onDispose = onDispose;

        protected override void Dispose(bool disposing)
        {
            _onDispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Terminal handler that always answers with one canned successful body.</summary>
    private sealed class SingleShotTerminalHandler : HttpMessageHandler
    {
        private readonly string _body;

        public SingleShotTerminalHandler(string body) => _body = body;

        /// <summary>Number of attempts that reached the transport.</summary>
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Terminal handler that always answers with one fixed failure status.</summary>
    private sealed class HttpStatusTerminalHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public HttpStatusTerminalHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        /// <summary>Number of attempts that reached the transport.</summary>
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(_status)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Terminal handler that parks the request until it is cancelled, signalling
    /// <see cref="Started"/> once it is really in flight.
    /// </summary>
    private sealed class BlockingTerminalHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            Started.TrySetResult();

            // Never completes on its own: only cancellation (or a timeout in the caller) ends it.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        }
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that counts every way a consumer can pull its bytes
    /// (serialization, read-stream creation, buffering) and records disposal, so a test can prove
    /// the handler performed zero reads and left the instance alone.
    /// </summary>
    private sealed class ReadTrackingContent : HttpContent
    {
        private readonly byte[] _bytes;
        private int _readCount;

        public ReadTrackingContent(string mediaType, string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }

        public int ReadCount => Volatile.Read(ref _readCount);

        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Increment(ref _readCount);
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            return stream.WriteAsync(_bytes, 0, _bytes.Length, cancellationToken);
        }

        protected override void SerializeToStream(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            stream.Write(_bytes, 0, _bytes.Length);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            return new MemoryStream(_bytes, writable: false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A terminal handler that records the exact request body reaching the transport and returns
    /// one ordinary completion, so request-side rewrites can be asserted without inspecting the
    /// test's own request object.
    /// </summary>
    private sealed class RecordingRequestTerminalHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingRequestTerminalHandler(string responseBody) => _responseBody = responseBody;

        public int Attempts { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// A one-shot, idempotent gate. <see cref="Open"/> may be called any number of times from any
    /// thread; waiters resume asynchronously so opening never runs a waiter inline.
    /// </summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _tcs.Task;
        public bool IsOpen => _tcs.Task.IsCompletedSuccessfully;
        public void Open() => _tcs.TrySetResult();

        public static Gate Opened()
        {
            var gate = new Gate();
            gate.Open();
            return gate;
        }
    }

    /// <summary>
    /// The INDEPENDENT gates a held-SSE scenario can close. Each one is deliberately separate from
    /// the two gates cleanup step 1 opens (the terminal's return gate and the body-release gate), so
    /// a scenario can keep exactly one operation genuinely in flight when cleanup reaches the wait
    /// that joins it. A gate that is not requested is created already open and changes nothing.
    /// </summary>
    private sealed class HeldGates
    {
        public HeldGates(
            bool holdPublication = false, bool holdSend = false,
            bool holdStreamCreation = false, bool holdRead = false)
        {
            Publication = holdPublication ? new Gate() : Gate.Opened();
            SendReturn = holdSend ? new Gate() : Gate.Opened();
            StreamCreation = holdStreamCreation ? new Gate() : Gate.Opened();
            ReadFinish = holdRead ? new Gate() : Gate.Opened();
        }

        /// <summary>Held BEFORE the terminal creates and publishes its content.</summary>
        public Gate Publication { get; }

        /// <summary>Held AFTER the return gate, so the send stays incomplete once cleanup opens that.</summary>
        public Gate SendReturn { get; }

        /// <summary>Held inside the content's read-stream creation.</summary>
        public Gate StreamCreation { get; }

        /// <summary>Held inside the stream's read, AFTER the body-release gate.</summary>
        public Gate ReadFinish { get; }

        public void OpenAll()
        {
            Publication.Open();
            SendReturn.Open();
            StreamCreation.Open();
            ReadFinish.Open();
        }
    }

    /// <summary>
    /// An <see cref="HttpContent"/> whose stream does not terminate until an explicit gate is
    /// released. It signals both the stream's creation and the first actual read, so a headers-only
    /// caller can prove it received the response without the handler having buffered the body.
    /// Disposal is COUNTED (not just flagged), so "disposed exactly once" is assertable.
    /// </summary>
    private sealed class HeldStreamContent : HttpContent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HeldGates _gates;
        private int _disposeCount;
        private int _disposedStreamCount;
        private int _readCountBacking;

        public HeldStreamContent(string mediaType, HeldGates? gates = null)
        {
            _gates = gates ?? new HeldGates();
            Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many times this content's disposal ran.</summary>
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool Disposed => DisposeCount > 0;
        public bool IsReleased => _release.Task.IsCompletedSuccessfully;
        public int DisposedStreamCount => Volatile.Read(ref _disposedStreamCount);

        public void Release() => _release.TrySetResult();

        public int GetReadCount() => Volatile.Read(ref _readCountBacking);

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            Interlocked.Increment(ref _readCountBacking);
            await _gates.StreamCreation.Task;
            return new HeldStream(this);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => CreateContentReadStreamAsync();

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCountBacking);
            _gates.StreamCreation.Task.GetAwaiter().GetResult();
            return new HeldStream(this);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException("A held stream is never serialized by the bypass path.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            Release();
            base.Dispose(disposing);
        }

        private sealed class HeldStream(HeldStreamContent owner) : Stream
        {
            private bool _disposed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                owner.ReadStarted.TrySetResult();
                await owner._release.Task.WaitAsync(cancellationToken);
                // A SECOND, independent gate: opening the body gate alone does not finish the read.
                await owner._gates.ReadFinish.Task.WaitAsync(cancellationToken);
                return 0; // EOF only once both gates are open.
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                owner.ReadStarted.TrySetResult();
                owner._release.Task.GetAwaiter().GetResult();
                owner._gates.ReadFinish.Task.GetAwaiter().GetResult();
                return 0;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    Interlocked.Increment(ref owner._disposedStreamCount);
                }
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Terminal handler returning a successful, never-ending <c>text/event-stream</c> body and
    /// signalling once that response has entered the production chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ownership.</b> Once the terminal has allocated the response it OWNS it until it has
    /// successfully handed it to the caller. If the hand-off does not happen — cancellation or any
    /// other fault while the terminal is still parked — the terminal disposes the response itself
    /// before the failure propagates, exactly once, and rethrows the ORIGINAL exception. Nothing
    /// above the terminal ever saw the response, so nothing else can release it.
    /// </para>
    /// <para>
    /// <b>Release persists across publication.</b> A request to release the held body is remembered.
    /// If it arrives before the content exists, the content is released the moment it is published —
    /// before the response can reach any handler above — rather than the request being lost.
    /// </para>
    /// </remarks>
    private sealed class HeldEventStreamTerminalHandler : HttpMessageHandler
    {
        private readonly string _mediaType;
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowReturn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _publicationReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _publishLock = new();

        // Assigned only when the request actually reaches publication. A fault earlier in the chain
        // (or a cancelled/timed-out rendezvous) leaves it null, so cleanup must never read it
        // unguarded — a NullReferenceException there would mask the original failure.
        private HeldStreamContent? _content;
        private HttpResponseMessage? _response;
        private bool _releaseRequested;
        private int _transferred;
        private int _handoffCount;
        private int _releasedAtHandoff = -1;
        private int _releasedAtPublication = -1;
        private Exception? _observedFault;

        public HeldEventStreamTerminalHandler(string mediaType, HeldGates? gates = null)
        {
            _mediaType = mediaType;
            Gates = gates ?? new HeldGates();
        }

        public HeldGates Gates { get; }
        public Task Arrived => _arrived.Task;

        /// <summary>Completes when the terminal is parked at the publication gate (content not yet created).</summary>
        public Task PublicationReached => _publicationReached.Task;

        public int Attempts { get; private set; }

        /// <summary>The held content of the response. Valid only once the terminal published it.</summary>
        public HeldStreamContent Content =>
            Volatile.Read(ref _content)
            ?? throw new InvalidOperationException("The request never reached the terminal handler.");

        /// <summary>Null-safe accessor used by cleanup when the terminal may never have published.</summary>
        public HeldStreamContent? ContentOrNull => Volatile.Read(ref _content);

        /// <summary>Whether a response was allocated (it may or may not have been handed over).</summary>
        public bool ResponseAllocated => Volatile.Read(ref _response) is not null;

        /// <summary>Whether the response was successfully handed to the caller.</summary>
        public bool ResponseTransferred => Volatile.Read(ref _transferred) == 1;

        /// <summary>How many times a response left this terminal towards the handlers above it.</summary>
        public int HandoffCount => Volatile.Read(ref _handoffCount);

        /// <summary>
        /// Whether the held body was ALREADY released at the instant the response left this terminal
        /// (null: it never left). Recorded before the response is returned, so it is the state the
        /// first handler above observes — not a teardown-time state.
        /// </summary>
        public bool? ReleasedAtHandoff => Volatile.Read(ref _releasedAtHandoff) switch { -1 => null, var v => v == 1 };

        /// <summary>
        /// Whether the held body was ALREADY released at the instant it was published (null: never
        /// published). Recorded inside the publication lock, after any pending sticky release was
        /// applied — i.e. the state the response is created with, not a later state.
        /// </summary>
        public bool? ReleasedAtPublication => Volatile.Read(ref _releasedAtPublication) switch { -1 => null, var v => v == 1 };

        /// <summary>
        /// TEST SEAM. Invoked by <see cref="OpenAllGates"/> immediately AFTER every gate has been
        /// opened, on the calling thread. A blocking delegate here is a bounded rendezvous that lets
        /// the continuations the gates just released run to their next observation — the exact
        /// window in which a release recorded only AFTER the gates would be too late. It never
        /// changes what <see cref="OpenAllGates"/> does and is null outside the order probes.
        /// </summary>
        public Action? AfterGatesOpenedForTest { get; set; }

        /// <summary>The exception that interrupted the terminal while it owned an untransferred response.</summary>
        public Exception? ObservedFault => Volatile.Read(ref _observedFault);

        public bool ReleaseRequested
        {
            get { lock (_publishLock) { return _releaseRequested; } }
        }

        /// <summary>Alias used by probes that assert "no release was requested yet" before a fallback ran.</summary>
        public bool ReleaseRequestedBeforeFallbackForTest => ReleaseRequested;

        public void AllowReturn() => _allowReturn.TrySetResult();

        /// <summary>Whether the terminal's return gate has been released.</summary>
        public bool IsReturnAllowed => _allowReturn.Task.IsCompletedSuccessfully;

        /// <summary>
        /// Requests release of the held body. The request PERSISTS: if no content exists yet it is
        /// applied at publication. Safe to call at any time, any number of times.
        /// </summary>
        public void ReleaseHeldContent()
        {
            lock (_publishLock)
            {
                _releaseRequested = true;
                _content?.Release();
            }
        }

        /// <summary>
        /// Safety-net helper: opens every gate this terminal and its content wait on.
        /// </summary>
        /// <remarks>
        /// <b>Order is the contract.</b> The sticky release is recorded FIRST. Opening a gate lets a
        /// parked continuation run at once (on another thread), and that continuation may publish
        /// the content or hand the response off before this method's next statement. A release
        /// recorded after the gates therefore races those continuations, and a body handed off
        /// before it lands is observed unreleased by the handler above — which then needs the late
        /// disposal to ever finish. Recorded first, the release is applied at publication, or to the
        /// already-published content, before any gate can let either happen.
        /// </remarks>
        public void OpenAllGates()
        {
            ReleaseHeldContent();
            OpenGatesCore();
        }

        /// <summary>
        /// Opens every gate, then runs the window seam. The seam lives HERE, inside the gate-opening
        /// primitive, so whatever statement follows this call in a caller is — by construction —
        /// the "late" step the seam makes observable.
        /// </summary>
        private void OpenGatesCore()
        {
            Gates.OpenAll();
            AllowReturn();
            AfterGatesOpenedForTest?.Invoke();
        }

        /// <summary>
        /// Control ONLY: the pre-repair order (gates first, release last) over the very same
        /// <see cref="OpenGatesCore"/>. It exists so a row can show that the seam really does make
        /// the old interleaving observable; the safety net never calls it.
        /// </summary>
        public void OpenAllGatesLegacyOrderForTest()
        {
            OpenGatesCore();
            ReleaseHeldContent();
        }

        /// <summary>
        /// Safety-net helper: disposes the allocated response if (and only if) nothing disposed its
        /// content yet. Returns whether it had to — i.e. whether a leak was found.
        /// </summary>
        public bool DisposeAllocatedResponseIfLeaked()
        {
            var response = Volatile.Read(ref _response);
            var content = Volatile.Read(ref _content);
            if (response is null || content is null || content.DisposeCount != 0) return false;

            response.Dispose();
            return true;
        }

        private void Publish(HeldStreamContent content)
        {
            lock (_publishLock)
            {
                if (_releaseRequested) content.Release();
                Volatile.Write(ref _releasedAtPublication, content.IsReleased ? 1 : 0);
                // ContentOrNull is a lock-free reader. Publish the reference only after release.
                Volatile.Write(ref _content, content);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            // Park BEFORE the content exists, so a scenario can run cleanup while nothing is published.
            _publicationReached.TrySetResult();
            await Gates.Publication.Task.WaitAsync(ct);

            var content = new HeldStreamContent(_mediaType, Gates);
            Publish(content);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            };
            Volatile.Write(ref _response, response);

            // From here until the hand-off below the terminal owns the response.
            var transferred = false;
            try
            {
                // Park ONLY the terminal response, before it can travel back up the production chain.
                _arrived.TrySetResult();
                await _allowReturn.Task.WaitAsync(ct);
                await Gates.SendReturn.Task.WaitAsync(ct);

                Volatile.Write(ref _releasedAtHandoff, content.IsReleased ? 1 : 0);
                Interlocked.Increment(ref _handoffCount);
                Volatile.Write(ref _transferred, 1);
                transferred = true;
                return response;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _observedFault, ex);
                throw;
            }
            finally
            {
                if (!transferred) response.Dispose();
            }
        }
    }

    /// <summary>
    /// A handler placed directly ABOVE the held-SSE terminal. It records whether the held body was
    /// already released at the instant the response first reached a handler — the state every
    /// handler above the terminal actually sees, as opposed to a teardown-time state.
    /// </summary>
    private sealed class ResponseObserverHandler : DelegatingHandler
    {
        private int _seen = -1;
        private int _disposeCountWhenSeen = -1;
        private readonly TaskCompletionSource _seenSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResponseObserverHandler(HttpMessageHandler inner) : base(inner) { }

        /// <summary>Whether the body was already released when the response reached this handler.</summary>
        public bool ContentReleasedWhenSeen => Volatile.Read(ref _seen) == 1;

        /// <summary>Completes once the response has first reached this handler and its state was recorded.</summary>
        public Task Seen => _seenSignal.Task;

        /// <summary>How many times the content had been disposed when this handler first received it (-1: never).</summary>
        public int DisposeCountWhenSeen => Volatile.Read(ref _disposeCountWhenSeen);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.Content is HeldStreamContent held)
            {
                Volatile.Write(ref _seen, held.IsReleased ? 1 : 0);
                Volatile.Write(ref _disposeCountWhenSeen, held.DisposeCount);
                _seenSignal.TrySetResult();
            }
            return response;
        }
    }

    /// <summary>
    /// Fault-injecting handler placed BENEATH the held-SSE terminal: it throws before the terminal
    /// is ever entered, so that terminal's Content is never assigned. The real ResilienceHandler
    /// above it still retries the transient fault (1 + 3 attempts), which is asserted by the
    /// pre-terminal failure-path row.
    /// </summary>
    private sealed class PreTerminalFaultHandler : HttpMessageHandler
    {
        private readonly HeldEventStreamTerminalHandler _terminal;
        private readonly Exception _fault;

        public PreTerminalFaultHandler(HeldEventStreamTerminalHandler terminal, Exception fault)
        {
            _terminal = terminal;
            _fault = fault;
        }

        /// <summary>Number of attempts that reached this fault site.</summary>
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // The terminal is deliberately never invoked; the fault replaces it on every attempt.
            Attempts++;
            _ = _terminal;
            return Task.FromException<HttpResponseMessage>(_fault);
        }
    }

    /// <summary>
    /// Terminal handler answering every attempt with a <see cref="ReadTrackingContent"/> declared
    /// with an exact media-type spelling, under a configurable status.
    /// </summary>
    private sealed class EventStreamTerminalHandler : HttpMessageHandler
    {
        private readonly string _mediaType;
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public EventStreamTerminalHandler(string mediaType, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _mediaType = mediaType;
            _body = body;
            _status = status;
        }

        /// <summary>Number of attempts that reached the transport.</summary>
        public int Attempts { get; private set; }

        /// <summary>The content of the most recent response.</summary>
        public ReadTrackingContent Content { get; private set; } = null!;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;

            if (request.Content is not null)
                await request.Content.ReadAsStringAsync(ct);

            Content = new ReadTrackingContent(_mediaType, _body);
            return new HttpResponseMessage(_status) { RequestMessage = request, Content = Content };
        }
    }
}
