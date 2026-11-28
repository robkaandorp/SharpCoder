using System.Net;
using System.Text;
using System.Text.Json;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>Named regression rows for the iteration-1 review repairs.</summary>
public sealed class WebRepairContractTests
{
    [Fact]
    public void RawDtoSentinels_AreAbsentFromResultAndPayloadPublicPropertyTraversal()
    {
        var search = new WebSearchResult(SecretSentinel, "url", "content");
        var searchResult = WebToolResult<WebSearchData>.Success(new WebSearchData([search]));
        var fetchResult = WebToolResult<WebFetchData>.Success(
            new WebFetchData(SecretSentinel, "content", [SecretSentinel]));

        var traversed = PublicPropertyTraversal.SerializePublicProperties(searchResult) +
                        PublicPropertyTraversal.SerializePublicProperties(fetchResult);
        Assert.DoesNotContain(SecretSentinel, traversed, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, JsonSerializer.Serialize(searchResult), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, JsonSerializer.Serialize(fetchResult), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, searchResult.ToString(), StringComparison.Ordinal);

        // Explicit host access still returns COMPLETE untruncated values.
        Assert.Same(search, Assert.Single(searchResult.GetRawData()!.GetRawResults()));
        Assert.Equal(SecretSentinel, search.GetRawTitle());
        Assert.Equal(SecretSentinel, fetchResult.GetRawData()!.GetRawTitle());
        Assert.Equal(SecretSentinel, Assert.Single(fetchResult.GetRawData()!.GetRawLinks()));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("url")]
    [InlineData("content")]
    public async Task Search_UnpairedEscapedSurrogate_ReturnsMalformedResponse(string field)
    {
        var json = "{\"results\":[{\"" + field + "\":\"\\uD800\"}]}";
        var result = await Create(Options(RecordingWebHandler.Json(json)))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
        AssertSafeMessage(result);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("content")]
    public async Task Fetch_UnpairedEscapedSurrogate_ReturnsMalformedResponse(string field)
    {
        var json = "{\"" + field + "\":\"\\uDFFF\"}";
        var result = await Create(Options(RecordingWebHandler.Json(json)))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task Fetch_LinksWithUnpairedEscapedSurrogate_ReturnsMalformedResponse()
    {
        var result = await Create(Options(RecordingWebHandler.Json("{\"links\":[\"\\uD800\"]}")))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task Search_InvalidUtf8InsideQuotedContent_ReturnsMalformedResponse()
    {
        var body = new byte[] { (byte)'{', (byte)'\"', (byte)'r', (byte)'e', (byte)'s', (byte)'u', (byte)'l', (byte)'t', (byte)'s', (byte)'\"', (byte)':', (byte)'[', (byte)'{', (byte)'\"', (byte)'c', (byte)'o', (byte)'n', (byte)'t', (byte)'e', (byte)'n', (byte)'t', (byte)'\"', (byte)':', (byte)'\"', 0xFF, (byte)'\"', (byte)'}', (byte)']', (byte)'}' };
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new ByteArrayContent(body));
        var result = await Create(Options(handler))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task Fetch_InvalidUtf8InsideQuotedTitle_ReturnsMalformedResponse()
    {
        var body = new byte[] { (byte)'{', (byte)'\"', (byte)'t', (byte)'i', (byte)'t', (byte)'l', (byte)'e', (byte)'\"', (byte)':', (byte)'\"', 0xC0, 0xAF, (byte)'\"', (byte)'}' };
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new ByteArrayContent(body));
        var result = await Create(Options(handler))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task ValidUnicode_PreservesEveryAffectedFieldFamily()
    {
        const string unicode = "A\u00E9\U0001F600\u4E2D";
        var search = await Create(Options(RecordingWebHandler.Json(
                "{\"results\":[{\"title\":\"" + unicode + "\",\"url\":\"" + unicode + "\",\"content\":\"" + unicode + "\"}]}")))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        var row = Assert.Single(search.GetRawData()!.GetRawResults());
        Assert.Equal(unicode, row.GetRawTitle());
        Assert.Equal(unicode, row.GetRawUrl());
        Assert.Equal(unicode, row.GetRawContent());

        var fetch = await Create(Options(RecordingWebHandler.Json(
                "{\"title\":\"" + unicode + "\",\"content\":\"" + unicode + "\",\"links\":[\"" + unicode + "\"]}")))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        Assert.Equal(unicode, fetch.GetRawData()!.GetRawTitle());
        Assert.Equal(unicode, fetch.GetRawData()!.GetRawContent());
        Assert.Equal(unicode, Assert.Single(fetch.GetRawData()!.GetRawLinks()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("=")]
    public async Task CallerCancellation_AfterEveryUnusableCredentialOutcome_Propagates(string? credential)
    {
        using var caller = new CancellationTokenSource();
        var handler = FetchOk();
        var client = Create(Options(handler, accessor: () =>
        {
            caller.Cancel();
            return credential;
        }));
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync("https://page.example.test", caller.Token));
        Assert.Equal(caller.Token, thrown.CancellationToken);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task CallerCancellation_AfterThrowingAccessor_PropagatesWithoutSecret()
    {
        using var caller = new CancellationTokenSource();
        var handler = FetchOk();
        var client = Create(Options(handler, accessor: () =>
        {
            caller.Cancel();
            throw new InvalidOperationException(SecretSentinel);
        }));
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync("https://page.example.test", caller.Token));
        Assert.Equal(caller.Token, thrown.CancellationToken);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task TimerCallback_ContainsRegistrationAggregateFaults_AndStillDisposesTimer()
    {
        var clock = new ManualWebTimeProvider();
        var handler = new GatedWebHandler();
        var client = Create(Options(handler, timeProvider: clock));
        var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        await handler.WaitForEntryAsync("timer callback containment");
        var timer = await clock.WaitForTimerAsync();
        // Register the throwing callback on the timer's CTS while it is still owned by production.
        var state = Assert.IsType<CancellationTokenSource>(timer.State);
        state.Token.Register(() => throw new InvalidOperationException(SecretSentinel));

        timer.Fire(); // Callback must not throw AggregateException back into the host.
        Assert.True(timer.Fired);
        handler.ReleaseWithFallback(); // release after firing, then join the parked send
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        Assert.True(timer.Disposed);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("content")]
    [InlineData("stream")]
    public async Task CleanupFault_DoesNotReplaceHttpFailure_AndEveryResourceIsReleased(string thrower)
    {
        var resources = new CleanupProbeResources();
        var handler = resources.CreateGatedHandler(HttpStatusCode.ServiceUnavailable);
        var client = Create(Options(handler));
        var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        await handler.WaitForEntryAsync("cleanup fault");
        switch (thrower)
        {
            case "response": resources.Response!.ThrowOnDispose = true; break;
            case "content": resources.Content!.ThrowOnDispose = true; break;
            default: resources.Stream!.ThrowOnDispose = true; break;
        }
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.HttpFailure);
        AssertSafeMessage(result);
        Assert.Equal(1, resources.Response!.DisposeCalls);
        Assert.Equal(1, resources.Content!.DisposeCalls);
        Assert.Equal(1, resources.Stream!.DisposeCalls);
        Assert.Equal(new[] { "response-start", "content", "stream" }, resources.Releases);
    }

    [Fact]
    public async Task CleanupFault_DoesNotReplaceSuccess_OrCallerCancellation()
    {
        var resources = new CleanupProbeResources();
        var handler = resources.CreateGatedHandler(body: "{\"title\":\"T\"}");
        var client = Create(Options(handler));
        var call = client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        await handler.WaitForEntryAsync("success cleanup");
        resources.Response!.ThrowOnDispose = true;
        resources.Content!.ThrowOnDispose = true;
        resources.Stream!.ThrowOnDispose = true;
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("T", result.GetRawData()!.GetRawTitle());
        Assert.Equal(1, resources.Stream!.DisposeCalls);
        Assert.Equal(1, resources.Content!.DisposeCalls);
        Assert.Equal(1, resources.Response!.DisposeCalls);
        Assert.Equal(new[] { "response-start", "content", "stream" }, resources.Releases);
    }
}
