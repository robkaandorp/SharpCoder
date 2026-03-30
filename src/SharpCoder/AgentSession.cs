using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace SharpCoder;

/// <summary>
/// Represents a persistent, resumable conversation session with accumulated context.
/// Sessions can be saved to and loaded from disk for crash recovery.
/// </summary>
public sealed class AgentSession
{
    /// <summary>Unique identifier for this session.</summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Full conversation history (excludes system prompt — that's rebuilt each call).
    /// Contains User, Assistant, Tool messages from all turns.
    /// </summary>
    public IList<ChatMessage> MessageHistory { get; set; } = new List<ChatMessage>();

    /// <summary>Total tool calls across all turns in this session.</summary>
    public int TotalToolCalls { get; set; }

    /// <summary>Cumulative input tokens used across all turns.</summary>
    public long InputTokensUsed { get; set; }

    /// <summary>Cumulative output tokens used across all turns.</summary>
    public long OutputTokensUsed { get; set; }

    /// <summary>When this session was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the last message was sent/received.</summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Exact input token count from the most recent API response.
    /// Represents the actual context size the model processed, including system prompt,
    /// message history, tool definitions, and all overhead. Updated after each API call.
    /// Zero until the first API response is received.
    /// </summary>
    public long LastKnownContextTokens { get; set; }

    /// <summary>
    /// Estimated token count of the current message history.
    /// Uses a rough heuristic (~4 chars per token).
    /// </summary>
    public long EstimatedContextTokens
    {
        get
        {
            long chars = 0;
            foreach (var msg in MessageHistory)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is TextContent tc)
                        chars += tc.Text?.Length ?? 0;
                    else if (content is FunctionCallContent fc)
                        chars += (fc.Name?.Length ?? 0) + EstimateArgumentsLength(fc);
                    else if (content is FunctionResultContent fr)
                        chars += EstimateResultLength(fr);
                }
            }
            return chars / 4; // ~4 chars per token heuristic
        }
    }

    /// <summary>Save session state to a JSON file.</summary>
    public async Task SaveAsync(string filePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var data = new SessionData
        {
            SessionId = SessionId,
            TotalToolCalls = TotalToolCalls,
            InputTokensUsed = InputTokensUsed,
            OutputTokensUsed = OutputTokensUsed,
            CreatedAt = CreatedAt,
            LastActivityAt = LastActivityAt,
            Messages = MessageHistory,
            LastKnownContextTokens = LastKnownContextTokens
        };

        var json = JsonSerializer.Serialize(data, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>Load session state from a JSON file.</summary>
    public static async Task<AgentSession> LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Session file not found: {filePath}", filePath);

        var json = await File.ReadAllTextAsync(filePath, ct);
        var data = JsonSerializer.Deserialize<SessionData>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session data.");

        return new AgentSession
        {
            SessionId = data.SessionId ?? Guid.NewGuid().ToString("N"),
            MessageHistory = data.Messages ?? new List<ChatMessage>(),
            TotalToolCalls = data.TotalToolCalls,
            InputTokensUsed = data.InputTokensUsed,
            OutputTokensUsed = data.OutputTokensUsed,
            CreatedAt = data.CreatedAt,
            LastActivityAt = data.LastActivityAt,
            LastKnownContextTokens = data.LastKnownContextTokens
        };
    }

    /// <summary>Create a new empty session.</summary>
    public static AgentSession Create(string? sessionId = null)
    {
        return new AgentSession
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Creates a deep copy of this session with a new session ID, zeroed token counters,
    /// and fresh timestamps. The message history is deep-copied via JSON serialization so
    /// mutations to either session's history do not affect the other.
    /// <c>LastKnownContextTokens</c> is copied from the original.
    /// </summary>
    /// <param name="sessionId">Optional custom session ID for the forked session. If null, a new GUID is generated.</param>
    /// <returns>A new <see cref="AgentSession"/> branched from the current state.</returns>
    public AgentSession Fork(string? sessionId = null)
    {
        // Deep-copy MessageHistory via JSON round-trip using the same options that handle
        // ChatMessage / AIContent polymorphism (TextContent, FunctionCallContent, etc.).
        var json = JsonSerializer.Serialize(MessageHistory, SerializerOptions);
        var clonedHistory = JsonSerializer.Deserialize<IList<ChatMessage>>(json, SerializerOptions)
            ?? new List<ChatMessage>();

        var now = DateTimeOffset.UtcNow;
        return new AgentSession
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString("N"),
            MessageHistory = clonedHistory,
            TotalToolCalls = 0,
            InputTokensUsed = 0,
            OutputTokensUsed = 0,
            LastKnownContextTokens = LastKnownContextTokens,
            CreatedAt = now,
            LastActivityAt = now
        };
    }

    /// <summary>Reset conversation history while preserving identity and counters.</summary>
    public void ClearHistory()
    {
        MessageHistory.Clear();
        LastKnownContextTokens = 0;
    }

    private static long EstimateArgumentsLength(FunctionCallContent fc)
    {
        if (fc.Arguments == null) return 0;
        long len = 0;
        foreach (var kvp in fc.Arguments)
        {
            len += kvp.Key?.Length ?? 0;
            len += kvp.Value?.ToString()?.Length ?? 0;
        }
        return len;
    }

    private static long EstimateResultLength(FunctionResultContent fr)
    {
        if (fr.Result == null) return 0;
        return fr.Result.ToString()?.Length ?? 0;
    }

    // Use AIJsonUtilities.DefaultOptions which has built-in converters for
    // ChatMessage, AIContent polymorphism (TextContent, FunctionCallContent, etc.)
    private static readonly JsonSerializerOptions SerializerOptions = new(AIJsonUtilities.DefaultOptions)
    {
        WriteIndented = true
    };

    /// <summary>Internal DTO for JSON serialization.</summary>
    private sealed class SessionData
    {
        public string? SessionId { get; set; }
        public IList<ChatMessage>? Messages { get; set; }
        public int TotalToolCalls { get; set; }
        public long InputTokensUsed { get; set; }
        public long OutputTokensUsed { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }
        public long LastKnownContextTokens { get; set; }
    }
}
