using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder.CliAgent;

/// <summary>
/// Forces HTTP/1.1 for outgoing requests to ollama.com. The default HttpClient on
/// .NET 10 negotiates HTTP/2, which Ollama Cloud's edge occasionally answers with
/// opaque 500s when the payload contains tool definitions — 1.1 is reliable.
/// </summary>
internal sealed class Http11Handler : DelegatingHandler
{
    public Http11Handler(HttpMessageHandler inner) : base(inner) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Version = System.Net.HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return base.SendAsync(request, ct);
    }
}
