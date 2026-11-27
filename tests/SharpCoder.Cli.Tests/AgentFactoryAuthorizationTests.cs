using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SharpCoder.Cli.Tests;

/// <summary>
/// Verifies that AgentFactory wires AuthorizeTool and that a denial actually stops a side effect
/// and becomes a model-visible refusal (the library's fail-closed boundary).
/// </summary>
public class AgentFactoryAuthorizationTests
{
    [Fact]
    public async Task CreateAsync_DeniedWriteFile_DoesNotWriteAndModelSeesRefusal()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "output.txt");
        var options = CliOptions.Parse(new[] { "run", "-w", dir.Path, "--confirm-tools", "always", "task" })!;
        var prompter = new DenyingPrompter();
        var client = new ScriptedClient(
            new ScriptedClient.Round
            {
                ToolName = "write_file",
                ToolArgs = new Dictionary<string, object?>
                {
                    ["filePath"] = target,
                    ["content"] = "should never be written",
                },
            },
            new ScriptedClient.Round { Text = "Acknowledged." });

        await using var factory = new AgentFactory(
            options,
            new ConsoleUi(),
            new EnvironmentDigest((_, _, _, _) => null),
            _ => client,
            prompter);
        var (agent, _) = await factory.CreateAsync();

        var session = AgentSession.Create();
        await agent.ExecuteAsync(session, "write the file", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(target), "a denied write_file must not produce a file");
        Assert.Equal(1, prompter.AskCount);
        Assert.Contains("declined this tool call", string.Join(" | ", client.ToolResults));
        Assert.Contains("was not executed", string.Join(" | ", client.ToolResults));
    }

    [Fact]
    public async Task CreateAsync_YesFlag_AllowsWriteWithoutPrompt()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "output.txt");
        var options = CliOptions.Parse(new[] { "run", "-w", dir.Path, "-y", "task" })!;
        var prompter = new DenyingPrompter();
        var client = new ScriptedClient(
            new ScriptedClient.Round
            {
                ToolName = "write_file",
                ToolArgs = new Dictionary<string, object?>
                {
                    ["filePath"] = target,
                    ["content"] = "written",
                },
            },
            new ScriptedClient.Round { Text = "Acknowledged." });

        await using var factory = new AgentFactory(
            options,
            new ConsoleUi(),
            new EnvironmentDigest((_, _, _, _) => null),
            _ => client,
            prompter);
        var (agent, _) = await factory.CreateAsync();

        var session = AgentSession.Create();
        await agent.ExecuteAsync(session, "write the file", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(target));
        Assert.Equal("written", File.ReadAllText(target));
        Assert.Equal(0, prompter.AskCount);
    }

    private sealed class DenyingPrompter : IToolConfirmationPrompter
    {
        public int AskCount { get; private set; }

        public ToolConfirmationAnswer Ask(ToolAuthorizationContext context, CancellationToken ct)
        {
            AskCount++;
            return ToolConfirmationAnswer.No;
        }
    }

    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private int _index;

        public ScriptedClient(params Round[] rounds) => _rounds = rounds;

        public List<string> ToolResults { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToList();
            foreach (var message in snapshot)
                foreach (var content in message.Contents)
                    if (content is FunctionResultContent result)
                        ToolResults.Add(result.Result?.ToString() ?? "(null)");

            var round = _rounds[_index++];
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                { FinishReason = ChatFinishReason.ToolCalls });
            }

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, round.Text ?? string.Empty))
            { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        internal sealed class Round
        {
            public string? ToolName;
            public Dictionary<string, object?>? ToolArgs;
            public string? Text;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sharpcoder-cli-auth-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }
}
