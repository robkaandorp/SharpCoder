using System.Net;
using System.Text.Json;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Construction, base-URI/bearer grammar, no-send credential/validation cases, and the exact
/// POST protocol + request-scoped Authorization behavior.
/// </summary>
public sealed class OllamaWebClientProtocolTests
{
    [Fact]
    public void WebToolErrorKind_HasExactlyTheSevenContractValues()
    {
        var names = Enum.GetNames<WebToolErrorKind>();
        Assert.Equal(7, names.Length);
        Assert.Equal(
            new[] { "Validation", "CredentialUnavailable", "HttpFailure", "TransportFailure", "MalformedResponse", "ResponseTooLarge", "Timeout" },
            names);
    }

    [Fact]
    public void EveryErrorKind_HasFixedSafeLibraryOwnedMessage()
    {
        foreach (var kind in Enum.GetValues<WebToolErrorKind>())
        {
            var message = WebToolErrorMessages.For(kind);
            Assert.False(string.IsNullOrEmpty(message));
            Assert.Same(message, WebToolErrorMessages.For(kind));
            Assert.DoesNotContain(kind.ToString(), message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretSentinel, message, StringComparison.Ordinal);
        }

        // Direct callers distinguish failures by enum even when display texts coincide.
        Assert.NotEqual(WebToolErrorKind.HttpFailure, WebToolErrorKind.TransportFailure);
        Assert.Equal(WebToolErrorMessages.For(WebToolErrorKind.HttpFailure), WebToolErrorMessages.For(WebToolErrorKind.TransportFailure));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/relative")]
    [InlineData("http://web.example.test")]
    [InlineData("ftp://web.example.test")]
    [InlineData("https://user:pass@web.example.test")]
    [InlineData("https://user@web.example.test")]
    [InlineData("https://@web.example.test")]
    [InlineData("https://@web.example.test/prefix")]
    [InlineData("https://web.example.test/api?q=1")]
    [InlineData("https://web.example.test/api?")]
    [InlineData("https://web.example.test/api#frag")]
    [InlineData("https://web.example.test/api#")]
    public void Constructor_InvalidBaseUri_ThrowsBeforeAnyUse(string invalid)
    {
        var handler = SearchOk();
        var options = Options(handler);
        options.BaseUri = new Uri(invalid, UriKind.RelativeOrAbsolute);
        Assert.Throws<ArgumentException>(() => Create(options));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void Constructor_NullBaseUri_Throws()
    {
        var options = Options(SearchOk());
        options.BaseUri = null!;
        Assert.Throws<ArgumentException>(() => Create(options));
    }

    [Fact]
    public void Constructor_MissingHostPieces_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new OllamaWebClient(null!));

        var noClient = Options(SearchOk());
        noClient.HttpClient = null!;
        Assert.Throws<ArgumentException>(() => Create(noClient));

        var noAccessor = Options(SearchOk());
        noAccessor.CredentialAccessor = null!;
        Assert.Throws<ArgumentException>(() => Create(noAccessor));
    }

    [Theory]
    [InlineData(Endpoint, "/api/web_search", "/api/web_fetch")]
    [InlineData("https://web.example.test/", "/api/web_search", "/api/web_fetch")]
    [InlineData("https://web.example.test/prefix", "/prefix/api/web_search", "/prefix/api/web_fetch")]
    [InlineData("https://web.example.test/prefix/", "/prefix/api/web_search", "/prefix/api/web_fetch")]
    [InlineData("https://web.example.test/prefix///", "/prefix/api/web_search", "/prefix/api/web_fetch")]
    [InlineData("https://web.example.test/a/b", "/a/b/api/web_search", "/a/b/api/web_fetch")]
    [InlineData("https://web.example.test:8443/p", "/p/api/web_search", "/p/api/web_fetch")]
    [InlineData("https://web.example.test/a%2Fb", "/a%2Fb/api/web_search", "/a%2Fb/api/web_fetch")]
    public async Task BaseUri_PathPrefix_ResolvesBothEndpointsBeneathExactlyOneSlash(
        string baseUri, string searchPath, string fetchPath)
    {
        var handler = FetchOk(); // handler returns {} for either call; only URIs are asserted here
        var client = Create(Options(handler, baseUri: baseUri));

        var search = await client.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        var fetch = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);

        // {} is a valid fetch but not a valid search, so search is MalformedResponse — the request
        // was still SENT, which is what this test establishes.
        AssertFailure(search, WebToolErrorKind.MalformedResponse);
        Assert.True(fetch.IsSuccess);
        Assert.Equal(2, handler.SendCount);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(searchPath, handler.Requests[0].AbsolutePath);
        Assert.Equal(fetchPath, handler.Requests[1].AbsolutePath);
        Assert.Equal("https", handler.Requests[0].Scheme);
        Assert.Equal("web.example.test", handler.Requests[0].Host);
    }

    [Theory]
    [InlineData("abcDEF0123456789-._~+/=")]
    [InlineData("  abcDEF  ")]
    [InlineData("\tabcDEF\t")]
    [InlineData(" \t abcDEF \t ")]
    [InlineData("A")]
    [InlineData("A=")]
    [InlineData("A==")]
    [InlineData("aaa===")]
    public async Task Bearer_B64TokenGrammar_AcceptedAndBoundarySpaceTabTrimmed(string raw)
    {
        var handler = FetchOk();
        var client = Create(Options(handler, accessor: () => raw));
        var result = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("Bearer " + raw.Trim(' ', '\t'), handler.Requests[0].Authorization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\t")]
    [InlineData(" \t ")]
    [InlineData("abc def")]
    [InlineData("abc\r\ndef")]
    [InlineData("abc\ndef")]
    [InlineData("abc\rdef")]
    [InlineData("abc\u00A0def")]
    [InlineData("abc\u2003")]
    [InlineData("abc!")]
    [InlineData("abc@def")]
    [InlineData("abc:def")]
    [InlineData("abc#def")]
    [InlineData("abc%2F")]
    [InlineData("abc\\def")]
    [InlineData("\"abc\"")]
    [InlineData("abc,def")]
    [InlineData("abc;def")]
    [InlineData("\u00E9abc")]
    [InlineData("=")]
    [InlineData("==")]
    [InlineData("=ZA")]
    [InlineData("A=Z")]
    [InlineData("A==Z")]
    [InlineData(" =Z ")]
    public async Task Bearer_MalformedOrBlank_ReturnsCredentialUnavailableWithoutSending(string raw)
    {
        var handler = FetchOk();
        var client = Create(Options(handler, accessor: () => raw));
        var result = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.CredentialUnavailable);
        AssertSafeMessage(result);
        Assert.Equal(0, handler.SendCount);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task CredentialAccessor_NullBlankOrThrowing_NoSendAndNoExceptionText(string? raw)
    {
        var handler = FetchOk();
        var client = Create(Options(handler, accessor: () => raw));
        var result = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.CredentialUnavailable);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task CredentialAccessor_ThrowsWithSecret_ReturnsCredentialUnavailableWithNoLeak()
    {
        var handler = FetchOk();
        var accessorCalls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            Interlocked.Increment(ref accessorCalls);
            throw new InvalidOperationException(SecretSentinel);
        }));

        var result = await client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.CredentialUnavailable);
        AssertSafeMessage(result);
        Assert.Equal(1, accessorCalls);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task CredentialAccessor_IsInvokedPerCallAndNeverGlobally()
    {
        var handler = FetchOk();
        var calls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            calls++;
            return Credential;
        }));
        await client.FetchAsync("https://a.example.test", TestContext.Current.CancellationToken);
        await client.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(2, handler.SendCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public async Task BlankQuery_ReturnsValidationBeforeCredentialOrTransport(string? query)
    {
        var handler = SearchOk();
        var accessorCalls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            accessorCalls++;
            return Credential;
        }));
        var result = await client.SearchAsync(query, cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Validation);
        AssertSafeMessage(result);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, accessorCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public async Task BlankUrl_ReturnsValidationBeforeCredentialOrTransport(string? url)
    {
        var handler = FetchOk();
        var accessorCalls = 0;
        var client = Create(Options(handler, accessor: () =>
        {
            accessorCalls++;
            return Credential;
        }));
        var result = await client.FetchAsync(url, TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Validation);
        AssertSafeMessage(result);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, accessorCalls);
    }

    [Fact]
    public async Task Search_PostsExactlyQueryAndMaxResults_AndRequestScopedBearerOnly()
    {
        var handler = SearchOk();
        var client = Create(Options(handler));
        var result = await client.SearchAsync("hello \"world\" \u00E9", 3, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, handler.SendCount);
        var request = handler.Requests[0];

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/web_search", request.AbsolutePath);
        Assert.Equal("Bearer " + Credential, request.Authorization);
        Assert.Equal(new[] { "Bearer " + Credential }, request.AuthorizationValues);
        Assert.Equal("INHERITED_SENTINEL", request.InheritedMarker); // other inherited headers survive
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        // A typed inherited Authorization is present on the host client. The recorded request must
        // carry ONLY the request-scoped value (one value, exact spelling), never the inherited one.
        Assert.DoesNotContain("INHERITED", request.Authorization, StringComparison.Ordinal);
        Assert.Single(request.AuthorizationValues);

        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count()); // property count, not a collection-size assertion
        Assert.Equal("hello \"world\" \u00E9", root.GetProperty("query").GetString());
        Assert.Equal(3, root.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task InheritedAuthorization_IsReplacedByRequestScopedBearerOnly_PositiveControl()
    {
        var handler = FetchOk();
        var options = Options(handler);

        // POSITIVE CONTROL: with no request-scoped Authorization, HttpClient really does merge the
        // host client's inherited default values. This proves the "no inherited value" assertion
        // below is not passable on a client that never had an inherited header at all.
        using (var probe = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "api/web_fetch")))
        using (var probeResponse = await options.HttpClient.SendAsync(
                   probe,
                   HttpCompletionOption.ResponseHeadersRead,
                   TestContext.Current.CancellationToken))
        {
            var inherited = handler.Requests[0].AuthorizationValues;
            Assert.NotEmpty(inherited);
            Assert.Contains(inherited, value => value.Contains("INHERITED_SENTINEL", StringComparison.Ordinal));
        }

        var result = await Create(options).FetchAsync(
            "https://page.example.test", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var production = handler.Requests[1];
        Assert.Equal(new[] { "Bearer " + Credential }, production.AuthorizationValues);
        Assert.All(production.AuthorizationValues, value =>
            Assert.DoesNotContain("INHERITED", value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_PostsExactlyUrl_NoMaxLines_AndRequestScopedBearerOnly()
    {
        var handler = FetchOk();
        var client = Create(Options(handler));
        var result = await client.FetchAsync("https://page.example.test/a?b=1#c", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/web_fetch", request.AbsolutePath);
        Assert.Equal("Bearer " + Credential, request.Authorization);
        Assert.Equal(new[] { "Bearer " + Credential }, request.AuthorizationValues);
        Assert.Single(request.AuthorizationValues);
        Assert.DoesNotContain("INHERITED", request.Authorization, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("https://page.example.test/a?b=1#c", json.RootElement.GetProperty("url").GetString());
        Assert.DoesNotContain("max_lines", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("INHERITED", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-100, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(1000, 10)]
    [InlineData(int.MaxValue, 10)]
    public async Task Search_MaxResults_NonpositiveMapsToOneAndAboveTenClamps(int supplied, int expected)
    {
        var handler = SearchOk();
        var client = Create(Options(handler));
        var result = await client.SearchAsync("q", supplied, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(expected, json.RootElement.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task Search_DefaultMaxResults_IsFive()
    {
        var handler = SearchOk();
        var client = Create(Options(handler));
        await client.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(5, json.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal(5, OllamaWebClient.DefaultMaxResults);
        Assert.Equal(10, OllamaWebClient.MaximumMaxResults);
    }

    [Fact]
    public async Task Search_RetainsEveryValidUpstreamEntryUntruncated()
    {
        var longContent = new string('Z', 20_000);
        var body = "{\"results\":[" +
                   "{\"title\":\"T1\",\"url\":\"U1\",\"content\":\"" + longContent + "\"}," +
                   "{\"title\":\"T2\",\"url\":\"U2\",\"content\":\"C2\"}," +
                   "{\"title\":\"T3\",\"url\":\"U3\",\"content\":\"C3\"}]}";
        var programmable = RecordingWebHandler.Json(body);
        var client = Create(Options(programmable));

        var result = await client.SearchAsync("q", 1, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var data = Assert.IsType<WebSearchData>(result.GetRawData());
        Assert.Equal(3, data.GetRawResults().Count); // count selection is the LATER adapter's job
        Assert.Equal(longContent, data.GetRawResults()[0].GetRawContent());
        Assert.Equal("T2", data.GetRawResults()[1].GetRawTitle());
        Assert.Equal("U3", data.GetRawResults()[2].GetRawUrl());
        Assert.Equal(string.Empty, result.DisplayMessage);
        Assert.Null(result.ErrorKind);
    }

    [Fact]
    public void SuccessfulDtos_AreHostOnly_NoRawContentInFormattingOrSerialization()
    {
        var search = new WebSearchResult(SecretSentinel, "u", "c");
        var data = new WebSearchData([search]);
        var fetch = new WebFetchData(SecretSentinel, SecretSentinel, [SecretSentinel]);
        var result = WebToolResult<WebSearchData>.Success(data);

        Assert.DoesNotContain(SecretSentinel, search.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, data.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, fetch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, JsonSerializer.Serialize(search), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, JsonSerializer.Serialize(data), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, JsonSerializer.Serialize(fetch), StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.DisplayMessage);
    }
}
