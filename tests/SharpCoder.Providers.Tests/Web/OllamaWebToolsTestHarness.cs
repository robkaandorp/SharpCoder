using SharpCoder.Providers.Web;
using static SharpCoder.Providers.Tests.Web.OllamaWebClientTestHarness;

namespace SharpCoder.Providers.Tests.Web;

internal static class OllamaWebToolsTestHarness
{
    internal static OllamaWebTools Tools(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<string?>>? transform = null,
        int displayBytes = OllamaWebTools.DefaultDisplayBytes,
        int maxFetchLines = OllamaWebTools.MaximumMaxLines,
        Func<string?>? accessor = null,
        int maxResponseBytes = OllamaWebClientOptions.DefaultMaxResponseBytes)
    {
        var options = Options(handler, accessor: accessor, maxResponseBytes: maxResponseBytes);
        return new OllamaWebTools(new OllamaWebClient(options), new OllamaWebToolsOptions
        {
            OutputTransform = transform ?? ((value, _) => Task.FromResult<string?>(value)),
            DisplayBytes = displayBytes,
            MaxFetchLines = maxFetchLines,
        });
    }

    internal static string SearchJson(params string[] fields) =>
        "{\"results\":[" + string.Join(",", fields.Select(field => "{" + field + "}")) + "]}";

    internal static string Field(string name, string value) =>
        "\"" + name + "\":\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
