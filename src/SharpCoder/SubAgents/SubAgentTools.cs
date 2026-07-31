using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using SharpCoder.Tools;

namespace SharpCoder.SubAgents;

/// <summary>
/// Builds the LLM-facing tools that drive a <see cref="SubAgentManager"/>.
/// </summary>
internal static class SubAgentTools
{
    private const string StartDescription =
        "Start a background sub-agent to work on a self-contained subtask. Returns immediately with " +
        "{\"id\":\"sub-1\",\"status\":\"Running\"} — does NOT wait for completion. The sub-agent runs read-only by " +
        "default and can never exceed your own tool capabilities (bash, file writes). If the concurrency limit is " +
        "reached, this call blocks until a slot frees. Cancelling the current execution aborts the slot wait. Only a " +
        "summary returns later via await_sub_agents. Use list_sub_agent_models to see valid model IDs. File ops and " +
        "skills are governed by manager defaults, not LLM-controllable arguments. Optionally pass image_paths (array " +
        "of repo-relative file paths to image/PDF files) to hand visual content to a vision-capable sub-agent model.";

    private const string AwaitDescription =
        "Block until all (or the specified) sub-agents finish, then return their results as a JSON array. Each item " +
        "has id, status, summary, error, input_tokens, output_tokens. Never throws for failed or timed-out " +
        "sub-agents. Cancelling the current execution aborts the wait.";

    private const string StatusDescription =
        "Get the status of sub-agents. Always returns a JSON array. With an id, returns a single-item array (or " +
        "empty [] if unknown). Without an id, returns all tracked sub-agents.";

    private const string ModelsDescription =
        "List the available sub-agent models. Returns a JSON array of {id, description, context_window, supports_vision}. " +
        "supports_vision is informational and marks vision-capable models to guide image/PDF image_paths model selection. " +
        "If no models are configured, returns a message indicating the default model is used.";

    /// <summary>Creates the four sub-agent tools bound to the given manager.</summary>
    internal static IList<AITool> BuildTools(SubAgentManager manager, SubAgentOptions options, CancellationToken executionCt)
    {
        if (manager is null) throw new ArgumentNullException(nameof(manager));
        if (options is null) throw new ArgumentNullException(nameof(options));

        async Task<string> StartSubAgentAsync(
            string task,
            string? model = null,
            string? system_prompt = null,
            bool? enable_bash = null,
            bool? enable_file_writes = null,
            int? timeout_seconds = null,
            string[]? image_paths = null)
        {
            SubAgentInfo info;
            try
            {
                var request = new SubAgentRequest
                {
                    Task = task,
                    Model = model,
                    SystemPrompt = system_prompt,
                    EnableBash = enable_bash,
                    EnableFileWrites = enable_file_writes,
                    Timeout = timeout_seconds.HasValue ? TimeSpan.FromSeconds(timeout_seconds.Value) : null
                };

                if (image_paths is { Length: > 0 } paths)
                {
                    var loadResult = await ImageLoader.LoadAsync(manager.WorkDirectory, paths, executionCt).ConfigureAwait(false);
                    if (!loadResult.Success)
                    {
                        return WriteJson(w =>
                        {
                            w.WriteStartObject();
                            w.WriteString("error", loadResult.Error ?? "Failed to load image paths.");
                            w.WriteEndObject();
                        });
                    }
                    request.Images = loadResult.Attachments;
                }

                info = await manager.StartAsync(request, executionCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("error", ex.Message);
                    w.WriteEndObject();
                });
            }

            if (string.IsNullOrEmpty(info.Id) && info.Status == SubAgentStatus.Failed)
            {
                return WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("error", info.Error ?? string.Empty);
                    w.WriteEndObject();
                });
            }

            return WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("id", info.Id);
                w.WriteString("status", info.Status.ToString());
                w.WriteEndObject();
            });
        }

        async Task<string> AwaitSubAgentsAsync(string[]? ids = null)
        {
            var results = await manager.AwaitAsync(ids, executionCt).ConfigureAwait(false);
            return WriteJson(w =>
            {
                w.WriteStartArray();
                foreach (var info in results)
                {
                    w.WriteStartObject();
                    w.WriteString("id", info.Id);
                    w.WriteString("status", info.Status.ToString());
                    WriteNullableString(w, "summary", info.Summary);
                    WriteNullableString(w, "error", info.Error);
                    WriteNullableLong(w, "input_tokens", info.InputTokens);
                    WriteNullableLong(w, "output_tokens", info.OutputTokens);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        string GetSubAgentStatus(string? id = null)
        {
            var results = manager.GetStatus(id);
            return WriteJson(w =>
            {
                w.WriteStartArray();
                foreach (var info in results)
                {
                    w.WriteStartObject();
                    w.WriteString("id", info.Id);
                    w.WriteString("status", info.Status.ToString());
                    w.WriteString("started_at", ToIso(info.StartedAt));
                    if (info.CompletedAt.HasValue)
                        w.WriteString("completed_at", ToIso(info.CompletedAt.Value));
                    else
                        w.WriteNull("completed_at");
                    WriteNullableString(w, "model", info.Model);
                    WriteNullableString(w, "summary", info.Summary);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        string ListSubAgentModels()
        {
            if (options.AvailableModels.Count == 0)
            {
                return WriteJson(w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("models");
                    w.WriteEndArray();
                    w.WriteString("message", "No sub-agent models configured; the default model is used.");
                    w.WriteEndObject();
                });
            }

            return WriteJson(w =>
            {
                w.WriteStartArray();
                foreach (var model in options.AvailableModels)
                {
                    w.WriteStartObject();
                    w.WriteString("id", model.Id);
                    WriteNullableString(w, "description", model.Description);
                    if (model.ContextWindow.HasValue)
                        w.WriteNumber("context_window", model.ContextWindow.Value);
                    else
                        w.WriteNull("context_window");
                    w.WriteBoolean("supports_vision", model.SupportsVision);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        return new List<AITool>
        {
            AIFunctionFactory.Create(
                (Func<string, string?, string?, bool?, bool?, int?, string[]?, Task<string>>)StartSubAgentAsync,
                "start_sub_agent",
                StartDescription),
            AIFunctionFactory.Create(
                (Func<string[]?, Task<string>>)AwaitSubAgentsAsync,
                "await_sub_agents",
                AwaitDescription),
            AIFunctionFactory.Create(
                (Func<string?, string>)GetSubAgentStatus,
                "get_sub_agent_status",
                StatusDescription),
            AIFunctionFactory.Create(
                (Func<string>)ListSubAgentModels,
                "list_sub_agent_models",
                ModelsDescription)
        };
    }

    private static string ToIso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteNullableLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
