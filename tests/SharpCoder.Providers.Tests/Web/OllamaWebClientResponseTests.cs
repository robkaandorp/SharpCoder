using System.Net;
using System.Text;
using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

/// <summary>
/// Response-shape mapping, every failure kind, fixed safe messages with secret sentinels, immediate
/// non-success without body read, and the streaming byte-cap read boundary.
/// </summary>
public sealed class OllamaWebClientResponseTests
{
    [Fact]
    public async Task Search_EmptyArray_IsSuccessfulEmptyResultSet()
    {
        var handler = SearchOk("[]");
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var data = Assert.IsType<WebSearchData>(result.GetRawData());
        Assert.Empty(data.GetRawResults());
        Assert.Null(result.ErrorKind);
        Assert.Equal(string.Empty, result.DisplayMessage);
    }

    [Fact]
    public async Task Search_ArrayOfOnlySkippedEntries_IsSuccessfulEmptyResultSetNotMalformed()
    {
        // Mix null and non-object entries. A {} entry would be a VALID object (empty fields) and is
        // intentionally covered by the missing/null-fields test instead.
        var handler = SearchOk("[null,1,\"s\",[],true]");
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.GetRawData()!.GetRawResults());
    }

    [Fact]
    public async Task Search_NullAndNonObjectEntries_AreSkippedIntentionallyAlongsideValidOnes()
    {
        var handler = SearchOk("[null,1,\"s\",[],{\"title\":\"T\",\"url\":\"U\",\"content\":\"C\"},true,999]");
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var entry = Assert.Single(result.GetRawData()!.GetRawResults());
        Assert.Equal("T", entry.GetRawTitle());
        Assert.Equal("U", entry.GetRawUrl());
        Assert.Equal("C", entry.GetRawContent());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":\"x\"}")]
    [InlineData("{\"results\":42}")]
    public async Task Search_MissingOrWrongShapeResults_ReturnsMalformedResponse(string body)
    {
        var handler = RecordingWebHandler.Json(body);
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
        AssertSafeMessage(result);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData("{\"results\":[{\"title\":42}]}")]
    [InlineData("{\"results\":[{\"url\":true}]}")]
    [InlineData("{\"results\":[{\"content\":{}}]}")]
    [InlineData("{\"results\":[{\"title\":[]}]}")]
    [InlineData("{\"results\":[{\"url\":1.5}]}")]
    public async Task Search_WrongTypeField_ReturnsMalformedResponse(string body)
    {
        var handler = RecordingWebHandler.Json(body);
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task Search_MissingAndNullFieldsIndependently_BecomeEmptyStrings()
    {
        const string body = """
            {"results":[
              {},
              {"title":null,"url":null,"content":null},
              {"title":"T"},
              {"url":"U"},
              {"content":"C"},
              {"title":null,"url":"U2","content":"C2"},
              {"title":"T3","url":null,"content":"C3"},
              {"title":"T4","url":"U4","content":null}
            ]}
            """;
        var handler = RecordingWebHandler.Json(body);
        var result = await Create(Options(handler)).SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var rows = result.GetRawData()!.GetRawResults();
        Assert.Equal(8, rows.Count);
        Assert.Equal(string.Empty, rows[0].GetRawTitle());
        Assert.Equal(string.Empty, rows[0].GetRawUrl());
        Assert.Equal(string.Empty, rows[0].GetRawContent());
        Assert.Equal(string.Empty, rows[1].GetRawTitle());
        Assert.Equal(string.Empty, rows[1].GetRawUrl());
        Assert.Equal(string.Empty, rows[1].GetRawContent());
        Assert.Equal("T", rows[2].GetRawTitle());
        Assert.Equal(string.Empty, rows[2].GetRawUrl());
        Assert.Equal(string.Empty, rows[2].GetRawContent());
        Assert.Equal(string.Empty, rows[3].GetRawTitle());
        Assert.Equal("U", rows[3].GetRawUrl());
        Assert.Equal("C", rows[4].GetRawContent());
        Assert.Equal(string.Empty, rows[5].GetRawTitle());
        Assert.Equal("U2", rows[5].GetRawUrl());
        Assert.Equal("C2", rows[5].GetRawContent());
        Assert.Equal("T3", rows[6].GetRawTitle());
        Assert.Equal(string.Empty, rows[6].GetRawUrl());
        Assert.Equal("C3", rows[6].GetRawContent());
        Assert.Equal("T4", rows[7].GetRawTitle());
        Assert.Equal("U4", rows[7].GetRawUrl());
        Assert.Equal(string.Empty, rows[7].GetRawContent());
    }

    [Fact]
    public async Task Fetch_EmptyObjectAndMissingNullFields_AreToleratedIndependently()
    {
        var empty = await FetchWithBody("{}");
        Assert.True(empty.IsSuccess);
        Assert.Equal(string.Empty, empty.GetRawData()!.GetRawTitle());
        Assert.Equal(string.Empty, empty.GetRawData()!.GetRawContent());
        Assert.Empty(empty.GetRawData()!.GetRawLinks());

        var nulls = await FetchWithBody("{\"title\":null,\"content\":null,\"links\":null}");
        Assert.True(nulls.IsSuccess);
        Assert.Equal(string.Empty, nulls.GetRawData()!.GetRawTitle());
        Assert.Equal(string.Empty, nulls.GetRawData()!.GetRawContent());
        Assert.Empty(nulls.GetRawData()!.GetRawLinks());

        var mixed = await FetchWithBody("{\"title\":\"T\",\"content\":null,\"links\":[]}");
        Assert.True(mixed.IsSuccess);
        Assert.Equal("T", mixed.GetRawData()!.GetRawTitle());
        Assert.Equal(string.Empty, mixed.GetRawData()!.GetRawContent());
        Assert.Empty(mixed.GetRawData()!.GetRawLinks());
    }

    [Fact]
    public async Task Fetch_Links_SkipsNullAndNonStringEntriesIntentionally()
    {
        var body = "{\"title\":\"T\",\"content\":\"C\",\"links\":[null,1,true,{},[],\"L1\",\"L2\",null,\"L3\"]}";
        var result = await FetchWithBody(body);
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "L1", "L2", "L3" }, result.GetRawData()!.GetRawLinks());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"title\":42}")]
    [InlineData("{\"content\":true}")]
    [InlineData("{\"links\":{}}")]
    [InlineData("{\"links\":\"x\"}")]
    [InlineData("{\"links\":42}")]
    public async Task Fetch_WrongShapeOrWrongTypeField_ReturnsMalformedResponse(string body)
    {
        var result = await FetchWithBody(body);
        AssertFailure(result, WebToolErrorKind.MalformedResponse);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task Fetch_FullUntruncatedPayload_PreservesOrderAndContent()
    {
        var longContent = new string('Q', 30_000);
        var body = "{\"title\":\"T\",\"content\":\"" + longContent + "\",\"links\":[\"A\",\"B\"]}";
        var result = await FetchWithBody(body);
        Assert.True(result.IsSuccess);
        Assert.Equal("T", result.GetRawData()!.GetRawTitle());
        Assert.Equal(longContent, result.GetRawData()!.GetRawContent());
        Assert.Equal(new[] { "A", "B" }, result.GetRawData()!.GetRawLinks());
        Assert.Equal(string.Empty, result.DisplayMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task NonSuccessStatus_ImmediateHttpFailure_WithSecretInReasonAndBody(HttpStatusCode status)
    {
        var neverRead = new NeverReadContent(SecretSentinel);
        // Pin the sentinel in the ACTUAL response metadata (reason phrase), not just in the body.
        var response = new HttpResponseMessage(status) { Content = neverRead, ReasonPhrase = SecretSentinel };
        var metadataHandler = RecordingWebHandler.With(status, neverRead);
        metadataHandler.ResponseFactory = _ => response;
        var result = await Create(Options(metadataHandler)).FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.HttpFailure);
        AssertSafeMessage(result);
        Assert.Equal(1, metadataHandler.SendCount);
        Assert.Equal(0, neverRead.ReadAttempts);
        Assert.True(neverRead.Disposed);
        Assert.DoesNotContain(status.ToString(), result.DisplayMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonSuccessStatus_OversizedAndStalledBody_IsNeverReadOrBuffered()
    {
        // A body that would block forever if read, and advertise an enormous length. If production
        // touched it, the test would hang at the gate; instead it must classify immediately.
        var stalled = new GatedWebStream();
        try
        {
            var content = new StreamContent(stalled);
            content.Headers.ContentLength = long.MaxValue / 2;
            var handler = RecordingWebHandler.With(HttpStatusCode.BadGateway, content);
            var result = await Create(Options(handler)).FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
            AssertFailure(result, WebToolErrorKind.HttpFailure);
            AssertSafeMessage(result);
            Assert.Equal(0, stalled.ReadCount);
            await stalled.WaitForDisposalAsync("non-success stalled body");
        }
        finally
        {
            // Assertion failures must never strand the parked stream.
            stalled.ReleaseAllPending();
        }
    }

    [Theory]
    [InlineData("SENTINEL_SECRET_DO_NOT_LEAK_7f31")]
    [InlineData("SECRET_REASON")]
    public async Task TransportFailure_MapsExpectedExceptionClasses_WithNoSecretLeak(string secret)
    {
        foreach (var fault in new Exception[]
                 {
                     new OperationCanceledException(secret),
                     new IOException(secret),
                     new HttpRequestException(secret),
                 })
        {
            var handler = new ThrowingWebHandler(fault);
            var result = await Create(Options(handler)).FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
            AssertFailure(result, WebToolErrorKind.TransportFailure);
            AssertSafeMessage(result);
            Assert.Equal(1, handler.SendCount);
        }
    }

    [Fact]
    public async Task WebToolTimeoutException_BeforeExpiry_MapsToTimeoutWithNoDynamicTextOrInnerLeak()
    {
        var fault = new WebToolTimeoutException(SecretSentinel, new InvalidOperationException(SecretSentinel));
        var handler = new ThrowingWebHandler(fault);
        var result = await Create(Options(handler)).FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task WebToolTimeoutException_NestedInChain_MapsToTimeout()
    {
        var fault = new HttpRequestException(
            SecretSentinel,
            new IOException(SecretSentinel, new WebToolTimeoutException(SecretSentinel)));
        var handler = new ThrowingWebHandler(fault);
        var result = await Create(Options(handler)).FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task MultipleTimeoutMarkers_AnywhereInExceptionGraph_MapToTimeout()
    {
        var fault = new AggregateException(
            new IOException(SecretSentinel),
            new HttpRequestException(SecretSentinel, new WebToolTimeoutException(SecretSentinel)),
            new WebToolTimeoutException(SecretSentinel));
        var handler = new ThrowingWebHandler(fault);
        var result = await Create(Options(handler))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        AssertFailure(result, WebToolErrorKind.Timeout);
        AssertSafeMessage(result);
    }

    [Fact]
    public async Task UnexpectedDirectClientHostException_MayPropagateForHostDiagnostics_BeforeExpiry()
    {
        var fault = new InvalidOperationException(SecretSentinel);
        var handler = new ThrowingWebHandler(fault);
        var client = Create(Options(handler));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchAsync("https://page.example.test", TestContext.Current.CancellationToken));
        Assert.Same(fault, thrown); // identity preserved; host diagnostics only, never a typed result
    }

    [Fact]
    public async Task SuccessBody_ExactlyAtCap_IsAccepted()
    {
        // {"results":[]} is 14 bytes; pad with an ignored property to land exactly on the cap.
        var cap = 64;
        var prefix = "{\"pad\":\"";
        var suffix = "\",\"results\":[]}";
        var padLength = cap - prefix.Length - suffix.Length;
        var body = prefix + new string('p', padLength) + suffix;
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(body));

        var stream = new ChunkedWebStream(Encoding.UTF8.GetBytes(body), chunkSize: 7);
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream));
        var result = await Create(Options(handler, maxResponseBytes: cap))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.GetRawData()!.GetRawResults());
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SuccessBody_OneByteOverCap_ReturnsResponseTooLargeAtReadBoundary()
    {
        var cap = 32;
        var body = "{\"pad\":\"" + new string('p', 64) + "\",\"results\":[]}";
        var stream = new ChunkedWebStream(Encoding.UTF8.GetBytes(body), chunkSize: 5);
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream));
        var result = await Create(Options(handler, maxResponseBytes: cap))
            .SearchAsync("q", cancellationToken: TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.ResponseTooLarge);
        AssertSafeMessage(result);
        Assert.True(stream.Disposed);
        // Exactly cap+1 bytes is the boundary; the cap is enforced per read, not by Content-Length.
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Any2xxStatus_IsSuccessAndBodyIsParsed(HttpStatusCode status)
    {
        var handler = RecordingWebHandler.With(status, JsonBody("{\"title\":\"T\"}"));
        var result = await Create(Options(handler))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("T", result.GetRawData()!.GetRawTitle());
    }

    [Fact]
    public async Task NonSuccessNoBodyReadGuard_ThrowingContentReadStreamAsync_IsNeverInvoked()
    {
        var content = new ThrowingReadAsStreamContent(SecretSentinel);
        var handler = RecordingWebHandler.With(HttpStatusCode.Forbidden, content);
        var result = await Create(Options(handler)).FetchAsync(
            "https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.HttpFailure);
        AssertSafeMessage(result);
        Assert.Equal(0, content.TokenOverloadAttempts);
        Assert.Equal(0, content.ParameterlessOverloadAttempts);
        Assert.Equal(0, content.SerializeAttempts);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task StreamingCap_StopsReadingOnceExceeded()
    {
        var cap = 16;
        var bytes = new byte[4096];
        Array.Fill(bytes, (byte)'a');
        var stream = new ChunkedWebStream(bytes, chunkSize: 8);
        var handler = RecordingWebHandler.With(HttpStatusCode.OK, new StreamContent(stream));
        var result = await Create(Options(handler, maxResponseBytes: cap))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);

        AssertFailure(result, WebToolErrorKind.ResponseTooLarge);
        // Two 8-byte reads fit exactly; the third read crosses the cap and stops the loop.
        Assert.True(stream.ReadCount <= 4, $"Unexpected read count {stream.ReadCount}; cap must stop at the read boundary.");
        Assert.True(stream.Disposed);
    }

    private static async Task<WebToolResult<WebFetchData>> FetchWithBody(string body) =>
        await Create(Options(RecordingWebHandler.Json(body)))
            .FetchAsync("https://page.example.test", TestContext.Current.CancellationToken);
}

/// <summary>A handler that throws a specified exception from the awaited send operation.</summary>
internal sealed class ThrowingWebHandler : RecordingWebHandler
{
    private readonly Exception _fault;

    internal ThrowingWebHandler(Exception fault)
        : base((_, _) => Task.FromResult(RecordingWebHandler.CreateResponse(HttpStatusCode.OK, new StringContent("{}")))) =>
        _fault = fault;

    protected override Task<HttpResponseMessage> SendAsyncCore(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        Task.FromException<HttpResponseMessage>(_fault);
}

/// <summary>
/// Content whose stream-open overloads and serialization path all fail. This is a second
/// independent no-body-read detector: production's exact ReadAsStreamAsync(token) path would
/// increment TokenOverloadAttempts and propagate a secret-bearing exception.
/// </summary>
internal sealed class ThrowingReadAsStreamContent : HttpContent
{
    private readonly string _secret;
    private int _tokenOverloadAttempts;
    private int _parameterlessOverloadAttempts;
    private int _serializeAttempts;
    private int _disposed;

    internal ThrowingReadAsStreamContent(string secret) => _secret = secret;

    internal int TokenOverloadAttempts => Volatile.Read(ref _tokenOverloadAttempts);
    internal int ParameterlessOverloadAttempts => Volatile.Read(ref _parameterlessOverloadAttempts);
    internal int SerializeAttempts => Volatile.Read(ref _serializeAttempts);
    internal bool Disposed => Volatile.Read(ref _disposed) != 0;

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tokenOverloadAttempts);
        return Task.FromException<Stream>(new InvalidOperationException(_secret));
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        Interlocked.Increment(ref _parameterlessOverloadAttempts);
        return Task.FromException<Stream>(new InvalidOperationException(_secret));
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        Interlocked.Increment(ref _serializeAttempts);
        return Task.FromException(new InvalidOperationException(_secret));
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 1;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}

/// <summary>
/// Content whose body is deliberately never read by production. It records any attempt, so a
/// regression that reads a non-success body fails immediately with bounded diagnostics.
/// </summary>
internal sealed class NeverReadContent : HttpContent
{
    private readonly string _secret;
    private int _readAttempts;
    private int _disposed;

    internal NeverReadContent(string secret) => _secret = secret;

    /// <summary>Nonzero if production ever attempted to materialize the body.</summary>
    internal int ReadAttempts => Volatile.Read(ref _readAttempts);

    /// <summary>True when the response/content was disposed.</summary>
    internal bool Disposed => Volatile.Read(ref _disposed) != 0;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        Interlocked.Increment(ref _readAttempts);
        return Task.FromException(new InvalidOperationException(_secret));
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        Interlocked.Increment(ref _readAttempts);
        return Task.FromException<Stream>(new InvalidOperationException(_secret));
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 1;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}
