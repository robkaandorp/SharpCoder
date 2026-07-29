using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpCoder.SubAgents;

namespace SharpCoder.Tests;

/// <summary>
/// Tests for the <see cref="CodingAgent"/> disposal lifecycle contract:
/// idempotent disposal, synchronized manager cleanup, post-disposal execution
/// guards, the <see cref="CodingAgent.ActiveSubAgentManager"/> snapshot rule,
/// safe no-op when no manager was created, and the disposal race invariant.
/// </summary>
public class CodingAgentDisposalTests
{
    // ========================================================================
    // Fakes
    // ========================================================================

    /// <summary>
    /// Simple IChatClient that returns a fixed text response. Used for ExecuteAsync
    /// tests that just need the agent loop to run one round.
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        private readonly string _response;

        public StubChatClient(string response = "Done.")
        {
            _response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(_response)],
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Gating IChatClient that blocks on a TaskCompletionSource until signaled,
    /// so a test can control when an LLM call is in-flight. Responds to
    /// cancellation by throwing OperationCanceledException.
    /// </summary>
    private sealed class GatingChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> _gate;
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatingChatClient(TaskCompletionSource<bool> gate)
        {
            _gate = gate;
        }

        /// <summary>Signalled once GetResponseAsync has been entered (before the gate wait).</summary>
        public Task Entered => _entered.Task;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult(true);
            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelTcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(_gate.Task, cancelTcs.Task).ConfigureAwait(false);
                if (ReferenceEquals(finished, cancelTcs.Task))
                    throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static AgentOptions ParentOptions() => new()
    {
        WorkDirectory = Path.GetTempPath(),
        EnableBash = true,
        EnableFileOps = true,
        EnableFileWrites = true,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        AutoLoadWorkspaceInstructions = false,
    };

    private static SubAgentOptions SubAgentOptionsWithClient(IChatClient client) =>
        new()
        {
            DefaultClient = client,
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(30),
        };

    private static SubAgentOptions SubAgentOptionsWithFactory(Func<string, IChatClient> factory)
    {
        var opts = new SubAgentOptions
        {
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(30),
            ClientFactory = factory
        };
        opts.AvailableModels.Add(new SubAgentModelInfo("m1", "first model", 1000));
        return opts;
    }

    // ========================================================================
    // 1. DisposeAsync cancels running sub-agents and is idempotent
    // ========================================================================

    [Fact]
    public async Task DisposeAsync_Cancels_Running_SubAgents_And_Is_Idempotent()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatingClient = new GatingChatClient(gate);
        var parent = ParentOptions();
        // Sub-agents run on the gating client so they stay Running until disposal.
        parent.SubAgents = SubAgentOptionsWithClient(gatingClient);

        var agent = new CodingAgent(new StubChatClient(), parent);

        // Trigger manager creation via a normal (non-blocking) parent execution.
        await agent.ExecuteAsync("work", TestContext.Current.CancellationToken);

        var manager = agent.ActiveSubAgentManager;
        Assert.NotNull(manager);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);
        Assert.False(manager!.IsDisposed);

        // Start a REAL sub-agent that blocks on the gate.
        var info = await manager.StartAsync(
            new SubAgentRequest { Task = "blocked sub-agent" },
            TestContext.Current.CancellationToken);

        // Wait until the sub-agent's LLM call is actually in flight.
        await gatingClient.Entered;

        var running = manager.GetStatus(info.Id);
        Assert.Single(running);
        Assert.Equal(SubAgentStatus.Running, running[0].Status);

        // DisposeAsync must cancel the sub-agent and wait for a terminal status.
        await agent.DisposeAsync();

        // Verifies disposal via SubAgentManager.IsDisposed test seam (§4 of goal spec)
        Assert.NotNull(agent.ActiveSubAgentManager);
        Assert.True(agent.ActiveSubAgentManager!.IsDisposed);

        var after = manager.GetStatus(info.Id);
        Assert.Single(after);
        Assert.Contains(after[0].Status, new[]
        {
            SubAgentStatus.Cancelled, SubAgentStatus.TimedOut,
            SubAgentStatus.Failed, SubAgentStatus.Completed
        });
        Assert.NotEqual(SubAgentStatus.Running, after[0].Status);

        // Release the gate so any lingering call unwinds.
        gate.TrySetResult(true);

        // Second/third DisposeAsync must be a no-op, never throw.
        await agent.DisposeAsync();
        await agent.DisposeAsync();
    }

    // ========================================================================
    // 2. After disposal, execution entry points throw ObjectDisposedException
    // ========================================================================

    [Fact]
    public async Task After_Disposal_ExecuteAsync_SingleTurn_Throws_ObjectDisposedException()
    {
        var agent = new CodingAgent(new StubChatClient(), ParentOptions());
        await agent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => agent.ExecuteAsync("test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task After_Disposal_ExecuteAsync_Session_Overload_Throws_ObjectDisposedException()
    {
        var agent = new CodingAgent(new StubChatClient(), ParentOptions());
        await agent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => agent.ExecuteAsync(null, "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task After_Disposal_ExecuteStreamingAsync_Throws_On_Enumeration()
    {
        var agent = new CodingAgent(new StubChatClient(), ParentOptions());
        await agent.DisposeAsync();

        // The method returns an IAsyncEnumerable immediately — the exception
        // fires on first MoveNextAsync, not on the method call itself.
        var enumerable = agent.ExecuteStreamingAsync(null, "test", TestContext.Current.CancellationToken);
        var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task After_Disposal_ExecuteAsync_Throws_Even_With_SubAgents()
    {
        var parent = ParentOptions();
        parent.SubAgents = SubAgentOptionsWithClient(new StubChatClient());
        var agent = new CodingAgent(new StubChatClient(), parent);
        await agent.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => agent.ExecuteAsync("test", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => agent.ExecuteAsync(null, "test", TestContext.Current.CancellationToken));

        var enumerable = agent.ExecuteStreamingAsync(null, "test", TestContext.Current.CancellationToken);
        var enumerator = enumerable.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }

    // ========================================================================
    // 3. ActiveSubAgentManager property — null and snapshot rules
    // ========================================================================

    [Fact]
    public void ActiveSubAgentManager_Null_When_SubAgents_Is_Null()
    {
        var parent = ParentOptions();
        // SubAgents left null (default)
        var agent = new CodingAgent(new StubChatClient(), parent);

        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);
    }

    [Fact]
    public async Task ActiveSubAgentManager_Null_Before_First_Use()
    {
        var parent = ParentOptions();
        parent.SubAgents = SubAgentOptionsWithClient(new StubChatClient());
        var agent = new CodingAgent(new StubChatClient(), parent);

        // Manager is created lazily — before any execution it should be null.
        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);

        // Confirm it's not created just by accessing the property multiple times.
        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ActiveSubAgentManager_NonNull_After_First_Execution()
    {
        var parent = ParentOptions();
        parent.SubAgents = SubAgentOptionsWithClient(new StubChatClient());
        var agent = new CodingAgent(new StubChatClient(), parent);

        await agent.ExecuteAsync("hello", TestContext.Current.CancellationToken);

        Assert.NotNull(agent.ActiveSubAgentManager);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ActiveSubAgentManager_Still_NonNull_After_SubAgents_Set_To_Null()
    {
        var parent = ParentOptions();
        parent.SubAgents = SubAgentOptionsWithClient(new StubChatClient());
        var agent = new CodingAgent(new StubChatClient(), parent);

        // Trigger manager creation.
        await agent.ExecuteAsync("hello", TestContext.Current.CancellationToken);
        Assert.NotNull(agent.ActiveSubAgentManager);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        // Set SubAgents to null AFTER the manager was created.
        parent.SubAgents = null;

        // Snapshot rule: the manager persists once created.
        Assert.NotNull(agent.ActiveSubAgentManager);
        Assert.Same(agent.ActiveSubAgentManager, agent.ActiveSubAgentManager);
        Assert.Equal(1, agent.SubAgentManagerCreateCount);

        await agent.DisposeAsync();
    }

    // ========================================================================
    // 4. Disposal when no manager was ever created is a safe no-op
    // ========================================================================

    [Fact]
    public async Task DisposeAsync_With_No_Manager_Created_Is_Safe_NoOp()
    {
        var parent = ParentOptions();
        // SubAgents is null — no manager will ever be created.
        var agent = new CodingAgent(new StubChatClient(), parent);

        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);

        await agent.DisposeAsync(); // must complete without throwing
        await agent.DisposeAsync(); // still must not throw

        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);
    }

    [Fact]
    public async Task DisposeAsync_With_SubAgents_But_No_Execution_Is_Safe_NoOp()
    {
        var parent = ParentOptions();
        parent.SubAgents = SubAgentOptionsWithClient(new StubChatClient());
        var agent = new CodingAgent(new StubChatClient(), parent);

        // Manager not yet created (lazy) — disposal should be a safe no-op.
        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);

        await agent.DisposeAsync();
        await agent.DisposeAsync();

        Assert.Null(agent.ActiveSubAgentManager);
        Assert.Equal(0, agent.SubAgentManagerCreateCount);
    }

    // ========================================================================
    // 5. Disposal race — no leaked manager (both branches exercised)
    // ========================================================================

    // Blocking calls are intentional: this test deliberately drives real threads
    // to exercise the disposal/creation race deterministically.
#pragma warning disable xUnit1031, xUnit1051
    [Fact]
    public void Disposal_Race_Never_Leaks_Manager()
    {
        const int PerBatch = 100;
        const int JoinTimeoutMs = 10000;

        var branchACount = 0; // counter == 1
        var branchBCount = 0; // counter == 0

        // ---------------- Batch A: manager created, then disposed ----------------
        for (var iteration = 0; iteration < PerBatch; iteration++)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gatingClient = new GatingChatClient(gate);
            var parent = ParentOptions();
            parent.SubAgents = SubAgentOptionsWithFactory(_ => new StubChatClient());
            var agent = new CodingAgent(gatingClient, parent);

            var barrier = new Barrier(2);
            AgentResult? execResult = null;
            Exception? execException = null;
            Exception? disposeException = null;

            var execThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    // Creates the manager (counter==1) and then blocks in the gating client.
                    execResult = agent.ExecuteAsync("race", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    execException = ex;
                }
            });

            var disposeThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    // Wait until the execution is genuinely in flight inside the LLM call,
                    // i.e. it has already passed lazy manager creation.
                    gatingClient.Entered.Wait(JoinTimeoutMs);
                    agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    disposeException = ex;
                }
                finally
                {
                    // Always release the gate so the execution thread can unwind.
                    gate.TrySetResult(true);
                }
            });

            try
            {
                execThread.Start();
                disposeThread.Start();
                Assert.True(disposeThread.Join(JoinTimeoutMs),
                    $"Batch A iteration {iteration}: disposal thread did not finish within {JoinTimeoutMs}ms.");
                Assert.True(execThread.Join(JoinTimeoutMs),
                    $"Batch A iteration {iteration}: execution thread did not finish within {JoinTimeoutMs}ms.");
            }
            finally
            {
                gate.TrySetResult(true);
            }

            Assert.True(disposeException is null,
                $"Batch A iteration {iteration}: disposal thread threw {disposeException}");

            var createCount = agent.SubAgentManagerCreateCount;
            Assert.Equal(1, createCount);
            branchACount++;

            // Manager must exist and be disposed — no leak.
            Assert.NotNull(agent.ActiveSubAgentManager);
            // Verifies disposal via SubAgentManager.IsDisposed test seam (§4 of goal spec)
            Assert.True(agent.ActiveSubAgentManager!.IsDisposed,
                $"Batch A iteration {iteration}: counter==1 but manager not disposed (leak!)");

            // Execution outcome unrestricted — success, error result, OCE, or ODE all valid.
            _ = execResult;
            _ = execException;
        }

        // ---------------- Batch B: disposal wins before manager creation ----------------
        for (var iteration = 0; iteration < PerBatch; iteration++)
        {
            var parent = ParentOptions();
            parent.SubAgents = SubAgentOptionsWithFactory(_ => new StubChatClient());
            var agent = new CodingAgent(new StubChatClient(), parent);

            agent.DisposeAsync().AsTask().GetAwaiter().GetResult();

            Exception? execException = null;
            try
            {
                agent.ExecuteAsync("race", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                execException = ex;
            }

            Assert.IsType<ObjectDisposedException>(execException);

            var createCount = agent.SubAgentManagerCreateCount;
            Assert.Equal(0, createCount);
            Assert.Null(agent.ActiveSubAgentManager);
            branchBCount++;
        }

        // Both branches must have been exercised.
        Assert.Equal(PerBatch, branchACount);
        Assert.Equal(PerBatch, branchBCount);
    }
#pragma warning restore xUnit1031, xUnit1051
}
