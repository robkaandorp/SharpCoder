using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Independent coverage for the round-2 sub-agent propagation of the tool-authorization pair,
/// authored by the test phase.
/// <para>
/// Where the coder's <see cref="ToolAuthorizationPropagationTests"/> asserts the POLICY (which pair a
/// child inherits, and that a mutated option never reaches it), these tests assert the OBSERVABLE
/// EFFECT of that policy: that a denied call performs no side effect at all, that the denial
/// happens before the effect, and that the effect is genuinely absent rather than merely hidden.
/// </para>
/// <para>
/// The "no file access" proofs use an EXCLUSIVE FILE LOCK: while the test holds the file open with
/// <see cref="FileShare.None"/>, any attempt to read it fails with an <see cref="IOException"/>. A
/// denied read therefore produces a refusal instead of the lock error — the difference between the
/// two outcomes is the proof that the file was never opened.
/// </para>
/// </summary>
public class ToolAuthorizationPropagationBoundaryTests
{
    // ========================================================================
    // Fakes and fixtures
    // ========================================================================

    /// <summary>One scripted round: a tool call, or final assistant text.</summary>
    private sealed class Round
    {
        public string? ToolName { get; init; }
        public Dictionary<string, object?>? ToolArgs { get; init; }
        public string? Text { get; init; }

        public static Round Call(string name, Dictionary<string, object?> args) =>
            new() { ToolName = name, ToolArgs = args };

        public static Round Call(string name) =>
            new() { ToolName = name, ToolArgs = new Dictionary<string, object?>() };

        public static Round Say(string text) => new() { Text = text };
    }

    /// <summary>
    /// Chat client that replays a script and records the tool results the MODEL saw. The results
    /// are read from the messages the client is handed, so they are exactly what the model saw.
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private readonly Action<int>? _beforeRound;
        private int _index;

        public ScriptedClient(params Round[] rounds) : this((IReadOnlyList<Round>)rounds, null) { }

        /// <summary>Creates a client with no per-round hook.</summary>
        public ScriptedClient(IReadOnlyList<Round> rounds) : this(rounds, null) { }

        /// <summary>
        /// Creates a client that invokes <paramref name="beforeRound"/> with the round index BEFORE
        /// serving that round, so a test can install a seam (or mutate the options) at an exact
        /// point in the parent's turn sequence.
        /// </summary>
        public ScriptedClient(IReadOnlyList<Round> rounds, Action<int>? beforeRound)
        {
            _rounds = rounds;
            _beforeRound = beforeRound;
        }

        public ChatOptions? LastOptions { get; private set; }
        public List<string> ToolResults { get; } = [];
        public int CallCount => _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            Record(messages);
            var round = Next();
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"parent_call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "parent done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            Record(messages);
            var round = Next();
            ChatResponse response;
            if (round.ToolName is not null)
            {
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"parent_call_{_index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }
            else
            {
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "parent done"))
                {
                    FinishReason = ChatFinishReason.Stop
                };
            }

            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            foreach (var message in messages)
                foreach (var content in message.Contents)
                    if (content is FunctionResultContent result)
                        ToolResults.Add(ResultToString(result.Result));
        }

        private Round Next()
        {
            var index = _index++;
            _beforeRound?.Invoke(index);
            return index < _rounds.Count ? _rounds[index] : Round.Say("parent done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Chat client for a CHILD agent; records the ChatOptions the child sent.</summary>
    private sealed class ChildClient : IChatClient
    {
        private readonly IReadOnlyList<Round> _rounds;
        private int _index;

        public ChildClient(params Round[] rounds) => _rounds = rounds;

        /// <summary>When true, the script REPEATS for every child, so a second child replays the
        /// same rounds instead of running off the end of the script.</summary>
        public bool RepeatScript { get; init; }

        public ConcurrentQueue<ChatOptions?> Options { get; } = new();
        public List<string> ToolResults { get; } = [];
        public int CallCount => _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Enqueue(options);
            foreach (var message in messages)
                foreach (var content in message.Contents)
                    if (content is FunctionResultContent result)
                        ToolResults.Add(ResultToString(result.Result));

            var index = _index++;
            var round = index < _rounds.Count
                ? _rounds[index]
                : RepeatScript && _rounds.Count > 0
                    ? _rounds[index % _rounds.Count]
                    : Round.Say("child done");
            if (round.ToolName is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"child_call_{index}", round.ToolName, round.ToolArgs ?? new())]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, round.Text ?? "child done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string ResultToString(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText(),
        _ => result.ToString() ?? string.Empty
    };

    /// <summary>A fresh, unique work directory for one test; deleted when the test ends.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpcoder-authb2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AgentOptions ParentOptions(string workDirectory) => new()
    {
        WorkDirectory = workDirectory,
        EnableBash = false,
        EnableFileOps = true,
        EnableFileWrites = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are a test agent.",
    };

    private static SubAgentOptions SubOptions(IChatClient childClient) => new()
    {
        DefaultClient = childClient,
        DefaultEnableBash = false,
        DefaultEnableFileOps = true,
        DefaultEnableFileWrites = false,
        DefaultEnableSkills = false,
    };

    private static AIFunction FunctionNamed(ChatOptions? options, string name) =>
        (options?.Tools ?? []).OfType<AIFunction>().Single(f => f.Name == name);

    private static IReadOnlyList<Round> DelegateScript(string subTask = "inspect the workspace") =>
    [
        Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = subTask }),
        Round.Call("await_sub_agents"),
        Round.Say("parent finished"),
    ];

    /// <summary>Records every authorization check the host callback received.</summary>
    private sealed class AuthorizationLog
    {
        public ConcurrentQueue<ToolAuthorizationContext> Calls { get; } = new();

        public IReadOnlyList<ToolAuthorizationContext> All => Calls.ToArray();

        public IEnumerable<ToolAuthorizationContext> ForTool(string toolName) =>
            All.Where(c => string.Equals(c.ToolName, toolName, StringComparison.Ordinal));
    }

    // ========================================================================
    // 1. Denied child read_file performs NO file access (exclusive-lock proof)
    // ========================================================================

    [Fact]
    public async Task DeniedChildReadFile_PerformsNoFileAccess_ExclusiveLockProof()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "locked.txt");
        await File.WriteAllTextAsync(target, "CONTENT", TestContext.Current.CancellationToken);

        // While this handle is open with FileShare.None, ANY read of the file fails with an
        // IOException. So the observable difference between "the file was opened" and "the file
        // was never opened" is the presence of the lock error in the tool result.
        using var exclusiveHold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = target }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) => Task.FromResult(
            string.Equals(context.ToolName, "read_file", StringComparison.Ordinal)
                ? ToolAuthorizationDecision.Deny("That file is off limits to delegated work.")
                : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var results = child.ToolResults;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            // The refusal, and NOT the lock error an actual read would have produced.
            Assert.Contains("That file is off limits to delegated work.", result);
            Assert.DoesNotContain("being used by another process", result);
            Assert.DoesNotContain("CONTENT", result);
        }
    }

    [Fact]
    public async Task AllowedChildReadFile_PerformsTheFileAccess_ExclusiveLockControl()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "locked.txt");
        await File.WriteAllTextAsync(target, "CONTENT", TestContext.Current.CancellationToken);

        using var exclusiveHold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = target }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The CONTROL: the same setup with an ALLOWED read DOES reach the file, so the lock error
        // appears. Without this control, "no lock error" in the denied test could be vacuous.
        var results = child.ToolResults;
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Contains("being used by another process"));
    }

    [Fact]
    public async Task DeniedParentReadFile_PerformsNoFileAccess_ExclusiveLockProof()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "locked.txt");
        await File.WriteAllTextAsync(target, "CONTENT", TestContext.Current.CancellationToken);

        using var exclusiveHold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var parent = ParentOptions(dir.Path);
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("No reads here."));

        var client = new ScriptedClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = target }),
            Round.Say("done"));
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("read it", TestContext.Current.CancellationToken);

        var result = Assert.Single(client.ToolResults);
        Assert.Contains("No reads here.", result);
        Assert.DoesNotContain("being used by another process", result);
    }

    // ========================================================================
    // 2. Denied start_sub_agent spawns NO child (nothing created or started)
    // ========================================================================

    [Fact]
    public async Task DeniedStartSubAgent_CreatesNoEntryAndStartsNoRunner()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("must never run"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) => Task.FromResult(
            string.Equals(context.ToolName, "start_sub_agent", StringComparison.Ordinal)
                ? ToolAuthorizationDecision.Deny("Delegation is not allowed.")
                : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "inspect" }),
            Round.Say("parent finished"));
        await using var agent = new CodingAgent(client, parent);

        var result = await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);
        Assert.Equal("Success", result.Status);

        var manager = agent.ActiveSubAgentManager!;

        // NOTHING was created: the manager tracks no entry at all, so no run was accepted and no
        // runner task was started.
        Assert.Empty(manager.GetStatus());

        // No child agent was ever constructed.
        Assert.Null(manager.LastChildOptions);

        // The child model was never called.
        Assert.Equal(0, child.CallCount);
        Assert.Empty(child.Options);

        // And the model received the refusal, not a sub-agent id.
        var refusal = Assert.Single(client.ToolResults);
        Assert.Contains("Delegation is not allowed.", refusal);
        Assert.DoesNotContain("sub-", refusal);
    }

    [Fact]
    public async Task DeniedStartSubAgent_OnManualStreamingPath_CreatesNoEntry()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("must never run"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ShowToolCallsInStream = true;
        parent.AuthorizeTool = (context, _) => Task.FromResult(
            string.Equals(context.ToolName, "start_sub_agent", StringComparison.Ordinal)
                ? ToolAuthorizationDecision.Deny("Delegation is not allowed.")
                : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "inspect" }),
            Round.Say("parent finished"));
        await using var agent = new CodingAgent(client, parent);

        var text = new System.Text.StringBuilder();
        await foreach (var update in agent.ExecuteStreamingAsync(null, "delegate", TestContext.Current.CancellationToken))
        {
            if (update.Kind == StreamingUpdateKind.TextDelta) text.Append(update.Text);
        }

        var manager = agent.ActiveSubAgentManager!;
        Assert.Empty(manager.GetStatus());
        Assert.Null(manager.LastChildOptions);
        Assert.Equal(0, child.CallCount);
        Assert.Contains("Delegation is not allowed.", text.ToString());
    }

    // ========================================================================
    // 3. The child's policy is the pair captured at ACCEPTANCE, not the live options
    // ========================================================================

    [Fact]
    public async Task AcceptedChild_UsesTheAcceptanceTimePair_EvenWhenTheOptionsChangeBeforeItRuns()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "accepted-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var replacementCalls = 0;
        CodingAgent? agentRef = null;

        // The mutation happens inside the child-options hook, i.e. AFTER the RunSpec was captured
        // at acceptance and IMMEDIATELY BEFORE the child CodingAgent is constructed. The child must
        // still run under the accepted pair.
        var client = new ScriptedClient(DelegateScript(), index =>
        {
            if (index != 0) return;
            agentRef!.ActiveSubAgentManager!.OnSubAgentOptionsCreated = _ =>
            {
                parent.AuthorizeTool = (_, _) =>
                {
                    replacementCalls++;
                    return Task.FromResult(ToolAuthorizationDecision.Deny("replacement after acceptance"));
                };
                parent.ToolAuthorizationCorrelationId = "replaced-user";
            };
        });

        await using var agent = new CodingAgent(client, parent);
        agentRef = agent;

        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The child's OWN read_file ran under the accepted correlation and the accepted callback.
        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal("accepted-user", childRead.CorrelationId);
        Assert.Equal(0, replacementCalls);
    }

    [Fact]
    public async Task AcceptedChild_KeepsItsPolicyWhenTheHostDisablesAuthorizationAfterAcceptance()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "b.txt" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "still-guarded";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        CodingAgent? agentRef = null;
        var client = new ScriptedClient(DelegateScript(), index =>
        {
            if (index != 0) return;
            agentRef!.ActiveSubAgentManager!.OnSubAgentOptionsCreated = _ =>
            {
                // Disable authorization entirely after acceptance.
                parent.AuthorizeTool = null;
                parent.ToolAuthorizationCorrelationId = null;
            };
        });

        await using var agent = new CodingAgent(client, parent);
        agentRef = agent;

        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The accepted child is still guarded: its own read went through the boundary.
        var childRead = Assert.Single(log.ForTool("read_file"));
        Assert.Equal("still-guarded", childRead.CorrelationId);
    }

    // ========================================================================
    // 4. Argument-aware denial: the child's context exposes the arguments
    // ========================================================================

    [Fact]
    public async Task DeniedChildTool_SeesTheConcretePathArgument()
    {
        using var dir = new TempDir();
        var allowedPath = Path.Combine(dir.Path, "allowed.txt");
        var deniedPath = Path.Combine(dir.Path, "denied.txt");
        await File.WriteAllTextAsync(allowedPath, "ALLOWED", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(deniedPath, "DENIED", TestContext.Current.CancellationToken);

        var seen = new List<(string Path, string? Correlation)>();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = deniedPath }),
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = allowedPath }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "arg-user";
        parent.AuthorizeTool = (context, _) =>
        {
            if (!string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                return Task.FromResult(ToolAuthorizationDecision.Allow);

            var path = context.Arguments.TryGetValue("filePath", out var value) ? value as string : null;
            seen.Add((path ?? "<null>", context.CorrelationId));
            return Task.FromResult(path == deniedPath
                ? ToolAuthorizationDecision.Deny("denied.txt is off limits.")
                : ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The callback saw BOTH concrete paths, with the child's correlation.
        Assert.Equal(2, seen.Count);
        Assert.Equal(deniedPath, seen[0].Path);
        Assert.Equal(allowedPath, seen[1].Path);
        Assert.All(seen, s => Assert.Equal("arg-user", s.Correlation));

        // The denied path's contents never reached the child model; the allowed one did.
        var results = child.ToolResults;
        Assert.Contains(results, r => r.Contains("denied.txt is off limits."));
        Assert.DoesNotContain("DENIED", string.Join("|||", results));
        Assert.Contains(results, r => r.Contains("ALLOWED"));
    }

    [Fact]
    public async Task DeniedChildBashCommand_SeesTheConcreteCommandArgument()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "bash-marker.txt");

        var seenCommand = new List<string?>();
        var child = new ChildClient(
            Round.Call("execute_bash_command", new Dictionary<string, object?> { ["command"] = $"echo ran > \"{marker}\"" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.EnableBash = true;
        var subOptions = SubOptions(child);
        subOptions.DefaultEnableBash = true;
        parent.SubAgents = subOptions;
        parent.AuthorizeTool = (context, _) =>
        {
            if (!string.Equals(context.ToolName, "execute_bash_command", StringComparison.Ordinal))
                return Task.FromResult(ToolAuthorizationDecision.Allow);

            seenCommand.Add(context.Arguments.TryGetValue("command", out var value) ? value as string : null);
            return Task.FromResult(ToolAuthorizationDecision.Deny("Shell access is not permitted."));
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The callback saw the concrete command.
        Assert.Equal($"echo ran > \"{marker}\"", Assert.Single(seenCommand));

        // And the command never ran.
        Assert.False(File.Exists(marker), "the denied child bash command must not have run");
        Assert.Contains("Shell access is not permitted.", string.Join("|||", child.ToolResults));
    }

    // ========================================================================
    // 5. Both dispatch paths: the child's fault is sanitized on both
    // ========================================================================

    [Fact]
    public async Task ChildCallbackFault_OnFunctionInvokingPath_IsSanitizedAndFailsClosed()
    {
        using var dir = new TempDir();
        var secretPath = Path.Combine(dir.Path, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "CHILD-SECRET-CONTENT", TestContext.Current.CancellationToken);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = secretPath }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                throw new InvalidOperationException("SECRET-CHILD-DETAIL-501");
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var results = child.ToolResults;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.Contains("could not be authorized", result);
            Assert.DoesNotContain("SECRET-CHILD-DETAIL-501", result);
            Assert.DoesNotContain(nameof(InvalidOperationException), result);
            Assert.DoesNotContain("CHILD-SECRET-CONTENT", result);
        }
    }

    [Fact]
    public async Task ChildCallbackFault_DoesNotFailTheChildRun()
    {
        using var dir = new TempDir();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }),
            Round.Say("child finished its work"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                throw new InvalidOperationException("SECRET-CHILD-DETAIL-502");
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The fail-closed refusal is a refusal, not a run failure: the child still completed.
        var status = agent.ActiveSubAgentManager!.GetStatus();
        Assert.Single(status);
        Assert.Equal(SubAgentStatus.Completed, status[0].Status);
        Assert.DoesNotContain("SECRET-CHILD-DETAIL-502", status[0].Summary ?? string.Empty);
    }

    // ========================================================================
    // 6. Direct guarded invocation of a CHILD function
    // ========================================================================

    [Fact]
    public async Task ChildGuardedFunction_DirectlyInvoked_Denied_ReadsNoFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "locked.txt");
        await File.WriteAllTextAsync(target, "MUST-NOT-APPEAR", TestContext.Current.CancellationToken);

        using var exclusiveHold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var child = new ChildClient(Round.Say("child done"));
        var denying = false;

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (_, _) => Task.FromResult(denying
            ? ToolAuthorizationDecision.Deny("Direct child reads are blocked.")
            : ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var childTools = child.Options.ToArray().Last()!;
        var readFile = FunctionNamed(childTools, "read_file");
        denying = true;

        var result = await readFile.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["filePath"] = target }),
            TestContext.Current.CancellationToken);

        var text = ResultToString(result);
        Assert.Contains("Direct child reads are blocked.", text);
        // The file was never opened: no lock error, no contents.
        Assert.DoesNotContain("being used by another process", text);
        Assert.DoesNotContain("MUST-NOT-APPEAR", text);
    }

    [Fact]
    public async Task ChildGuardedFunction_DirectlyInvoked_Allowed_ReadsTheFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "ok.txt");
        await File.WriteAllTextAsync(target, "DIRECT-CHILD-CONTENT", TestContext.Current.CancellationToken);

        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var childTools = child.Options.ToArray().Last()!;
        var readFile = FunctionNamed(childTools, "read_file");

        var result = await readFile.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["filePath"] = target }),
            TestContext.Current.CancellationToken);

        Assert.Contains("DIRECT-CHILD-CONTENT", ResultToString(result));
    }

    // ========================================================================
    // 7. The guarantee boundary: the raw FileTools are NOT guarded
    // ========================================================================

    [Fact]
    public async Task RawFileTools_AreNotGuarded_AndBypassTheCallback()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "raw.txt");
        await File.WriteAllTextAsync(target, "RAW-CONTENT", TestContext.Current.CancellationToken);

        var authorizations = 0;
        var parent = ParentOptions(dir.Path);
        parent.AuthorizeTool = (_, _) =>
        {
            authorizations++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("never"));
        };

        var client = new ScriptedClient(Round.Say("done"));
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The raw FileTools instance is a TRUSTED HOST ENTRY POINT: the callback never sees it.
        // The guarantee covers only the functions SharpCoder registers in ChatOptions.
        var rawTools = new SharpCoder.Tools.FileTools(dir.Path);
        var raw = await rawTools.read_file(target, ct: TestContext.Current.CancellationToken);

        Assert.Contains("RAW-CONTENT", raw);
        Assert.Equal(0, authorizations);
    }

    // ========================================================================
    // 8. The lazy manager: the constructor-captured pair, not the live options
    // ========================================================================

    [Fact]
    public async Task LazyManager_UsesTheConstructorCapturedPair_EvenWhenBothMembersChanged()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = "construction-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);

        // The manager does not exist yet.
        Assert.Null(agent.ActiveSubAgentManager);

        // Change BOTH members after construction but before the first execution.
        var liveCalls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            liveCalls++;
            return Task.FromResult(ToolAuthorizationDecision.Deny("live options policy"));
        };
        parent.ToolAuthorizationCorrelationId = "live-user";

        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The live callback was never invoked, and every check carried the construction-time
        // correlation — for the parent's own tools AND for the child's.
        Assert.Equal(0, liveCalls);
        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("construction-user", c.CorrelationId));

        var childOptions = agent.ActiveSubAgentManager!.LastChildOptions;
        Assert.NotNull(childOptions);
        Assert.Equal("construction-user", childOptions!.ToolAuthorizationCorrelationId);
    }

    [Fact]
    public async Task LazyManager_WithAuthorizationOff_CannotAdoptALaterEnabledCallback()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // Constructed with authorization OFF.

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);

        var calls = 0;
        parent.AuthorizeTool = (_, _) =>
        {
            calls++;
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };
        parent.ToolAuthorizationCorrelationId = "added-later";

        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // Neither the parent nor its children gained a policy they were not constructed with.
        Assert.Equal(0, calls);
        Assert.Null(agent.ActiveSubAgentManager!.LastChildOptions!.AuthorizeTool);
        Assert.Null(agent.ActiveSubAgentManager!.LastChildOptions!.ToolAuthorizationCorrelationId);

        var childTools = child.Options.ToArray().Last()!;
        foreach (var tool in childTools.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool), $"child tool '{tool.Name}' must stay unguarded");
    }

    // ========================================================================
    // 9. Non-AIFunction custom tools: fail closed only when authorization is on
    // ========================================================================

    [Fact]
    public async Task NonAIFunctionCustomTool_WithAuthorizationEnabled_FailsClosedOnTheParent()
    {
        using var dir = new TempDir();
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

        var parent = ParentOptions(dir.Path);
        parent.CustomTools = [AIFunctionFactory.CreateDeclaration("provider_tool", "provider-executed", schema, null)];
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

        var client = new ScriptedClient(Round.Say("done"));
        await using var agent = new CodingAgent(client, parent);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.ExecuteAsync("go", TestContext.Current.CancellationToken));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task NonAIFunctionCustomTool_WithoutAuthorization_KeepsCurrentShape()
    {
        using var dir = new TempDir();
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        var declaration = AIFunctionFactory.CreateDeclaration("provider_tool", "provider-executed", schema, null);

        var parent = ParentOptions(dir.Path);
        parent.CustomTools = [declaration];
        // No callback.

        var client = new ScriptedClient(Round.Say("done"));
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("go", TestContext.Current.CancellationToken);

        // The declaration is registered as-is, exactly as before.
        var registered = client.LastOptions!.Tools!.Single(t => t.Name == "provider_tool");
        Assert.Same(declaration, registered);
    }

    // ========================================================================
    // 11. Child-side fallbacks: missing / invalid / empty-reason decisions
    // ========================================================================

    [Fact]
    public async Task ChildCallbackReturningNullDecision_FallsBackToTheFixedSafeRefusal()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "secret.txt");
        await File.WriteAllTextAsync(target, "NULL-DECISION-SECRET", TestContext.Current.CancellationToken);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = target }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                return Task.FromResult<ToolAuthorizationDecision>(null!);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var results = child.ToolResults;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            // The FIXED, host-neutral refusal — no "Reason:" section, no file contents.
            Assert.Contains("was not executed", result);
            Assert.DoesNotContain("Reason:", result);
            Assert.DoesNotContain("NULL-DECISION-SECRET", result);
        }
    }

    [Fact]
    public async Task ChildCallbackReturningBlankReason_FallsBackToTheFixedSafeRefusal()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "secret.txt");
        await File.WriteAllTextAsync(target, "BLANK-REASON-SECRET", TestContext.Current.CancellationToken);

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = target }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                return Task.FromResult(ToolAuthorizationDecision.Deny("   "));
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var results = child.ToolResults;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.Contains("was not executed", result);
            Assert.DoesNotContain("Reason:", result);
            Assert.DoesNotContain("BLANK-REASON-SECRET", result);
        }
    }

    // ========================================================================
    // 12. Cancellation in the child path
    // ========================================================================

    [Fact]
    public async Task ChildCallbackCancellation_IsNotConvertedIntoADenialRefusal()
    {
        using var dir = new TempDir();
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = (context, _) =>
        {
            if (string.Equals(context.ToolName, "read_file", StringComparison.Ordinal))
                throw new OperationCanceledException("cancelled inside the child callback");
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The cancellation is never turned into the fixed denial refusal.
        var results = child.ToolResults;
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.DoesNotContain("was not executed", result);
            Assert.DoesNotContain("could not be authorized", result);
        }
    }

    // ========================================================================
    // 13. The callback is invoked concurrently from the parent and the child
    // ========================================================================

    [Fact]
    public async Task ParentAndChildCallbacks_ReallyOverlapInTime()
    {
        using var dir = new TempDir();

        // Both callbacks enter the gate and wait for the OTHER to arrive, so they are provably
        // inside the callback at the same time: the counter reaches 2 only while BOTH are parked
        // inside the callback body.
        var bothInside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;

        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }),
            Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.AuthorizeTool = async (context, _) =>
        {
            // Only the parent's OWN status check and the child's read participate: the parent's
            // start_sub_agent check must NOT block, or the child could never start.
            var participates = string.Equals(context.ToolName, "read_file", StringComparison.Ordinal)
                               || string.Equals(context.ToolName, "get_sub_agent_status", StringComparison.Ordinal);
            if (!participates)
                return ToolAuthorizationDecision.Allow;

            if (Interlocked.Increment(ref inside) == 2)
                bothInside.TrySetResult(true);

            // Wait for the other participant, bounded so a regression fails instead of hanging.
            await Task.WhenAny(bothInside.Task, Task.Delay(30, TestContext.Current.CancellationToken));

            Interlocked.Decrement(ref inside);
            return ToolAuthorizationDecision.Allow;
        };

        // The parent starts the child and then checks its status while the child is still working:
        // the parent's status check and the child's read_file therefore run concurrently.
        var client = new ScriptedClient(
        [
            Round.Call("start_sub_agent", new Dictionary<string, object?> { ["task"] = "inspect" }),
            Round.Call("get_sub_agent_status", new Dictionary<string, object?>()),
            Round.Call("await_sub_agents", new Dictionary<string, object?>()),
            Round.Say("parent finished")
        ], null);

        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // Both really were inside the callback at the same time.
        Assert.True(bothInside.Task.IsCompleted,
            "the parent's status check and the child's read_file must have been inside the callback at the same time");
    }

    // ========================================================================
    // 14. Correlation immutability for the host's own local variable
    // ========================================================================

    [Fact]
    public async Task Correlation_MutatingTheHostsOwnVariableNeverReachesTheCapturedValue()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        var child = new ChildClient(Round.Say("child done"));

        var hostSupplied = string.Concat("tenant-", "abc");
        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        parent.ToolAuthorizationCorrelationId = hostSupplied;
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);

        // "Mutate the original backing data": reassign the host's own variable AND the options member.
        hostSupplied = "tenant-mutated";
        parent.ToolAuthorizationCorrelationId = hostSupplied;

        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        // The captured correlation is the ORIGINAL value, for the parent AND the child.
        Assert.NotEmpty(log.All);
        Assert.All(log.All, c => Assert.Equal("tenant-abc", c.CorrelationId));

        var manager = agent.ActiveSubAgentManager!;
        Assert.Equal("tenant-abc", manager.ToolAuthorizationSnapshot!.CorrelationId);
        Assert.Equal("tenant-abc", manager.LastChildOptions!.ToolAuthorizationCorrelationId);
    }

    [Fact]
    public async Task HostCreatedManager_KeepsItsCorrelationAcrossASecondStartAfterAMutation()
    {
        using var dir = new TempDir();
        var log = new AuthorizationLog();
        // The child calls read_file, so the child's OWN tool call goes through the boundary and
        // produces an authorization check with the manager's correlation.
        var child = new ChildClient(
            Round.Call("read_file", new Dictionary<string, object?> { ["filePath"] = "a.txt" }),
            Round.Say("child done"))
        {
            RepeatScript = true
        };

        var parent = ParentOptions(dir.Path);
        parent.ToolAuthorizationCorrelationId = "manager-user";
        parent.AuthorizeTool = (context, _) =>
        {
            log.Calls.Enqueue(context);
            return Task.FromResult(ToolAuthorizationDecision.Allow);
        };

        var subOptions = SubOptions(child);
        await using var manager = new SubAgentManager(subOptions, child, parent);

        // Mutate AFTER the manager was created.
        parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Deny("too late"));
        parent.ToolAuthorizationCorrelationId = "too-late-user";

        await manager.StartAsync(new SubAgentRequest { Task = "one" }, TestContext.Current.CancellationToken);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);
        await manager.StartAsync(new SubAgentRequest { Task = "two" }, TestContext.Current.CancellationToken);
        await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

        // BOTH children ran under the manager's creation-time correlation.
        Assert.Equal(2, log.ForTool("read_file").Count());
        Assert.All(log.All, c => Assert.Equal("manager-user", c.CorrelationId));
        Assert.Equal("manager-user", manager.LastChildOptions!.ToolAuthorizationCorrelationId);
    }

    // ========================================================================
    // 10. No-callback compatibility of the CHILD tool surface
    // ========================================================================

    [Fact]
    public async Task ChildWithoutAuthorization_KeepsTheLegacyToolNamesAndSchemas()
    {
        using var dir = new TempDir();
        var child = new ChildClient(Round.Say("child done"));

        var parent = ParentOptions(dir.Path);
        parent.SubAgents = SubOptions(child);
        // No callback anywhere.

        var client = new ScriptedClient(DelegateScript(), null);
        await using var agent = new CodingAgent(client, parent);
        await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

        var childTools = child.Options.ToArray().Last()!;
        var names = childTools.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.Contains("glob", names);
        Assert.Contains("grep", names);

        foreach (var tool in childTools.Tools!)
            Assert.False(ToolAuthorizationGuard.IsGuarded(tool), $"child tool '{tool.Name}' must not be wrapped");
    }

    [Fact]
    public async Task ChildWithAuthorization_KeepsTheSameNamesAndSchemasAsWithout()
    {
        using var dir = new TempDir();

        async Task<(List<string> Names, List<string> Schemas)> BuildAsync(bool authorize)
        {
            var child = new ChildClient(Round.Say("child done"));
            var parent = ParentOptions(dir.Path);
            parent.SubAgents = SubOptions(child);
            if (authorize)
                parent.AuthorizeTool = (_, _) => Task.FromResult(ToolAuthorizationDecision.Allow);

            var client = new ScriptedClient(DelegateScript(), null);
            await using var agent = new CodingAgent(client, parent);
            await agent.ExecuteAsync("delegate", TestContext.Current.CancellationToken);

            var childTools = child.Options.ToArray().Last()!;
            return (childTools.Tools!.Select(t => t.Name).ToList(),
                    childTools.Tools!.OfType<AIFunction>().Select(f => f.JsonSchema.ToString()).ToList());
        }

        var baseline = await BuildAsync(authorize: false);
        var guarded = await BuildAsync(authorize: true);

        Assert.Equal(baseline.Names, guarded.Names);
        Assert.Equal(baseline.Schemas, guarded.Schemas);
    }
}
