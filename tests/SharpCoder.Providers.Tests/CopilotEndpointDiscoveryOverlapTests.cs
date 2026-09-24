using SharpCoder.Providers;

using System.Net;
using System.Text;

namespace SharpCoder.Providers.Tests;

/// <summary>
/// Self-check for the token A → B → A overlap in the endpoint-discovery cache: a lookup for A (A1)
/// that is superseded — first by B, then by a fresh lookup for A (A2) — must neither publish its
/// stale result nor clear A2's in-flight slot, even though it still carries A's token hash.
/// </summary>
/// <remarks>
/// Every lookup is held at the transport by its own <see cref="TaskCompletionSource{TResult}"/>
/// gate, so the completion order is dictated by the test — no delays, no sleeps. The cache and the
/// lookup seam are process-wide static state, hence the serialized <c>EnvVarMutation</c> collection.
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class CopilotEndpointDiscoveryOverlapTests : IDisposable
{
    private const string TokenA = "gho_overlap_token_a";
    private const string TokenB = "gho_overlap_token_b";

    private const string StaleA1Endpoint = "https://api.stale.githubcopilot.com";
    private const string BEndpoint = "https://api.individual.githubcopilot.com";
    private const string CurrentA2Endpoint = "https://api.business.githubcopilot.com";

    private readonly HttpMessageHandler? _originalLookupHandler = CopilotEndpointDiscovery.LookupHandler;
    private readonly GatedLookupHandler _lookup = new();

    public CopilotEndpointDiscoveryOverlapTests()
    {
        CopilotEndpointDiscovery.LookupHandler = _lookup;
        CopilotEndpointDiscovery.ResetCache();
    }

    public void Dispose()
    {
        _lookup.ReleaseAll();
        CopilotEndpointDiscovery.LookupHandler = _originalLookupHandler;
        CopilotEndpointDiscovery.ResetCache();
    }

    /// <summary>
    /// A1 completes LAST, after A2 has already published: A2's result must stay the cached entry
    /// for A, served without any further lookup.
    /// </summary>
    [Fact]
    public async Task SupersededLookupCompletingLast_DoesNotOverwriteTheCurrentEntry()
    {
        var (a1, a2) = await StartAThenBThenAAsync();

        _lookup.Release(2, CurrentA2Endpoint);
        Assert.Equal(CurrentA2Endpoint, Authority(await a2));

        _lookup.Release(0, StaleA1Endpoint);
        Assert.Equal(StaleA1Endpoint, Authority(await a1)); // A1's own awaiters still get its result.

        var cached = await ChatClientFactory.GetCopilotApiEndpointAsync(TokenA, TestContext.Current.CancellationToken);

        Assert.Equal(CurrentA2Endpoint, Authority(cached));
        Assert.Equal(3, _lookup.Count);
    }

    /// <summary>
    /// A1 completes while A2 is still in flight: the superseded caller gets its own response, but
    /// A1 neither publishes that stale response nor clears A2's slot. A third A caller joins A2
    /// (no fourth lookup and not yet completed). After A2 completes, both that caller and a later
    /// fourth A caller get A2's published result without any further transport request.
    /// </summary>
    [Fact]
    public async Task SupersededLookupCompletingFirst_DoesNotClearTheCurrentInFlightSlot()
    {
        var (a1, a2) = await StartAThenBThenAAsync();
        Task<Uri>? a3 = null;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // This is the dangerous window: the cache's single entry now belongs to A2, but A1
            // has the same token hash. Settle A1 fully BEFORE allowing A2 to complete.
            _lookup.Release(0, StaleA1Endpoint);
            Assert.Equal(StaleA1Endpoint, Authority(await a1.WaitAsync(TimeSpan.FromSeconds(5), ct)));
            Assert.False(a2.IsCompleted);

            a3 = ChatClientFactory.GetCopilotApiEndpointAsync(TokenA, ct);
            Assert.False(a3.IsCompleted, "A3 must join pending A2, never read A1's stale cache entry.");
            Assert.Equal(3, _lookup.Count); // A1, B and A2 only; A3 must not start a new lookup.

            _lookup.Release(2, CurrentA2Endpoint);
            Assert.Equal(CurrentA2Endpoint, Authority(await a2.WaitAsync(TimeSpan.FromSeconds(5), ct)));
            Assert.Equal(CurrentA2Endpoint, Authority(await a3.WaitAsync(TimeSpan.FromSeconds(5), ct)));

            var a4 = await ChatClientFactory.GetCopilotApiEndpointAsync(TokenA, ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(CurrentA2Endpoint, Authority(a4));
            Assert.Equal(3, _lookup.Count); // A2 was published; A4 was served from its cache entry.
        }
        finally
        {
            // Never leave a held transport (or its callers) behind if an assertion fails.
            _lookup.ReleaseAll();
            var pending = a3 is null ? new[] { a1, a2 } : new[] { a1, a2, a3 };
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>
    /// Drives the overlap up to the point where both A lookups are held: A1 started and held, B
    /// started and completed (replacing the entry), then A2 started and held.
    /// </summary>
    private async Task<(Task<Uri> A1, Task<Uri> A2)> StartAThenBThenAAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var a1 = ChatClientFactory.GetCopilotApiEndpointAsync(TokenA, ct);
        Assert.Equal(TokenA, await _lookup.TokenAtAsync(0));

        var b = ChatClientFactory.GetCopilotApiEndpointAsync(TokenB, ct);
        Assert.Equal(TokenB, await _lookup.TokenAtAsync(1));
        _lookup.Release(1, BEndpoint);
        Assert.Equal(BEndpoint, Authority(await b));

        var a2 = ChatClientFactory.GetCopilotApiEndpointAsync(TokenA, ct);
        Assert.Equal(TokenA, await _lookup.TokenAtAsync(2));

        Assert.False(a1.IsCompleted);
        Assert.False(a2.IsCompleted);
        Assert.Equal(3, _lookup.Count);

        return (a1, a2);
    }

    private static string Authority(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Lookup transport that holds every request on its own gate until the test releases it with the
    /// endpoint to advertise, recording the bearer token of each request in arrival order.
    /// </summary>
    private sealed class GatedLookupHandler : HttpMessageHandler
    {
        private readonly List<(string Token, TaskCompletionSource<string> Gate)> _requests = new();

        /// <summary>One arrival signal per request index, completed when that request reaches here.</summary>
        private readonly List<TaskCompletionSource<string>> _arrivals = new();

        public int Count
        {
            get { lock (_requests) { return _requests.Count; } }
        }

        /// <summary>Waits until the request at <paramref name="index"/> has arrived; returns its token.</summary>
        public Task<string> TokenAtAsync(int index) =>
            ArrivalAt(index).Task.WaitAsync(TestContext.Current.CancellationToken);

        private TaskCompletionSource<string> ArrivalAt(int index)
        {
            lock (_requests)
            {
                while (_arrivals.Count <= index)
                    _arrivals.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                return _arrivals[index];
            }
        }

        public void Release(int index, string advertisedEndpoint)
        {
            TaskCompletionSource<string> gate;
            lock (_requests) { gate = _requests[index].Gate; }
            gate.SetResult(advertisedEndpoint);
        }

        public void ReleaseAll()
        {
            lock (_requests)
            {
                foreach (var (_, gate) in _requests) gate.TrySetResult("https://api.githubcopilot.com");
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            int index;
            lock (_requests)
            {
                index = _requests.Count;
                _requests.Add((token, gate));
            }

            ArrivalAt(index).TrySetResult(token);

            var endpoint = await gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"endpoints\":{{\"api\":\"{endpoint}\"}}}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
