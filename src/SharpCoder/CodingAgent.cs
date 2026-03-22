using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly ContextCompactor _compactor;

    public CodingAgent(IChatClient client, AgentOptions options)
    {
        _client = client;
        _options = options;
        _logger = options.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _compactor = new ContextCompactor(client, _logger);
    }

    /// <summary>
    /// Execute a task as a single-turn (stateless) conversation.
    /// For multi-turn, use the overload that accepts an <see cref="AgentSession"/>.
    /// </summary>
    public Task<AgentResult> ExecuteAsync(string taskDescription, CancellationToken ct = default)
    {
        return ExecuteAsync(null, taskDescription, ct);
    }

    /// <summary>
    /// Execute a task within a session, preserving conversation history across calls.
    /// Pass null for a stateless single-turn execution.
    /// </summary>
    public async Task<AgentResult> ExecuteAsync(AgentSession? session, string userMessage, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting coding agent task in {Dir}", _options.WorkDirectory);

        // Auto-compact before building messages if session is large
        if (session != null)
        {
            await _compactor.CompactIfNeededAsync(session, _options, ct);
        }

        var chatOptions = BuildChatOptions();
        var wrappedClient = BuildWrappedClient();

        var messages = BuildMessages(session, userMessage);

        try
        {
            var response = await wrappedClient.GetResponseAsync(messages, chatOptions, ct);
            var toolCalls = AgentResult.CountToolCalls(response.Messages);
            var finalText = response.Text ?? "No text response.";

            // Update session with new messages and usage
            if (session != null)
            {
                UpdateSession(session, response);
            }

            if (response.FinishReason == ChatFinishReason.ToolCalls)
            {
                _logger.LogWarning(
                    "Agent reached MaxSteps limit ({MaxSteps}) with {ToolCalls} tool calls. Task may be incomplete.",
                    _options.MaxSteps, toolCalls);
                return BuildResult("MaxStepsReached", finalText, response, toolCalls);
            }

            _logger.LogInformation("Task complete ({ToolCalls} tool calls).", toolCalls);
            return BuildResult("Success", finalText, response, toolCalls);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution failed.");
            return new AgentResult { Status = "Error", Message = ex.Message };
        }
    }

    private ChatOptions BuildChatOptions()
    {
        var chatOptions = new ChatOptions
        {
            Tools = new List<AITool>(_options.CustomTools),
            ToolMode = ChatToolMode.Auto
        };

        if (_options.EnableBash)
        {
            var bashTools = new BashTools(_options.WorkDirectory, logger: _logger);
            chatOptions.Tools.Add(AIFunctionFactory.Create(bashTools.execute_bash_command));
        }

        if (_options.EnableFileOps)
        {
            var fileTools = new FileTools(_options.WorkDirectory, _logger);
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.read_file));
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.glob));
            chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.grep));

            if (_options.EnableFileWrites)
            {
                chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.write_file));
                chatOptions.Tools.Add(AIFunctionFactory.Create(fileTools.edit_file));
            }
        }

        if (_options.EnableSkills)
        {
            var skillTools = new SkillTools(_options.WorkDirectory);
            chatOptions.Tools.Add(AIFunctionFactory.Create(skillTools.load_skill));
            chatOptions.Tools.Add(AIFunctionFactory.Create(skillTools.list_skills));
        }

        return chatOptions;
    }

    private IChatClient BuildWrappedClient()
    {
        return new ChatClientBuilder(_client)
            .UseFunctionInvocation(configure: fic =>
            {
                fic.MaximumIterationsPerRequest = _options.MaxSteps;
                fic.IncludeDetailedErrors = true;
            })
            .Build();
    }

    private List<ChatMessage> BuildMessages(AgentSession? session, string userMessage)
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, BuildSystemPrompt())
        };

        // Replay session history if present
        if (session?.MessageHistory.Count > 0)
        {
            messages.AddRange(session.MessageHistory);
        }

        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }

    private void UpdateSession(AgentSession session, ChatResponse response)
    {
        // Extract new messages (skip system prompt, keep everything else)
        var newMessages = response.Messages.Where(m => m.Role != ChatRole.System).ToList();
        session.MessageHistory = newMessages;
        session.TotalToolCalls += AgentResult.CountToolCalls(response.Messages);
        session.LastActivityAt = DateTimeOffset.UtcNow;

        // Track token usage
        if (response.Usage != null)
        {
            session.InputTokensUsed += response.Usage.InputTokenCount ?? 0;
            session.OutputTokensUsed += response.Usage.OutputTokenCount ?? 0;
        }

        _logger.LogDebug(
            "Session {SessionId}: {MessageCount} messages, ~{Tokens} context tokens, {TotalTools} total tool calls",
            session.SessionId, session.MessageHistory.Count, session.EstimatedContextTokens, session.TotalToolCalls);
    }

    private static AgentResult BuildResult(string status, string message, ChatResponse response, int toolCalls)
    {
        return new AgentResult
        {
            Status = status,
            Message = message,
            Messages = response.Messages,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ToolCallCount = toolCalls
        };
    }

    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            sb.AppendLine(_options.SystemPrompt);
        }
        else
        {
            sb.AppendLine("You are a helpful autonomous coding agent.");
            sb.AppendLine("You have access to tools to execute bash commands and manipulate the file system.");
            sb.AppendLine("Execute the user's task by running commands, reading files, and making changes.");
            sb.AppendLine("When you are completely finished, provide a final summary of what you did.");
        }

        if (!string.IsNullOrWhiteSpace(_options.CustomInstructions))
        {
            sb.AppendLine("\n# Custom Instructions");
            sb.AppendLine(_options.CustomInstructions);
        }

        if (_options.AutoLoadWorkspaceInstructions)
        {
            var workspaceInstructions = GetWorkspaceInstructions();
            if (!string.IsNullOrWhiteSpace(workspaceInstructions))
            {
                sb.AppendLine("\n# Workspace Instructions");
                sb.AppendLine(workspaceInstructions);
            }
        }

        return sb.ToString();
    }

    private string GetWorkspaceInstructions()
    {
        var sb = new StringBuilder();
        var dir = _options.WorkDirectory;

        var agentsPath = Path.Combine(dir, "AGENTS.md");
        try
        {
            if (File.Exists(agentsPath))
            {
                sb.AppendLine($"--- AGENTS.md ---");
                sb.AppendLine(File.ReadAllText(agentsPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {File}", agentsPath);
        }

        var githubDir = Path.Combine(dir, ".github");
        var copilotInstructionsPath = Path.Combine(githubDir, "copilot-instructions.md");
        try
        {
            if (File.Exists(copilotInstructionsPath))
            {
                sb.AppendLine($"--- .github/copilot-instructions.md ---");
                sb.AppendLine(File.ReadAllText(copilotInstructionsPath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {File}", copilotInstructionsPath);
        }

        var instructionsDir = Path.Combine(githubDir, "instructions");
        if (Directory.Exists(instructionsDir))
        {
            try
            {
                var files = Directory.GetFiles(instructionsDir, "*.instructions.md", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var relPath = Path.GetRelativePath(dir, file).Replace('\\', '/');
                        sb.AppendLine($"--- {relPath} ---");
                        sb.AppendLine(File.ReadAllText(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read instruction file {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate instruction files in {Dir}", instructionsDir);
            }
        }

        return sb.ToString();
    }
}
