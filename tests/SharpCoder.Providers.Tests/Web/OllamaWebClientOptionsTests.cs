using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Option defaults/boundaries, deterministic disposal, caller cancellation before transport, and the
/// iteration-1 confirmation of the future registered-invocation design.
/// </summary>
public sealed class OllamaWebClientOptionsTests
{
    [Fact]
    public void Options_Defaults_AreExactlyContractValues()
    {
        var options = new OllamaWebClientOptions();
        Assert.Equal(1024 * 1024, options.MaxResponseBytes);
        Assert.Equal(1024 * 1024, OllamaWebClientOptions.DefaultMaxResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(15), OllamaWebClientOptions.DefaultDeadline);
        Assert.Equal(TimeSpan.FromSeconds(1), OllamaWebClientOptions.MinimumDeadline);
        Assert.Equal(TimeSpan.FromSeconds(300), OllamaWebClientOptions.MaximumDeadline);
        Assert.Null(options.TimeProvider);
        Assert.Equal(5, OllamaWebClient.DefaultMaxResults);
        Assert.Equal(10, OllamaWebClient.MaximumMaxResults);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1024)]
    [InlineData(1024 * 1024)]
    [InlineData(int.MaxValue)]
    public void Constructor_PositiveResponseCap_Accepted(int cap)
    {
        var options = Options(SearchOk(), maxResponseBytes: cap);
        Assert.IsType<OllamaWebClient>(Create(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Constructor_NonPositiveResponseCap_ThrowsBeforeAnyUse(int cap)
    {
        var handler = SearchOk();
        var options = Options(handler, maxResponseBytes: cap);
        Assert.Throws<ArgumentException>(() => Create(options));
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData(1)]    // minimum inclusive
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(299)]
    [InlineData(300)] // maximum inclusive
    public void Constructor_FiniteDeadlineWithinBounds_Accepted(int seconds)
    {
        var options = Options(SearchOk(), deadline: TimeSpan.FromSeconds(seconds));
        Assert.IsType<OllamaWebClient>(Create(options));
    }

    [Fact]
    public void Constructor_InfiniteDeadline_AcceptedAndIsTheOnlyNonFiniteValue()
    {
        var options = Options(SearchOk(), deadline: Timeout.InfiniteTimeSpan);
        Assert.IsType<OllamaWebClient>(Create(options));
        Assert.Equal(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(301)]
    [InlineData(1000)]
    public void Constructor_FiniteDeadlineOutsideBounds_Throws(int seconds)
    {
        var options = Options(SearchOk(), deadline: TimeSpan.FromSeconds(seconds));
        Assert.Throws<ArgumentException>(() => Create(options));
    }

    [Fact]
    public void Constructor_NonFiniteNonInfiniteDeadline_Throws()
    {
        var options = Options(SearchOk(), deadline: TimeSpan.FromMilliseconds(-2));
        Assert.Throws<ArgumentException>(() => Create(options));
    }

    [Fact]
    public async Task HostSuppliedTimeProvider_IsUsedForDeadlineRegistration()
    {
        var clock = new ManualWebTimeProvider();
        var handler = FetchOk();
        var options = Options(handler, timeProvider: clock, deadline: TimeSpan.FromSeconds(42));
        var client = Create(options);
        var result = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var timer = Assert.Single(clock.RegisteredTimers);
        Assert.Equal(TimeSpan.FromSeconds(42), timer.DueTime);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task HttpClientOwnership_RemainsWithHostAfterClientUse()
    {
        var handler = FetchOk();
        var options = Options(handler);
        var client = Create(options);
        await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);

        // The host's HttpClient is still usable: this library never disposes it.
        var again = await options.HttpClient.GetAsync("https://web.example.test/probe", TestContext.Current.CancellationToken);
        again.Dispose();
        Assert.Equal(2, handler.SendCount);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task RequestResponseContentAndTimer_AreDisposedOnEverySuccessAndFailurePath()
    {
        // Success path: response + content + timer disposed.
        var clock = new ManualWebTimeProvider();
        var stream = new ChunkedWebStream(Encoding.UTF8.GetBytes("{}"), chunkSize: 1);
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream));
        var ok = await Create(Options(handler, timeProvider: clock))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        Assert.True(ok.IsSuccess);
        Assert.True(stream.Disposed);
        Assert.All(clock.RegisteredTimers, t => Assert.True(t.Disposed));

        // Non-success path: content disposed without being read.
        var neverRead = new NeverReadContent(SecretSentinel);
        var badHandler = RecordingWebHandler.With(HttpStatusCode.Forbidden, neverRead);
        var bad = await Create(Options(badHandler))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(bad, WebToolErrorKind.HttpFailure);
        Assert.Equal(0, neverRead.ReadAttempts);
        Assert.True(neverRead.Disposed);

        // Transport-fault path: timer disposed even though no response exists.
        var clock2 = new ManualWebTimeProvider();
        var throwHandler = new ThrowingWebHandler(new IOException(SecretSentinel));
        var failed = await Create(Options(throwHandler, timeProvider: clock2))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(failed, WebToolErrorKind.TransportFailure);
        Assert.All(clock2.RegisteredTimers, t => Assert.True(t.Disposed));

        // No-send paths: no timer is ever registered, nothing to dispose.
        var clock3 = new ManualWebTimeProvider();
        var noSendHandler = FetchOk();
        var noSend = Create(Options(noSendHandler, timeProvider: clock3, accessor: () => null));
        var validation = await noSend.SearchAsync("  ", cancellationToken: TestContext.Current.CancellationToken);
        var credential = await noSend.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(validation, WebToolErrorKind.Validation);
        AssertFailure(credential, WebToolErrorKind.CredentialUnavailable);
        Assert.Empty(clock3.RegisteredTimers);
        Assert.Equal(0, noSendHandler.SendCount);
    }

    [Fact]
    public async Task CallerCancellation_BeforeAnyWork_PropagatesWithoutCredentialOrSend()
    {
        var handler = FetchOk();
        var accessorCalls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            accessorCalls++;
            return Credential;
        }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync("https://page.example.test", cts.Token));
        Assert.Equal(0, accessorCalls);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task EveryFailureKind_IsReachableAndHasItsOwnFixedSafeMessage()
    {
        // Validation
        var blank = await Create(Options(SearchOk())).SearchAsync(" ", cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(blank, WebToolErrorKind.Validation);

        // CredentialUnavailable
        var noCred = await Create(Options(FetchOk(), accessor: () => null))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(noCred, WebToolErrorKind.CredentialUnavailable);

        // HttpFailure
        var http = await Create(Options(RecordingWebHandler.With(HttpStatusCode.BadGateway, JsonBody("{}"))))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(http, WebToolErrorKind.HttpFailure);

        // TransportFailure
        var transport = await Create(Options(new ThrowingWebHandler(new IOException(SecretSentinel))))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(transport, WebToolErrorKind.TransportFailure);

        // MalformedResponse
        var malformed = await Create(Options(RecordingWebHandler.Json("not-json")))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(malformed, WebToolErrorKind.MalformedResponse);

        // ResponseTooLarge
        var tooLarge = await Create(Options(RecordingWebHandler.Json("{}"), maxResponseBytes: 1))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(tooLarge, WebToolErrorKind.ResponseTooLarge);

        // Timeout
        var timeout = await Create(Options(new ThrowingWebHandler(new WebToolTimeoutException(SecretSentinel))))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(timeout, WebToolErrorKind.Timeout);

        foreach (var result in new object[] { blank, noCred, http, transport, malformed, tooLarge, timeout })
        {
            var kind = (WebToolErrorKind?)result.GetType().GetProperty("ErrorKind")!.GetValue(result)!;
            Assert.NotNull(kind);
            var message = (string)result.GetType().GetProperty("DisplayMessage")!.GetValue(result)!;
            Assert.Equal(WebToolErrorMessages.For(kind.Value), message);
            Assert.DoesNotContain(SecretSentinel, message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ITERATION 1 DESIGN CONFIRMATION (explicitly NOT an implementation): the future registered
    /// invocation boundary is a Providers-owned <see cref="AIFunction"/> decorator following
    /// SharpCoder's <c>ToolAuthorizationGuard.AuthorizedAIFunction</c> precedent. It wraps the INNER
    /// function's <c>InvokeAsync</c>, preserves all schema metadata, propagates caller cancellation,
    /// and returns the fixed <c>Unexpected tool failure.</c> for unexpected binding/invocation faults.
    /// It is NOT an HTTP/client abstraction and requires NO SharpCoder core reference:
    /// <see cref="AIFunctionFactory"/> / <see cref="AIFunction"/> already ship with this project's
    /// existing Microsoft.Extensions.AI dependency. Iteration 3 will implement and verify it.
    /// </summary>
    [Fact]
    public void FutureInvocationDesign_ProvidersOwnedAIFunctionDecorator_IsFeasibleWithoutCoreReference()
    {
        var referenced = typeof(OllamaWebClient).Assembly.GetReferencedAssemblies().Select(n => n.Name).ToArray();
        Assert.Contains("Microsoft.Extensions.AI.Abstractions", referenced);
        Assert.DoesNotContain("SharpCoder", referenced);

        // AIFunction is the decorator base type; AIFunctionFactory.Create produces an inner function
        // whose InvokeAsync can be wrapped by a decorator with the same schema metadata.
        var inner = AIFunctionFactory.Create(
            (string query) => "ok",
            "probe",
            "Design-confirmation probe only; never registered in this iteration.");
        Assert.IsAssignableFrom<AIFunction>(inner);
        Assert.Equal("probe", inner.Name);
        Assert.False(string.IsNullOrEmpty(inner.Description));
        Assert.True(inner.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Object);

        // Schema-metadata surface that the future decorator must forward unchanged.
        _ = inner.Name;
        _ = inner.Description;
        _ = inner.JsonSchema;
        _ = inner.ReturnJsonSchema;
        _ = inner.UnderlyingMethod;
        _ = inner.JsonSerializerOptions;
        _ = inner.AdditionalProperties;
        Assert.Equal("Unexpected tool failure.", WebInvocationDesignNotes.FixedFallback);
    }
}

/// <summary>
/// Iteration-1-only design notes for the deferred registered invocation boundary. No behavior is
/// implemented here: iteration 3 adds the real decorator and its security matrix.
/// </summary>
internal static class WebInvocationDesignNotes
{
    internal const string FixedFallback = "Unexpected tool failure.";

    // The decorator contract (deferred, iteration 3):
    //   * Providers-owned subtype of Microsoft.Extensions.AI.AIFunction.
    //   * InvokeCoreAsync wraps inner.InvokeAsync(AIFunctionArguments, CancellationToken).
    //   * Forwards Name, Description, JsonSchema, ReturnJsonSchema, UnderlyingMethod,
    //     JsonSerializerOptions, AdditionalProperties, GetService, ToString.
    //   * Caller OperationCanceledException propagates unchanged (never a fallback).
    //   * Every other unexpected binding/invocation fault returns exactly FixedFallback;
    //     no dynamic type/message/log text is ever included.
    //   * It is NOT an HTTP/client abstraction. No SharpCoder core reference is added.
}
