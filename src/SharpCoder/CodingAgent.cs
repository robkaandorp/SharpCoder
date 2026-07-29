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
using SharpCoder.SubAgents;
using SharpCoder.Tools;

namespace SharpCoder;

public sealed class CodingAgent
{
    private readonly IChatClient _client;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly ContextCompactor _compactor;
    private SubAgentManager? _subAgentManager;
    private SubAgentOptions? _subAgentSnapshot;
    private readonly object _subAgentLock = new();
    private int _subAgentManagerCreateCount;

    /// <summary>Test seam: how many times a sub-agent manager was created for this instance.</summary>
    internal int SubAgentManagerCreateCount => Volatile.Read(ref _subAgentManagerCreateCount);

    private bool IsSubAgentManagerCreated => Volatile.Read(ref _subAgentManagerCreateCount) > 0;

    private bool SubAgentsEffectivelyEnabled => _options.SubAgents != null || IsSubAgentManagerCreated;

    /// <summary>Tool names reserved by the sub-agent tool namespace.</summary>
    private static readonly HashSet<string> ReservedSubAgentToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "start_sub_agent", "await_sub_agents", "get_sub_agent_status", "list_sub_agent_models"
        };

    private static void ThrowOnReservedToolNames(IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            // Check AITool.Name on EVERY tool, not just invokable AIFunction instances:
            // non-invokable subtypes (e.g. AIFunctionDeclaration) also carry a Name.
            if (tool != null && ReservedSubAgentToolNames.Contains(tool.Name))
                throw new ArgumentException(
                    $"CustomTools contains a reserved name '{tool.Name}' that conflicts with the sub-agent tool namespace. Remove it or rename it.");
        }
    }

    /// <summary>
    /// Returns the shared sub-agent manager, creating it once (thread-safe) from a defensive
    /// snapshot of <see cref="AgentOptions.SubAgents"/>.
    /// </summary>
    private SubAgentManager? GetOrCreateSubAgentManager()
    {
        if (IsSubAgentManagerCreated) return _subAgentManager;
        lock (_subAgentLock)
        {
            if (IsSubAgentManagerCreated) return _subAgentManager;
            var source = _options.SubAgents;
            if (source is null) return null;

            var snapshot = new SubAgentOptions
            {
                MaxConcurrentSubAgents = source.MaxConcurrentSubAgents,
                DefaultTimeout = source.DefaultTimeout,
                MaxTimeout = source.MaxTimeout,
                MaxSummaryChars = source.MaxSummaryChars,
                ClientFactory = source.ClientFactory,
                DefaultClient = source.DefaultClient,
                DefaultEnableBash = source.DefaultEnableBash,
                DefaultEnableFileOps = source.DefaultEnableFileOps,
                DefaultEnableFileWrites = source.DefaultEnableFileWrites,
                DefaultEnableSkills = source.DefaultEnableSkills,
                MaxSteps = source.MaxSteps
            };
            foreach (var model in source.AvailableModels)
                snapshot.AvailableModels.Add(model);

            _subAgentSnapshot = snapshot;
            _subAgentManager = new SubAgentManager(snapshot, _client, _options, _logger);
            Interlocked.Increment(ref _subAgentManagerCreateCount);
            return _subAgentManager;
        }
    }

    public CodingAgent(IChatClient client, AgentOptions options)
    {
        _client = client;
        _options = options;
        _logger = options.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _compactor = new ContextCompactor(options.CompactionClient ?? client, _logger);

        if (SubAgentsEffectivelyEnabled)
            ThrowOnReservedToolNames(options.CustomTools);
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

        var chatOptions = BuildChatOptions(ct);
        var (wrappedClient, captureClient) = BuildWrappedClientWithCapture();

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

            if (session != null)
                session.LastKnownContextTokens = captureClient.LastRoundInputTokens
                    ?? response.Usage?.InputTokenCount ?? session.LastKnownContextTokens;

            // Update session with new messages and usage
            if (session != null)
            {
                UpdateSession(session, userMessage, response, captureClient);
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

        // When ShowToolCallsInStream is enabled, handle the tool loop manually
        // so we can inject markdown-formatted tool call text at the right position.
        if (_options.ShowToolCallsInStream)
        {
            await foreach (var update in StreamWithToolCallsAsync(session, userMessage, ct))
                yield return update;
            yield break;
        }

        var chatOptions = BuildChatOptions(ct);
        var (wrappedClient, captureClient) = BuildWrappedClientWithCapture();
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
        var finalText = BuildDisplayText(updates);
        if (string.IsNullOrEmpty(finalText)) finalText = "No text response.";

        if (response.Usage != null)
        {
            _logger.LogInformation(
                "Usage: inputTokens={InputTokens}, outputTokens={OutputTokens}, totalTokens={TotalTokens}",
                response.Usage.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount);
        }

        if (session != null)
        {
            UpdateSession(session, userMessage, response, captureClient);
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

    /// <summary>
    /// Streaming path that handles the tool invocation loop manually,
    /// injecting markdown-formatted tool call info into the text stream.
    /// </summary>
    private async IAsyncEnumerable<StreamingUpdate> StreamWithToolCallsAsync(
        AgentSession? session,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chatOptions = BuildChatOptions(ct);
        var messages = BuildMessages(session, userMessage);
        var diagnostics = BuildDiagnostics(messages, chatOptions, userMessage, session);

        var allResponseMessages = new List<ChatMessage>();
        int totalToolCalls = 0;
        int steps = 0;
        string? lastModelId = null;
        UsageDetails? lastUsage = null;

        while (steps <= _options.MaxSteps)
        {
            // Stream one round from the raw client (no FunctionInvokingChatClient)
            var streamUpdates = new List<ChatResponseUpdate>();
            Exception? streamError = null;

            var enumerator = _client.GetStreamingResponseAsync(messages, chatOptions, ct)
                .GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = await enumerator.MoveNextAsync(); }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException) { throw; }
                    catch (Exception ex) { streamError = ex; break; }

                    if (!hasNext) break;

                    var update = enumerator.Current;
                    streamUpdates.Add(update);

                    if (!string.IsNullOrEmpty(update.Text))
                        yield return StreamingUpdate.TextDelta(update.Text);
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (streamError != null)
            {
                if (ContextCompactor.IsContextOverflowError(streamError))
                {
                    _logger.LogWarning(streamError, "Context overflow — compacting and retrying");
                    if (session != null && await _compactor.ForceCompactAsync(session, _options, ct))
                    {
                        messages = BuildMessages(session, ""); // rebuild from compacted session
                        messages.RemoveAt(messages.Count - 1); // remove the empty user message
                        continue; // retry the round
                    }
                }

                _logger.LogError(streamError, "Streaming agent execution failed.");
                yield return StreamingUpdate.Completed(new AgentResult
                {
                    Status = "Error",
                    Message = streamError.Message,
                    Diagnostics = diagnostics,
                });
                yield break;
            }

            // Reconstruct the response from streaming updates
            var response = streamUpdates.ToChatResponse();
            if (response.ModelId != null) lastModelId = response.ModelId;
            if (response.Usage != null) lastUsage = response.Usage;

            if (session != null && response.Usage?.InputTokenCount != null)
                session.LastKnownContextTokens = response.Usage.InputTokenCount.Value;

            // Track response messages for session
            foreach (var msg in response.Messages)
                allResponseMessages.Add(msg);

            // Extract tool calls from the response
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (functionCalls.Count == 0)
            {
                // No tool calls — final text response. Add to messages before breaking.
                foreach (var msg in response.Messages)
                    messages.Add(msg);
                break;
            }

            // Add assistant response (containing tool calls) to the conversation
            foreach (var msg in response.Messages)
                messages.Add(msg);

            // Process each tool call
            foreach (var fc in functionCalls)
            {
                steps++;
                totalToolCalls++;

                // Yield formatted tool call as markdown
                var argsStr = FormatToolCallArgs(fc.Arguments);
                yield return StreamingUpdate.TextDelta($"\n\n`🔧 {fc.Name}({argsStr})`\n");

                // Find and invoke the matching tool
                var tool = chatOptions.Tools?.OfType<AIFunction>().FirstOrDefault(f => f.Name == fc.Name);
                object? result;
                if (tool != null)
                {
                    try
                    {
                        result = await tool.InvokeAsync(
                            new AIFunctionArguments(fc.Arguments ?? new Dictionary<string, object?>()), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Tool {ToolName} failed", fc.Name);
                        result = $"Error: {ex.Message}";
                    }
                }
                else
                {
                    result = $"Unknown tool: {fc.Name}";
                }

                // Yield formatted result (first line, truncated)
                var resultStr = result?.ToString() ?? "(no result)";
                var firstLine = TruncateFirstLine(resultStr, 120);
                yield return StreamingUpdate.TextDelta($"> {firstLine}\n");

                // Add tool result to conversation for the next round
                var resultMessage = new ChatMessage(ChatRole.Tool,
                    new AIContent[] { new FunctionResultContent(fc.CallId, result) });
                messages.Add(resultMessage);
                allResponseMessages.Add(resultMessage);
            }

            // Mid-loop compaction: check before next API call
            await _compactor.CompactIfNeededAsync(session, messages, _options, ct);
        }

        // Update session from the authoritative messages list.
        // messages contains: [system prompt, (compacted summary OR full history), user message, tool calls/results...]
        // We strip the system prompt to get the session history.
        if (session != null)
        {
            var startIdx = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
            session.MessageHistory = new List<ChatMessage>(messages.Skip(startIdx));

            session.TotalToolCalls += totalToolCalls;
            session.LastActivityAt = DateTimeOffset.UtcNow;
            if (lastUsage != null)
            {
                session.InputTokensUsed += lastUsage.InputTokenCount ?? 0;
                session.OutputTokensUsed += lastUsage.OutputTokenCount ?? 0;
            }
        }

        // Build final text from all assistant text messages
        var finalText = string.Join("\n\n",
            allResponseMessages
                .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text));
        if (string.IsNullOrEmpty(finalText)) finalText = "No text response.";

        var status = steps >= _options.MaxSteps ? "MaxStepsReached" : "Success";
        if (status == "MaxStepsReached")
        {
            _logger.LogWarning("Agent reached MaxSteps limit ({MaxSteps}) with {ToolCalls} tool calls.",
                _options.MaxSteps, totalToolCalls);
        }
        else
        {
            _logger.LogInformation("Streaming task complete ({ToolCalls} tool calls).", totalToolCalls);
        }

        yield return StreamingUpdate.Completed(new AgentResult
        {
            Status = status,
            Message = finalText,
            Messages = allResponseMessages,
            ModelId = lastModelId,
            FinishReason = steps >= _options.MaxSteps ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = lastUsage,
            ToolCallCount = totalToolCalls,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>Formats tool call arguments as a truncated key=value string.</summary>
    internal static string FormatToolCallArgs(IDictionary<string, object?>? args, int maxLength = 100)
    {
        if (args == null || args.Count == 0) return "";
        var parts = args.Select(a =>
        {
            var val = a.Value?.ToString() ?? "null";
            if (val.Length > 40) val = val.Substring(0, 39) + "…";
            return $"{a.Key}={FormatArgValue(val)}";
        });
        var joined = string.Join(", ", parts);
        if (joined.Length > maxLength)
            joined = joined.Substring(0, maxLength - 1) + "…";
        return joined;
    }

    private static string FormatArgValue(string val)
    {
        // Wrap string values in quotes for readability
        if (val == "null" || val == "true" || val == "false") return val;
        if (int.TryParse(val, out _) || double.TryParse(val, out _)) return val;
        return "\"" + val + "\"";
    }

    /// <summary>Returns the first line of text, truncated to maxLength.</summary>
    internal static string TruncateFirstLine(string text, int maxLength)
    {
        var newline = text.IndexOfAny(new[] { '\n', '\r' });
        var line = newline >= 0 ? text.Substring(0, newline) : text;
        if (line.Length > maxLength)
            line = line.Substring(0, maxLength - 1) + "…";
        return line;
    }

    private ChatOptions BuildChatOptions(CancellationToken ct)
    {
        var chatOptions = new ChatOptions
        {
            Tools = new List<AITool>(_options.CustomTools),
            ToolMode = ChatToolMode.Auto
        };

        // Catches post-construction mutation of CustomTools too.
        if (SubAgentsEffectivelyEnabled)
            ThrowOnReservedToolNames(chatOptions.Tools);

        if (_options.ReasoningEffort.HasValue)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = _options.ReasoningEffort.Value };
        }

        if (_options.EnableBash)
        {
            var bashTools = new BashTools(
                _options.WorkDirectory,
                timeoutMs: 120000,
                logger: _logger,
                shellPathOverride: _options.BashShellPath,
                shellArgsFormat: _options.BashShellArgsFormat);
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

        if (SubAgentsEffectivelyEnabled)
        {
            var manager = GetOrCreateSubAgentManager();
            if (manager != null && _subAgentSnapshot != null)
            {
                foreach (var tool in SubAgentTools.BuildTools(manager, _subAgentSnapshot, ct))
                    chatOptions.Tools.Add(tool);
            }
        }

        return chatOptions;
    }

    private IChatClient BuildWrappedClient() => BuildWrappedClientWithCapture().Wrapped;

    private (IChatClient Wrapped, UsageCapturingChatClient Capture) BuildWrappedClientWithCapture()
    {
        var capture = new UsageCapturingChatClient(_client);
        var wrapped = new ChatClientBuilder(capture)
            .UseFunctionInvocation(configure: fic =>
            {
                fic.MaximumIterationsPerRequest = _options.MaxSteps;
                fic.IncludeDetailedErrors = true;
            })
            .Build();
        return (wrapped, capture);
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

    private void UpdateSession(AgentSession session, string userMessage, ChatResponse response,
        UsageCapturingChatClient? captureClient = null)
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

        if (captureClient?.LastRoundInputTokens != null)
            session.LastKnownContextTokens = captureClient.LastRoundInputTokens.Value;
        else if (response.Usage?.InputTokenCount != null)
            session.LastKnownContextTokens = response.Usage.InputTokenCount.Value;

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

        if (SubAgentsEffectivelyEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("You can delegate self-contained subtasks (codebase analysis, large text summarization, parallel research) to background sub-sessions using start_sub_agent. Their full output never enters this conversation — only a summary returns when you call await_sub_agents. Sub-sessions run read-only by default and cannot exceed your own tool capabilities.");
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

    /// <summary>
    /// Aggregates streaming updates into a <see cref="ChatResponse"/>, preserving the
    /// full message structure — assistant text, reasoning content, tool calls and tool
    /// results produced by the function invocation loop. This is what gets appended to
    /// the session history, so dropping any of it would erase the agent's memory of the
    /// tools it invoked (and zero out <see cref="AgentResult.ToolCallCount"/>).
    /// </summary>
    private static ChatResponse BuildResponseFromUpdates(List<ChatResponseUpdate> updates)
        => updates.ToChatResponse();

    /// <summary>
    /// Builds the human-readable text for the final result from the raw stream updates.
    /// Rounds are separated by a blank line: when <see cref="FunctionInvokingChatClient"/>
    /// handles tool calls, each round ends with a FinishReason, so text arriving after one
    /// belongs to a new round and must not be glued onto the previous round's text.
    /// </summary>
    private static string BuildDisplayText(List<ChatResponseUpdate> updates)
    {
        var textBuilder = new StringBuilder();
        bool hasText = false;
        bool needsSeparator = false;

        foreach (var update in updates)
        {
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
        }

        return textBuilder.ToString();
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

    private sealed class UsageCapturingChatClient : DelegatingChatClient
    {
        public long? LastRoundInputTokens { get; private set; }

        public UsageCapturingChatClient(IChatClient inner) : base(inner) { }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? chatOptions, CancellationToken ct = default)
        {
            var response = await InnerClient.GetResponseAsync(messages, chatOptions, ct);
            LastRoundInputTokens = response.Usage?.InputTokenCount;
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? chatOptions,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            long? captured = null;
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, chatOptions, ct)
                .WithCancellation(ct))
            {
                if (update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details is { } details)
                {
                    captured = details.InputTokenCount;
                }
                yield return update;
            }
            LastRoundInputTokens = captured;
        }
    }
}
