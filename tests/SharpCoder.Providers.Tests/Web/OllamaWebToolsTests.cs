using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;
using static SharpCoder.Providers.Tests.Web.OllamaWebToolsTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>Adapter, mandatory transform, selection, redaction and cancellation contracts.</summary>
public sealed class OllamaWebToolsTests
{
    [Fact]
    public void Constructor_RequiresTransformAndEnforcesDisplayAndFetchCaps()
    {
        var client = new OllamaWebClient(Options(FetchOk()));
        Assert.Throws<ArgumentNullException>(() => new OllamaWebTools(null!, ValidOptions()));
        Assert.Throws<ArgumentNullException>(() => new OllamaWebTools(client, null!));
        Assert.Throws<ArgumentNullException>(() => new OllamaWebTools(client, new OllamaWebToolsOptions()));
        Assert.Throws<ArgumentException>(() => new OllamaWebTools(client, ValidOptions(OllamaWebTools.MinimumDisplayBytes - 1)));
        var minimum = new OllamaWebTools(client, ValidOptions(OllamaWebTools.MinimumDisplayBytes));
        Assert.Equal(2, minimum.Tools.Count);
        foreach (var invalid in new[] { 0, -1, 1001 })
            Assert.Throws<ArgumentException>(() => new OllamaWebTools(client, ValidOptions(maxFetchLines: invalid)));
    }

    [Fact]
    public async Task Search_TransformsSelectedFieldsBeforeRendering_AndOmitsUpstreamEntries()
    {
        var seen = new List<string>();
        var handler = SearchOk("[" + string.Join(",", Enumerable.Range(1, 6).Select(i =>
            "{\"title\":\"T" + i + "\",\"url\":\"U" + i + "\",\"content\":\"C" + i + "\"}")) + "]");
        var tools = Tools(handler, async (value, token) =>
        {
            await Task.Yield();
            seen.Add(value);
            return "X" + value;
        });
        var output = await tools.SearchAsync("q", 5, TestContext.Current.CancellationToken);
        Assert.Equal("Title: XT1\nURL: XU1\nContent: XC1\n\nTitle: XT2\nURL: XU2\nContent: XC2\n\nTitle: XT3\nURL: XU3\nContent: XC3\n\nTitle: XT4\nURL: XU4\nContent: XC4\n\nTitle: XT5\nURL: XU5\nContent: XC5\n\nRESULTS-OMITTED", output);
        Assert.Equal(new[] { "T1", "U1", "C1", "T2", "U2", "C2", "T3", "U3", "C3", "T4", "U4", "C4", "T5", "U5", "C5" }, seen);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("5", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("max_results").GetRawText());
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(99, 10)]
    public async Task Search_CountRules_SelectAndRequestClampedValues(int? requested, int expected)
    {
        var handler = SearchOk();
        var tools = Tools(handler);
        Assert.Equal("No results found.", requested is null
            ? await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken)
            : await tools.SearchAsync("q", requested.Value, TestContext.Current.CancellationToken));
        Assert.Equal(expected, JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task Search_CombinedNoticeAtPublicMinimumCap_IsExact()
    {
        var tools = Tools(SearchOk("[{\"title\":\"" + new string('T', 100) + "\"},{\"title\":\"later\"}]"),
            displayBytes: OllamaWebTools.MinimumDisplayBytes);
        Assert.Equal("RESULTS-OMITTED\n\nTRUNCATED",
            await tools.SearchAsync("q", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_TransformsWholeContentBeforeRuneClip_PreservingSurrogatePairs()
    {
        var input = new string('A', 499) + SecretSentinel + "😀";
        var seen = new List<string>();
        var handler = SearchOk("[{\"title\":\"T\",\"url\":\"U\",\"content\":\"" + input + "\"}]");
        var tools = Tools(handler, (value, _) =>
        {
            seen.Add(value);
            return Task.FromResult<string?>(value == input ? new string('A', 499) + "😀" : value);
        });
        var output = await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        var snippet = Assert.Single(output.Split('\n'), line => line.StartsWith("Content: ", StringComparison.Ordinal))["Content: ".Length..];
        Assert.Equal(500, snippet.EnumerateRunes().Count());
        Assert.EndsWith("😀", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, snippet, StringComparison.Ordinal);
        Assert.Equal(input, Assert.Single(seen, value => value.Contains(SecretSentinel, StringComparison.Ordinal)));
        var control = await Tools(SearchOk("[{\"title\":\"T\",\"url\":\"U\",\"content\":\"" + new string('B', 501) + "\"}]"))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.EndsWith("Content: " + new string('B', 500), control, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_Golden_TransformsEveryFieldAndPreservesRequestUrlBeforeTransform()
    {
        var seen = new List<string>();
        var handler = FetchOk("{\"title\":\"T\",\"content\":\"C\",\"links\":[\"L\"]}");
        var tools = Tools(handler, (value, _) =>
        {
            seen.Add(value);
            return Task.FromResult<string?>(value.ToUpperInvariant());
        });
        var output = await tools.FetchAsync("https://page.example.test/private", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Title: T\nContent: C\nLinks:\nL", output);
        Assert.Equal(new[] { "T", "C", "L" }, seen);
        Assert.Equal("https://page.example.test/private", JsonDocument.Parse(handler.Requests[0].Body).RootElement.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(1001, 1000)]
    public async Task Fetch_DefaultAndClampedLineLimits_AreApplied(int? requested, int expected)
    {
        var handler = FetchOk("{\"title\":\"T\",\"content\":\"" + string.Join("\\n", Enumerable.Range(0, 1000)) + "\"}");
        var output = await Tools(handler).FetchAsync("https://page.example.test", requested, TestContext.Current.CancellationToken);
        Assert.True(output.Length < 10000, output.Length.ToString());
        Assert.Equal(expected, output.Split('\n').Length); // retained content + separator + notice
        Assert.EndsWith("\nTRUNCATED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Fetch_NonpositiveLines_ReturnsValidationWithoutCredentialOrTransport(int requested)
    {
        var handler = FetchOk();
        var seen = new List<string>();
        var accessorCalls = 0;
        var tools = Tools(handler, (value, _) => { seen.Add(value); return Task.FromResult<string?>(value); },
            accessor: () => { accessorCalls++; return Credential; });
        Assert.Equal(WebToolErrorMessages.Validation,
            await tools.FetchAsync("https://page.example.test", requested, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, accessorCalls);
        Assert.Equal(WebToolErrorMessages.Validation, Assert.Single(seen));
    }

    [Fact]
    public async Task Fetch_ExplicitAboveHostCap_UsesHostCapRatherThanGlobalMaximum()
    {
        var content = string.Join("\\n", Enumerable.Range(0, 20));
        var tools = Tools(FetchOk("{\"title\":\"T\",\"content\":\"" + content + "\"}"), maxFetchLines: 7);
        var output = await tools.FetchAsync("https://page.example.test", 999, TestContext.Current.CancellationToken);
        Assert.Equal(7, output.Split('\n').Length);
        Assert.Equal("Title: T\nContent: 0\n1\n2\n3\n\nTRUNCATED", output);
    }

    [Fact]
    public async Task BackendInputs_AreSentBeforeTransform_WithViableQueryAndUrlControls()
    {
        var search = SearchOk("[{\"title\":\"T\"}]");
        var searchTransformCalls = 0;
        var searchTools = Tools(search, (value, _) =>
        {
            searchTransformCalls++;
            Assert.Equal(1, search.SendCount);
            return Task.FromResult<string?>(value);
        });
        await searchTools.SearchAsync("query+with spaces", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("query+with spaces", JsonDocument.Parse(search.Requests[0].Body).RootElement.GetProperty("query").GetString());
        Assert.Equal(3, searchTransformCalls);

        var fetch = FetchOk("{\"title\":\"T\"}");
        var fetchTransformCalls = 0;
        var fetchTools = Tools(fetch, (value, _) =>
        {
            fetchTransformCalls++;
            Assert.Equal(1, fetch.SendCount);
            return Task.FromResult<string?>(value);
        });
        await fetchTools.FetchAsync("https://page.example.test/a b?q=x y", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://page.example.test/a b?q=x y", JsonDocument.Parse(fetch.Requests[0].Body).RootElement.GetProperty("url").GetString());
        Assert.Equal(2, fetchTransformCalls);
    }

    [Fact]
    public async Task StructuralEmptyPage_IsDetectedBeforeTransform_AndRedactsToEmpty()
    {
        var seen = new List<string>();
        var tools = Tools(FetchOk("{\"title\":null,\"content\":\"\",\"links\":[null,3]}"),
            (value, _) => { seen.Add(value); return Task.FromResult<string?>(" \n "); });
        Assert.Equal(string.Empty, await tools.FetchAsync("https://page.example.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(WebToolDisplayLiterals.EmptyPage, Assert.Single(seen));
    }

    [Fact]
    public async Task TransformEmptiedOrdinaryPage_IsNotStructuralEmpty_AndRetainsGrammar()
    {
        var seen = new List<string>();
        var tools = Tools(FetchOk("{\"title\":\" \",\"content\":\" \",\"links\":[]}"),
            (value, _) => { seen.Add(value); return Task.FromResult<string?>(string.Empty); });
        Assert.Equal("Title: \nContent: ", await tools.FetchAsync("https://page.example.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new[] { " ", " " }, seen);
        Assert.DoesNotContain(WebToolDisplayLiterals.EmptyPage, seen);
    }

    [Fact]
    public async Task SuccessfulNoResults_RedactsToEmpty_WhileTypedFailureUsesToolRequestFailed()
    {
        var success = Tools(SearchOk(), (_, _) => Task.FromResult<string?>("\r\n "));
        Assert.Equal(string.Empty, await success.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));

        var failed = Tools(SearchOk(), (_, _) => Task.FromResult<string?>("\r\n "), accessor: () => null);
        Assert.Equal(WebToolDisplayLiterals.ToolRequestFailed,
            await failed.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TypedFailure_MultilineMessage_IsNormalizedAndCapped()
    {
        var tools = Tools(SearchOk(), (_, _) => Task.FromResult<string?>("first\r\nsecond\rthird"),
            displayBytes: 40, accessor: () => null);
        Assert.Equal("first\nsecond\nthird", await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_ClipNormalizesCrBeforeRuneCount_AndDoesNotMarkTruncated()
    {
        var input = new string('A', 499) + "\r\n" + "B";
        var tools = Tools(SearchOk("[{\"title\":\"T\",\"url\":\"U\",\"content\":\"" + input.Replace("\r", "\\r").Replace("\n", "\\n") + "\"}]"));
        var output = await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        var lines = output.Split('\n');
        var content = lines[2]["Content: ".Length..] + "\n" + lines[3];
        Assert.Equal(500, content.EnumerateRunes().Count());
        Assert.DoesNotContain("TRUNCATED", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WebToolErrorKind.Validation)]
    [InlineData(WebToolErrorKind.CredentialUnavailable)]
    [InlineData(WebToolErrorKind.HttpFailure)]
    [InlineData(WebToolErrorKind.TransportFailure)]
    [InlineData(WebToolErrorKind.MalformedResponse)]
    [InlineData(WebToolErrorKind.ResponseTooLarge)]
    [InlineData(WebToolErrorKind.Timeout)]
    public async Task TypedFailures_TransformOnlyFixedSafeMessage(WebToolErrorKind kind)
    {
        var handler = FailureHandler(kind);
        var seen = new List<string>();
        var tools = Tools(handler, (value, _) =>
        {
            seen.Add(value);
            return Task.FromResult<string?>("SAFE:" + value);
        }, maxResponseBytes: kind == WebToolErrorKind.ResponseTooLarge ? 1 : OllamaWebClientOptions.DefaultMaxResponseBytes,
            accessor: kind == WebToolErrorKind.CredentialUnavailable ? () => null : null);
        var output = kind == WebToolErrorKind.Validation
            ? await tools.SearchAsync(" ", cancellationToken: TestContext.Current.CancellationToken)
            : await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("SAFE:" + WebToolErrorMessages.For(kind), output);
        Assert.Equal(WebToolErrorMessages.For(kind), Assert.Single(seen));
        Assert.DoesNotContain(SecretSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedFailure_CallerCancellationAfterThrowingTransform_PropagatesCallerToken()
    {
        using var caller = new CancellationTokenSource();
        // Cancel after the awaited callback yields, then throw: the caller check must follow the catch.
        var tools = Tools(SearchOk(), async (_, _) =>
        {
            await Task.Yield();
            caller.Cancel();
            throw new InvalidOperationException(SecretSentinel);
        }, accessor: () => null);
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.SearchAsync("q", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, thrown.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransformNullOrThrow_ReturnsOneFixedFallbackWithoutRetry(bool throwing)
    {
        var calls = 0;
        var tools = Tools(SearchOk("[{\"title\":\"T\"}]"), (_, _) =>
        {
            calls++;
            return throwing
                ? throw new InvalidOperationException(SecretSentinel)
                : Task.FromResult<string?>(null);
        });
        var output = await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WebToolDisplayLiterals.TransformFailure, output);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(SecretSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateTransformFailure_AfterEarlyByteFill_DiscardsPartialOutput()
    {
        var calls = 0;
        var tools = Tools(SearchOk("[{\"title\":\"T\",\"url\":\"U\",\"content\":\"C\"}]"), (value, _) =>
        {
            calls++;
            if (calls == 3) throw new InvalidOperationException(SecretSentinel);
            return Task.FromResult<string?>(new string('é', 100));
        }, displayBytes: 40);
        Assert.Equal(WebToolDisplayLiterals.TransformFailure,
            await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CallerCancellation_WinsOverTransformNullThrowAndUnrelatedCancellation()
    {
        foreach (var outcome in new[] { "null", "throw", "cancel" })
        {
            using var caller = new CancellationTokenSource();
            var tools = Tools(SearchOk(), (_, _) =>
            {
                caller.Cancel();
                return outcome switch
                {
                    "null" => Task.FromResult<string?>(null),
                    "throw" => throw new InvalidOperationException(SecretSentinel),
                    _ => Task.FromCanceled<string?>(new CancellationToken(canceled: true)),
                };
            });
            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tools.SearchAsync("q", cancellationToken: caller.Token));
            Assert.Equal(caller.Token, thrown.CancellationToken);
        }
    }

    [Theory]
    [InlineData("cancel-null")]
    [InlineData("cancel-throw")]
    [InlineData("throw-cancel")]
    public async Task CallerCancellation_WinsAcrossAwaitedTransformOutcomes(string outcome)
    {
        using var caller = new CancellationTokenSource();
        var tools = Tools(SearchOk(), async (_, _) =>
        {
            await Task.Yield();
            if (outcome == "throw-cancel")
            {
                caller.Cancel();
                throw new InvalidOperationException(SecretSentinel);
            }
            caller.Cancel();
            if (outcome == "cancel-throw") throw new InvalidOperationException(SecretSentinel);
            return null;
        });
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.SearchAsync("q", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, thrown.CancellationToken);
    }

    [Fact]
    public void Tools_AreFactoryBuiltWithExactNames_AndDirectMethodsRemainIndependent()
    {
        var tools = Tools(SearchOk());
        Assert.Equal(new[] { "web_search", "web_fetch" }, tools.Tools.Select(t => t.Name));
        Assert.All(tools.Tools, t => Assert.IsAssignableFrom<AIFunction>(t));
        Assert.All(tools.Tools, t => Assert.Equal(JsonValueKind.Object, Assert.IsAssignableFrom<AIFunction>(t).JsonSchema.ValueKind));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    public async Task TypedFailure_EmptyOrWhitespaceTransform_ReturnsFixedFallback(string outcome)
    {
        var tools = Tools(SearchOk(), (_, _) => Task.FromResult<string?>(outcome == "empty" ? "" : " \r\n\t"),
            accessor: () => null);
        Assert.Equal("Tool request failed.",
            await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypedFailure_NullOrThrowTransform_ReturnsFallbackOnceWithoutRawDiagnostic(bool throwing)
    {
        var calls = 0;
        var tools = Tools(FailureHandler(WebToolErrorKind.HttpFailure), (value, _) =>
        {
            calls++;
            Assert.Equal(WebToolErrorMessages.HttpFailure, value);
            return throwing ? throw new InvalidOperationException(SecretSentinel) : Task.FromResult<string?>(null);
        });
        var output = await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WebToolDisplayLiterals.TransformFailure, output);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(SecretSentinel, output, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(output) <= OllamaWebTools.MinimumDisplayBytes);
    }

    [Fact]
    public async Task HttpFailure_ReasonPhraseBodyAndCredential_NeverReachTransformOrOutput()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = SecretSentinel + "-REASON",
            Content = new StringContent(SecretSentinel + "-BODY"),
        };
        var handler = RecordingWebHandler.With(HttpStatusCode.BadGateway, response.Content);
        // Install the actual reason phrase through a factory so the adapter receives it verbatim.
        handler.ResponseFactory = _ => response;
        var seen = new List<string>();
        var tools = Tools(handler, (value, _) => { seen.Add(value); return Task.FromResult<string?>(value); },
            accessor: () => SecretSentinel + "-CREDENTIAL");
        var output = await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WebToolErrorMessages.HttpFailure, output);
        Assert.Equal(WebToolErrorMessages.HttpFailure, Assert.Single(seen));
        Assert.DoesNotContain(SecretSentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, seen.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_AllOrdinaryFieldsRedacted_RetainsNormalGrammar()
    {
        var tools = Tools(SearchOk("[{\"title\":\"T\",\"url\":\"U\",\"content\":\"C\"}]"),
            (_, _) => Task.FromResult<string?>(string.Empty));
        Assert.Equal("Title: \nURL: \nContent: ",
            await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fetch_MultilineTransformedFieldsAndLinks_PreserveExactGrammar()
    {
        var tools = Tools(FetchOk("{\"title\":\"t\",\"content\":\"c\",\"links\":[\"l\"]}"), (value, _) =>
            Task.FromResult<string?>(value switch { "t" => "T\r\nT2\rT3", "c" => "C\n", _ => "L1\rL2" }));
        Assert.Equal("Title: T\nT2\nT3\nContent: C\n\nLinks:\nL1\nL2",
            await tools.FetchAsync("https://page.example.test", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StructuralEmptyPage_MissingFields_TransformsFixedEmptyPageMessage()
    {
        var seen = new List<string>();
        var tools = Tools(FetchOk("{}"), (value, _) => { seen.Add(value); return Task.FromResult<string?>("redacted page"); });
        Assert.Equal("redacted page", await tools.FetchAsync("https://page.example.test", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(WebToolDisplayLiterals.EmptyPage, Assert.Single(seen));
    }

    [Fact]
    public async Task SuccessfulNoResults_TransformsFixedMessage_AndSupportsMultilineOutput()
    {
        var seen = new List<string>();
        var tools = Tools(SearchOk(), (value, _) => { seen.Add(value); return Task.FromResult<string?>("none\r\nreally"); });
        Assert.Equal("none\nreally", await tools.SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(WebToolDisplayLiterals.NoResults, Assert.Single(seen));
    }

    [Fact]
    public async Task LateTransformNullOrThrow_AfterAllPriorFieldsTransformed_DiscardsPartialOutput()
    {
        foreach (var throwing in new[] { false, true })
        {
            var seen = new List<string>();
            var tools = Tools(FetchOk("{\"title\":\"T\",\"content\":\"C\",\"links\":[\"L1\",\"L2\"]}"), (value, _) =>
            {
                seen.Add(value);
                if (value == "L2") return throwing
                    ? throw new InvalidOperationException(SecretSentinel)
                    : Task.FromResult<string?>(null);
                return Task.FromResult<string?>(new string('é', 100));
            }, displayBytes: 40);
            Assert.Equal(WebToolDisplayLiterals.TransformFailure,
                await tools.FetchAsync("https://page.example.test", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "T", "C", "L1", "L2" }, seen);
        }
    }

    private static OllamaWebToolsOptions ValidOptions(int? displayBytes = null, int maxFetchLines = 1000) => new()
    {
        OutputTransform = (value, _) => Task.FromResult<string?>(value),
        DisplayBytes = displayBytes ?? OllamaWebTools.DefaultDisplayBytes,
        MaxFetchLines = maxFetchLines,
    };

    private static RecordingWebHandler FailureHandler(WebToolErrorKind kind) => kind switch
    {
        WebToolErrorKind.Validation => SearchOk(),
        WebToolErrorKind.CredentialUnavailable => SearchOk(),
        WebToolErrorKind.HttpFailure => RecordingWebHandler.Json(SecretSentinel, HttpStatusCode.BadGateway),
        WebToolErrorKind.TransportFailure => new ThrowingWebHandler(new IOException(SecretSentinel)),
        WebToolErrorKind.MalformedResponse => RecordingWebHandler.Json(SecretSentinel),
        WebToolErrorKind.ResponseTooLarge => RecordingWebHandler.Json("{\"results\":[]}"),
        WebToolErrorKind.Timeout => new ThrowingWebHandler(new WebToolTimeoutException(SecretSentinel)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
