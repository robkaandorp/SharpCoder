using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder;

public sealed class AgentOptions
{
    public string WorkDirectory { get; set; } = Directory.GetCurrentDirectory();
    public int MaxSteps { get; set; } = 25;
    public bool EnableBash { get; set; } = true;
    public bool EnableFileOps { get; set; } = true;
    public ILogger Logger { get; set; } = NullLogger.Instance;
}
