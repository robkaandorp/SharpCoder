using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SharpCoder;

public sealed class CodingAgent
{
    private readonly IChatClient _client;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;

    public CodingAgent(IChatClient client, AgentOptions options)
    {
        _client = client;
        _options = options;
        _logger = options.Logger;
    }

    public async Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting coding agent task in {Dir}", _options.WorkDirectory);
        
        return new AgentResult { Status = "NotImplemented", Message = "Agent loop not yet implemented." };
    }
}
