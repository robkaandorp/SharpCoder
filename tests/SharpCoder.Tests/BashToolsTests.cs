using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;
using SharpCoder;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class BashToolsTests
{
    private const int ConstructorDefaultTimeoutMs = 120000;
    private const int FifteenMinutesMs = 900000;

    /// <summary>Chat client that records the ChatOptions it was called with.</summary>
    private sealed class OptionsCapturingClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Installs the internal timing seam so the effective per-call timeout can be observed
    /// without ever waiting for it. The returned list records every timeout the production
    /// execution path selected, in call order.
    /// </summary>
    internal static List<int> ObserveTimeouts(BashTools tools)
    {
        var observed = new List<int>();
        tools.DelayFactory = (timeoutMs, token) =>
        {
            lock (observed) observed.Add(timeoutMs);
            // Never completes on its own: the process-exit race always wins.
            return Task.Delay(Timeout.Infinite, token);
        };
        return observed;
    }

    private static string LongRunningCommand() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ========================================================================
    // Existing behavior guard
    // ========================================================================

    [Fact]
    public async Task ExecuteBashCommand_Echo_ReturnsOutput()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command("echo Hello XUnit", Ct);

        Assert.Contains("Hello XUnit", result);
        Assert.Contains("--- STDOUT ---", result);
    }

    // ========================================================================
    // Timeout selection
    // ========================================================================

    [Fact]
    public async Task OmittedTimeout_UsesInstanceDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        var result = await tools.execute_bash_command("echo omitted", Ct);

        Assert.Contains("omitted", result);
        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task NullTimeout_UsesInstanceDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo null", Ct, null);

        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task OmittedTimeout_WithConstructorDefault_UsesTwoMinuteDefault()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo default", Ct);

        Assert.Equal(new[] { ConstructorDefaultTimeoutMs }, observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public async Task NonPositiveConstructorTimeout_FallsBackToTwoMinuteDefault(int constructorTimeout)
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: constructorTimeout);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo fallback", Ct);

        Assert.Equal(new[] { ConstructorDefaultTimeoutMs }, observed);
    }

    [Fact]
    public async Task ShorterOverride_IsSelectedForThatInvocation()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 60000);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo shorter", Ct, 5000);

        Assert.Equal(new[] { 5000 }, observed);
    }

    [Fact]
    public async Task LongerOverride_SelectsFifteenMinuteBudget()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo longer", Ct, FifteenMinutesMs);

        Assert.Equal(new[] { FifteenMinutesMs }, observed);
    }

    [Fact]
    public async Task Override_IsNotRetained_ForSubsequentInvocations()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 7000);
        var observed = ObserveTimeouts(tools);

        await tools.execute_bash_command("echo one", Ct, FifteenMinutesMs);
        await tools.execute_bash_command("echo two", Ct);
        await tools.execute_bash_command("echo three", Ct, 1500);
        await tools.execute_bash_command("echo four", Ct, null);

        Assert.Equal(new[] { FifteenMinutesMs, 7000, 1500, 7000 }, observed);
    }

    [Fact]
    public async Task EffectiveTimeout_IsUsedInTheTimeoutMessage()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var observed = new List<int>();
        tools.DelayFactory = (timeoutMs, _) =>
        {
            observed.Add(timeoutMs);
            return Task.CompletedTask; // force the timeout branch immediately
        };

        var result = await tools.execute_bash_command(LongRunningCommand(), Ct, FifteenMinutesMs);

        Assert.Equal(new[] { FifteenMinutesMs }, observed);
        Assert.Contains($"timed out after {FifteenMinutesMs}ms", result);
    }

    // ========================================================================
    // Invalid values are rejected before launching a process
    // ========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-900000)]
    public async Task InvalidTimeout_ThrowsBeforeProcessLaunch(int timeoutMs)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "bashtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var marker = Path.Combine(workDir, "marker.txt");
            var tools = new BashTools(workDir);
            var observed = ObserveTimeouts(tools);

            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => tools.execute_bash_command("echo launched > marker.txt", Ct, timeoutMs));

            Assert.Equal("timeout_ms", ex.ParamName);
            Assert.False(File.Exists(marker), "No process may be launched for an invalid timeout.");
            Assert.Empty(observed);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ========================================================================
    // Pre-cancelled calls launch nothing (lifecycle fix)
    // ========================================================================

    [Fact]
    public async Task PreCancelledToken_ThrowsBeforeProcessLaunch()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "bashtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            var marker = Path.Combine(workDir, "marker.txt");
            var tools = new BashTools(workDir);
            var observed = ObserveTimeouts(tools);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.execute_bash_command("echo launched > marker.txt", cts.Token));

            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.False(File.Exists(marker), "No process may be launched for an already-cancelled call.");
            Assert.Empty(observed);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidTimeout_IsRejected_EvenWhenTheTokenIsAlreadyCancelled(int timeoutMs)
    {
        // Argument validation still runs first: the contract for an invalid timeout_ms is
        // unchanged by the pre-cancellation guard.
        var tools = new BashTools(Environment.CurrentDirectory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tools.execute_bash_command("echo never", cts.Token, timeoutMs));

        Assert.Equal("timeout_ms", ex.ParamName);
    }

    // ========================================================================
    // Source/binary compatibility of the original signature
    // ========================================================================

    [Fact]
    public async Task OriginalSignature_StillResolves_WithSingleArgumentAndPositionalToken()
    {
        var tools = new BashTools(Environment.CurrentDirectory);

        // One-argument call (token defaulted) and the existing positional-token call
        // must both remain unambiguous. The whole point of this test is the *shape* of
        // these legacy call sites, so the xUnit1051 "pass TestContext token" rule is
        // suppressed narrowly here; the token-passing form is covered on the next line.
#pragma warning disable xUnit1051
        var single = await tools.execute_bash_command("echo single");
        var positional = await tools.execute_bash_command("echo positional", default);
#pragma warning restore xUnit1051
        var withTestToken = await tools.execute_bash_command("echo token", Ct);

        Assert.Contains("single", single);
        Assert.Contains("positional", positional);
        Assert.Contains("token", withTestToken);
    }

    [Fact]
    public async Task TwoParameterDelegate_BindsToOriginalSignature()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        Func<string, CancellationToken, Task<string>> del = tools.execute_bash_command;
        var result = await del("echo delegate", Ct);

        Assert.Contains("delegate", result);
        Assert.Equal(new[] { 4321 }, observed);
    }

    [Fact]
    public async Task ThreeParameterDelegate_BindsToTimeoutAwareOverload()
    {
        var tools = new BashTools(Environment.CurrentDirectory, timeoutMs: 4321);
        var observed = ObserveTimeouts(tools);

        Func<string, CancellationToken, int?, Task<string>> del = tools.execute_bash_command;
        var result = await del("echo delegate3", Ct, 6000);

        Assert.Contains("delegate3", result);
        Assert.Equal(new[] { 6000 }, observed);
    }

    // ========================================================================
    // Registration through the real CodingAgent
    // ========================================================================

    private static AgentOptions BashAgentOptions() => new()
    {
        WorkDirectory = Environment.CurrentDirectory,
        EnableBash = true,
        EnableFileOps = false,
        EnableSkills = false,
        SystemPrompt = "You are a test agent.",
        AutoLoadWorkspaceInstructions = false,
    };

    private static async Task<AIFunction> GetRegisteredBashToolAsync()
    {
        var client = new OptionsCapturingClient();
        var agent = new CodingAgent(client, BashAgentOptions());

        await agent.ExecuteAsync("hello", Ct);

        var tools = client.LastOptions?.Tools ?? new List<AITool>();
        return tools.OfType<AIFunction>().Single(f => f.Name == "execute_bash_command");
    }

    [Fact]
    public async Task RegisteredTool_ExposesOptionalTimeoutMs_AndNoCancellationToken()
    {
        var fn = await GetRegisteredBashToolAsync();

        var schema = fn.JsonSchema;
        Assert.True(schema.TryGetProperty("properties", out var properties));

        var names = properties.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("command", names);
        Assert.Contains("timeout_ms", names);
        Assert.DoesNotContain(names, n =>
            n.Equals("ct", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase));

        // command is required, timeout_ms is not.
        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()).ToList()
            : new List<string?>();
        Assert.Contains("command", required);
        Assert.DoesNotContain("timeout_ms", required);

        // timeout_ms is an integer (nullable => integer or null).
        var timeoutSchema = properties.GetProperty("timeout_ms").GetRawText();
        Assert.Contains("integer", timeoutSchema);
    }

    [Fact]
    public async Task RegisteredTool_Description_DoesNotClaimPersistentShellSession()
    {
        var fn = await GetRegisteredBashToolAsync();

        Assert.DoesNotContain("persistent shell session", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fresh shell process", fn.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeout_ms", fn.Description);
        Assert.Contains("900000", fn.Description);
    }

    [Fact]
    public async Task RegisteredTool_CanBeInvoked_WithAndWithoutTimeout()
    {
        var fn = await GetRegisteredBashToolAsync();

        var omitted = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "echo registered-omitted" }),
            Ct);
        var explicitTimeout = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["command"] = "echo registered-explicit",
                ["timeout_ms"] = 5000
            }),
            Ct);

        Assert.Contains("registered-omitted", ResultToString(omitted));
        Assert.Contains("registered-explicit", ResultToString(explicitTimeout));
    }

    private static string ResultToString(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? (je.GetString() ?? string.Empty) : je.GetRawText();
        return result.ToString() ?? string.Empty;
    }
}
