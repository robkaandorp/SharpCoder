using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
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

        // Capture diagnostics before the LLM call so they're available even on failure
        var diagnostics = BuildDiagnostics(messages, chatOptions, userMessage, session);

        try
        {
            var response = await wrappedClient.GetResponseAsync(messages, chatOptions, ct);
            var toolCalls = AgentResult.CountToolCalls(response.Messages);
            var finalText = response.Text ?? "No text response.";

            // Log token usage (always, even without a session)
            if (response.Usage != null)
            {
                _logger.LogInformation(
                    "Usage: inputTokens={InputTokens}, outputTokens={OutputTokens}, totalTokens={TotalTokens}",
                    response.Usage.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount);
            }

            // Update session with new messages and usage
            if (session != null)
            {
                UpdateSession(session, userMessage, response);
            }

            if (response.FinishReason == ChatFinishReason.ToolCalls)
            {
                _logger.LogWarning(
                    "Agent reached MaxSteps limit ({MaxSteps}) with {ToolCalls} tool calls. Task may be incomplete.",
                    _options.MaxSteps, toolCalls);
                return BuildResult("MaxStepsReached", finalText, response, toolCalls, diagnostics);
            }

            _logger.LogInformation("Task complete ({ToolCalls} tool calls).", toolCalls);
            return BuildResult("Success", finalText, response, toolCalls, diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogError(ex, "SDK ArgumentOutOfRangeException — likely a malformed LLM response. Messages sent: {Count}", messages.Count);
            return new AgentResult { Status = "Error", Message = $"SDK error (malformed LLM response): {ex.Message}", Diagnostics = diagnostics };
        }
        catch (HttpRequestException)
        {
            throw; // Propagate HTTP errors so callers can retry
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution failed.");
            return new AgentResult { Status = "Error", Message = ex.Message, Diagnostics = diagnostics };
        }
    }

    /// <summary>
    /// Execute a task with streaming, yielding incremental text updates as they arrive.
    /// The final update has <see cref="StreamingUpdateKind.Completed"/> with the full <see cref="AgentResult"/>.
    /// </summary>
    public async IAsyncEnumerable<StreamingUpdate> ExecuteStreamingAsync(
        AgentSession? session,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Starting streaming coding agent task in {Dir}", _options.WorkDirectory);

        if (session != null)
        {
            await _compactor.CompactIfNeededAsync(session, _options, ct);
        }

        var chatOptions = BuildChatOptions();
        var wrappedClient = BuildWrappedClient();
        var messages = BuildMessages(session, userMessage);
        var diagnostics = BuildDiagnostics(messages, chatOptions, userMessage, session);

        var updates = new List<ChatResponseUpdate>();
        Exception? streamError = null;

        // Manually iterate the stream so we can catch errors from MoveNextAsync
        // while still yielding text deltas (yield is not allowed inside try-catch,
        // but IS allowed inside try-finally).
        var enumerator = wrappedClient.GetStreamingResponseAsync(messages, chatOptions, ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    throw; // Propagate HTTP errors so callers can retry
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    break;
                }

                if (!hasNext) break;

                var update = enumerator.Current;
                updates.Add(update);

                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return StreamingUpdate.TextDelta(update.Text);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (streamError != null)
        {
            _logger.LogError(streamError, "Streaming agent execution failed.");
            yield return StreamingUpdate.Completed(new AgentResult
            {
                Status = "Error",
                Message = streamError.Message,
                Diagnostics = diagnostics,
            });
            yield break;
        }

        // Build a ChatResponse from the accumulated stream updates for session tracking
        var response = BuildResponseFromUpdates(updates);
        var toolCalls = AgentResult.CountToolCalls(response.Messages);
        var finalText = response.Text ?? "No text response.";

        if (response.Usage != null)
        {
            _logger.LogInformation(
                "Usage: inputTokens={InputTokens}, outputTokens={OutputTokens}, totalTokens={TotalTokens}",
                response.Usage.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount);
        }

        if (session != null)
        {
            UpdateSession(session, userMessage, response);
        }

        if (response.FinishReason == ChatFinishReason.ToolCalls)
        {
            _logger.LogWarning(
                "Agent reached MaxSteps limit ({MaxSteps}) with {ToolCalls} tool calls. Task may be incomplete.",
                _options.MaxSteps, toolCalls);
            yield return StreamingUpdate.Completed(
                BuildResult("MaxStepsReached", finalText, response, toolCalls, diagnostics));
        }
        else
        {
            _logger.LogInformation("Streaming task complete ({ToolCalls} tool calls).", toolCalls);
            yield return StreamingUpdate.Completed(
                BuildResult("Success", finalText, response, toolCalls, diagnostics));
        }
    }

    private ChatOptions BuildChatOptions()
    {
        var chatOptions = new ChatOptions
        {
            Tools = new List<AITool>(_options.CustomTools),
            ToolMode = ChatToolMode.Auto
        };

        if (_options.ReasoningEffort.HasValue)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = _options.ReasoningEffort.Value };
        }

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

    private void UpdateSession(AgentSession session, string userMessage, ChatResponse response)
    {
        // Append the user message and all response messages to the existing history.
        // response.Messages contains assistant responses (and tool call/result messages
        // from the function invocation loop) but NOT the input messages we sent.
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, userMessage));
        foreach (var msg in response.Messages)
        {
            if (msg.Role != ChatRole.System)
                session.MessageHistory.Add(msg);
        }

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

    private static AgentResult BuildResult(string status, string message, ChatResponse response, int toolCalls, SessionDiagnostics? diagnostics = null)
    {
        return new AgentResult
        {
            Status = status,
            Message = message,
            Messages = response.Messages,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ToolCallCount = toolCalls,
            Diagnostics = diagnostics,
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

        if (_options.EnableSkills)
        {
            var skillTools = new SkillTools(_options.WorkDirectory);
            var skillSummary = skillTools.ListSkillsSummary();
            if (!string.IsNullOrWhiteSpace(skillSummary))
            {
                sb.AppendLine("\n# Project Skills");
                sb.AppendLine(skillSummary);
                sb.AppendLine("IMPORTANT: Before building or testing, load the relevant skill first with load_skill.");
            }
        }

        return sb.ToString();
    }

    private SessionDiagnostics BuildDiagnostics(
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        string userMessage,
        AgentSession? session)
    {
        var systemPrompt = messages.Count > 0 && messages[0].Role == ChatRole.System
            ? messages[0].Text ?? string.Empty
            : string.Empty;

        return new SessionDiagnostics
        {
            SystemPrompt = systemPrompt,
            UserMessage = userMessage,
            SessionHistoryCount = session?.MessageHistory.Count ?? 0,
            TotalMessageCount = messages.Count,
            ToolNames = chatOptions.Tools?.Select(t => t is AIFunction f ? f.Name : t.GetType().Name).ToList()
                        ?? new List<string>(),
            WorkDirectory = _options.WorkDirectory,
            EnableBash = _options.EnableBash,
            EnableFileWrites = _options.EnableFileWrites,
            AutoLoadedWorkspaceInstructions = _options.AutoLoadWorkspaceInstructions,
            SkillsEnabled = _options.EnableSkills,
            ReasoningEffort = _options.ReasoningEffort?.ToString(),
            MaxSteps = _options.MaxSteps,
        };
    }

    private static ChatResponse BuildResponseFromUpdates(List<ChatResponseUpdate> updates)
    {
        var textBuilder = new StringBuilder();
        ChatFinishReason? finishReason = null;
        string? modelId = null;
        UsageDetails? usage = null;
        bool hasText = false;
        bool needsSeparator = false;

        foreach (var update in updates)
        {
            // Detect new round boundary. When FunctionInvokingChatClient
            // handles tool calls, the previous round ends with a FinishReason.
            // If text follows after that, it's a new round — insert a paragraph
            // break so rounds don't merge into a single wall of text.
            if (update.FinishReason is not null && hasText)
            {
                needsSeparator = true;
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                if (needsSeparator)
                {
                    textBuilder.Append("\n\n");
                    needsSeparator = false;
                }
                textBuilder.Append(update.Text);
                hasText = true;
            }

            // Keep last FinishReason (the final round's reason)
            if (update.FinishReason != null)
                finishReason = update.FinishReason;

            if (update.ModelId != null)
                modelId = update.ModelId;

            // Usage details may appear as UsageContent in the final update
            foreach (var content in update.Contents)
            {
                if (content is UsageContent uc)
                    usage = uc.Details;
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, textBuilder.ToString());
        return new ChatResponse(message)
        {
            FinishReason = finishReason,
            ModelId = modelId,
            Usage = usage,
        };
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
