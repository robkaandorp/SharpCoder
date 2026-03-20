using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharpCoder.Tools;

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
        _logger = options.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting coding agent task in {Dir}", _options.WorkDirectory);
        
        var chatOptions = new ChatOptions
        {
            Tools = new List<AITool>()
        };

        if (_options.EnableBash)
        {
            var bashTools = new BashTools(_options.WorkDirectory);
            chatOptions.Tools.Add(AIFunctionFactory.Create(bashTools.execute_bash_command));
        }

        if (_options.EnableFileOps)
        {
            var fileTools = new FileTools(_options.WorkDirectory);
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.read_file));
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.write_file));
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.edit_file));
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.search_files));
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, @"You are a helpful autonomous coding agent.
You have access to tools to execute bash commands and manipulate the file system.
Execute the user's task by running commands, reading files, and making changes.
When you are completely finished, provide a final summary of what you did.")
        };

        messages.Add(new ChatMessage(ChatRole.User, taskDescription));

        int step = 0;
        while (step < _options.MaxSteps)
        {
            ct.ThrowIfCancellationRequested();
            step++;
            _logger.LogInformation("Agent step {Step}/{MaxSteps}", step, _options.MaxSteps);

            var response = await _client.GetResponseAsync(messages, chatOptions, ct);
            var assistantMessage = response.Messages.FirstOrDefault();
            if (assistantMessage == null)
            {
                _logger.LogWarning("Received empty response from client.");
                return new AgentResult { Status = "Error", Message = "Empty response." };
            }

            messages.Add(assistantMessage);

            var toolCalls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count == 0)
            {
                _logger.LogInformation("Task complete. Final response received.");
                return new AgentResult { Status = "Success", Message = assistantMessage.Text ?? "No text response." };
            }

            foreach (var toolCall in toolCalls)
            {
                _logger.LogInformation("Executing tool {ToolName} with arguments {Args}", toolCall.Name, toolCall.Arguments);
                
                var tool = chatOptions.Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolCall.Name);
                if (tool != null)
                {
                    try
                    {
                        var result = await tool.InvokeAsync(new AIFunctionArguments(toolCall.Arguments), ct);
                        var msg = new ChatMessage(ChatRole.Tool, (string?)null);
                        msg.Contents.Add(new FunctionResultContent(toolCall.CallId, result));
                        messages.Add(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing tool {ToolName}", toolCall.Name);
                        var errorMsg = new ChatMessage(ChatRole.Tool, (string?)null);
                        errorMsg.Contents.Add(new FunctionResultContent(toolCall.CallId, $"Error: {ex.Message}"));
                        messages.Add(errorMsg);
                    }
                }
                else
                {
                    _logger.LogWarning("Tool {ToolName} not found.", toolCall.Name);
                    var notFoundMsg = new ChatMessage(ChatRole.Tool, (string?)null);
                    notFoundMsg.Contents.Add(new FunctionResultContent(toolCall.CallId, $"Error: Tool '{toolCall.Name}' not found."));
                    messages.Add(notFoundMsg);
                }
            }
        }

        _logger.LogWarning("Agent reached maximum steps ({MaxSteps}) without finishing.", _options.MaxSteps);
        return new AgentResult { Status = "Timeout", Message = $"Agent exceeded max steps ({_options.MaxSteps})." };
    }
}
