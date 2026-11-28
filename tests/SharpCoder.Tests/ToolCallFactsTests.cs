using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SharpCoder.Tests;

/// <summary>
/// Verifies that built-in tools publish exact metrics as structured facts (never leaving hosts to
/// parse the human-readable result text) and that the effective bash timeout is configurable.
/// </summary>
public class ToolCallFactsTests
{
    private static AgentOptions Options(string workDir) => new()
    {
        WorkDirectory = workDir,
        EnableBash = true,
        EnableFileOps = true,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        ShowToolCallsInStream = true,
        EmitStructuredToolUpdates = true,
        BashTimeoutMs = 42_000,
    };

    [Fact]
    public async Task ReadFile_PublishesLineRangeAndCharacterCount()
    {
        var dir = TempDir.Create();
        try
        {
            var file = Path.Combine(dir, "sample.txt");
            await File.WriteAllTextAsync(file, "alpha\nbeta\ngamma\n", TestContext.Current.CancellationToken);

            var completed = await RunSingleCallAsync(dir, "read_file",
                new Dictionary<string, object?> { ["filePath"] = "sample.txt", ["offset"] = 2, ["limit"] = 2 },
                TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(2, completed.Facts!.StartLine);
            Assert.Equal(3, completed.Facts.EndLine);
            // Lines 2-3 are "beta" (4) and "gamma" (5): raw content length, no "N: " prefixes.
            Assert.Equal("beta".Length + "gamma".Length, completed.Facts.CharactersRead);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task EditFile_PublishesOccurrencesAndCharacterDelta()
    {
        var dir = TempDir.Create();
        try
        {
            var file = Path.Combine(dir, "sample.txt");
            await File.WriteAllTextAsync(file, "hello world", TestContext.Current.CancellationToken);

            var completed = await RunSingleCallAsync(dir, "edit_file", new Dictionary<string, object?>
            {
                ["filePath"] = "sample.txt",
                ["oldString"] = "world",
                ["newString"] = "there!!",
            }, TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(1, completed.Facts!.OccurrencesReplaced);
            Assert.Equal("world".Length, completed.Facts.CharactersRemoved);
            Assert.Equal("there!!".Length, completed.Facts.CharactersAdded);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task WriteFile_PublishesCharacterCount()
    {
        var dir = TempDir.Create();
        try
        {
            var completed = await RunSingleCallAsync(dir, "write_file", new Dictionary<string, object?>
            {
                ["filePath"] = "new.txt",
                ["content"] = "12345",
            }, TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(5, completed.Facts!.CharactersWritten);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task Bash_PublishesConfiguredDefaultTimeoutAndExitCode()
    {
        var dir = TempDir.Create();
        try
        {
            var completed = await RunSingleCallAsync(dir, "execute_bash_command",
                new Dictionary<string, object?> { ["command"] = "exit 7" },
                TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(42_000, completed.Facts!.TimeoutMs);
            Assert.True(completed.Facts.TimeoutIsDefault);
            Assert.Equal(7, completed.Facts.ExitCode);
            Assert.False(completed.Facts.TimedOut);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task Bash_ExplicitTimeout_IsNotMarkedAsDefault()
    {
        var dir = TempDir.Create();
        try
        {
            var completed = await RunSingleCallAsync(dir, "execute_bash_command", new Dictionary<string, object?>
            {
                ["command"] = "echo ok",
                ["timeout_ms"] = 5000,
            }, TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(5000, completed.Facts!.TimeoutMs);
            Assert.False(completed.Facts.TimeoutIsDefault);
            Assert.Equal(0, completed.Facts.ExitCode);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task Glob_PublishesMatchCount()
    {
        var dir = TempDir.Create();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.cs"), "x", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(dir, "b.cs"), "x", TestContext.Current.CancellationToken);

            var completed = await RunSingleCallAsync(dir, "glob",
                new Dictionary<string, object?> { ["pattern"] = "*.cs" },
                TestContext.Current.CancellationToken);

            Assert.NotNull(completed.Facts);
            Assert.Equal(2, completed.Facts!.MatchCount);
        }
        finally { TempDir.Delete(dir); }
    }

    /// <summary>Runs one tool call through the real streaming loop and returns its completed update.</summary>
    private static async Task<ToolCallUpdate> RunSingleCallAsync(
        string dir, string toolName, Dictionary<string, object?> args, CancellationToken ct)
    {
        var client = new SingleCallClient(toolName, args);
        var agent = new CodingAgent(client, Options(dir));
        var updates = new List<StreamingUpdate>();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "go", ct))
            updates.Add(update);

        var finished = Assert.Single(updates, u => u.Kind == StreamingUpdateKind.ToolCallCompleted);
        return finished.ToolCall!;
    }

    private static class TempDir
    {
        public static string Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "sharpcoder-facts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void Delete(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SingleCallClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _args;
        private int _callCount;

        public SingleCallClient(string toolName, Dictionary<string, object?> args)
        {
            _toolName = toolName;
            _args = args;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent("call_1", _toolName, _args)]
                };
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
            }
            else
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent("Done.")]
                };
                await Task.Yield();
                yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
