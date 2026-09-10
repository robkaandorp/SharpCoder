using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

/// <summary>
/// Serializes the lifecycle tests: each one launches real process trees and one of them observes
/// <see cref="TaskScheduler.UnobservedTaskException"/>, which is process-wide.
/// </summary>
[CollectionDefinition("BashToolsLifecycle", DisableParallelization = true)]
public class BashToolsLifecycleCollection
{
}

/// <summary>
/// Lifecycle tests for <see cref="BashTools"/> that run against the real
/// <c>SharpCoder.ProcessFixture</c> console app rather than fabricated process identities.
/// <para>
/// Every test records the concrete PIDs and GUID identities of the fixture root, child and
/// grandchild from the fixture's own readiness records, establishes readiness through a barrier
/// (never a fixed sleep), and releases every recorded process in a <c>finally</c> block using
/// identity-scoped cleanup only — no killing by process name and no process sweeping.
/// </para>
/// <para>
/// Timeouts inside these tests are failure guards, not the success mechanism: readiness is proven
/// by the fixture's rendezvous records and liveness transitions are proven by bounded polling of
/// the recorded PIDs.
/// </para>
/// </summary>
[Collection("BashToolsLifecycle")]
public class BashToolsLifecycleTests
{
    private const int BarrierGuardMs = 30_000;
    private const int LivenessGuardMs = 20_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ====================================================================
    // Live-root, stable accessible ancestry
    // ====================================================================

    [Fact]
    public async Task LiveRootWithEstablishedDescendants_Timeout_TerminatesWholeRecordedTree()
    {
        using var fixture = new ProcessTreeFixture();
        using var sentinel = new SentinelProcess();
        try
        {
            var tools = fixture.CreateTools();

            // The command blocks until the fixture tree is fully established (barrier), so the
            // timeout can only fire with descendants already running.
            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 60_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            Assert.Equal(3, fixture.RecordedProcesses.Count);

            // Now force the timeout deterministically through the existing DelayFactory seam.
            fixture.TripTimeout();
            var result = await run;

            Assert.Contains("timed out after 60000ms", result);

            foreach (var recorded in fixture.RecordedProcesses)
            {
                Assert.True(
                    await WaitUntilNotRunningAsync(recorded.Pid, LivenessGuardMs, Ct),
                    $"Recorded fixture process {recorded.Role} (id={recorded.Identity}, pid={recorded.Pid}) was still running after the tree kill.");
            }

            // Identity-scoped cleanup: an unrelated process launched by this test must survive.
            Assert.True(sentinel.IsRunning, "The unrelated sentinel process must not be killed.");

            // Non-vacuity guard for the assertion above: the same probe must observe the sentinel
            // transitioning to "not running" once the test itself releases it.
            sentinel.Release();
            Assert.True(
                await WaitUntilNotRunningAsync(sentinel.Pid, LivenessGuardMs, Ct),
                "The sentinel liveness probe must be able to observe a terminated process.");

            // The tool is reusable after cleanup.
            var after = await new BashTools(fixture.WorkDirectory).execute_bash_command("echo reusable-after-timeout", Ct);
            Assert.Contains("reusable-after-timeout", after);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
            sentinel.Release();
        }
    }

    [Fact]
    public async Task LiveRootWithEstablishedDescendants_CancellationAfterReadiness_TerminatesWholeRecordedTree()
    {
        using var fixture = new ProcessTreeFixture();
        using var sentinel = new SentinelProcess();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 60_000);

            // Cancel ONLY after descendants are established.
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            Assert.Equal(3, fixture.RecordedProcesses.Count);
            callerCts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(callerCts.Token, ex.CancellationToken);

            foreach (var recorded in fixture.RecordedProcesses)
            {
                Assert.True(
                    await WaitUntilNotRunningAsync(recorded.Pid, LivenessGuardMs, Ct),
                    $"Recorded fixture process {recorded.Role} (id={recorded.Identity}, pid={recorded.Pid}) was still running after cancellation cleanup.");
            }

            Assert.True(sentinel.IsRunning, "The unrelated sentinel process must not be killed.");

            sentinel.Release();
            Assert.True(
                await WaitUntilNotRunningAsync(sentinel.Pid, LivenessGuardMs, Ct),
                "The sentinel liveness probe must be able to observe a terminated process.");

            var after = await new BashTools(fixture.WorkDirectory).execute_bash_command("echo reusable-after-cancel", Ct);
            Assert.Contains("reusable-after-cancel", after);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
            sentinel.Release();
        }
    }

    // ====================================================================
    // Root-exited / inherited-pipe vectors
    // ====================================================================

    [Fact]
    public async Task RootExitedWithInheritedPipeHeld_Timeout_ReturnsInsteadOfHanging()
    {
        using var fixture = new ProcessTreeFixture(rootExitsAfterReady: true);
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 45_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);

            // The vector requires the ROOT to be gone while a descendant still holds the pipe.
            await fixture.WaitUntilRootExitedAsync(LivenessGuardMs, Ct);
            fixture.TripTimeout();

            // Failure guard only: the assertion is that the call returns at all.
            var result = await WithGuardAsync(run, LivenessGuardMs, "Timeout must return while a descendant holds the inherited pipe.");

            Assert.Contains("timed out after 45000ms", result);
            Assert.Contains("--- CLEANUP DIAGNOSTICS ---", result);
            Assert.Contains("Output capture is INCOMPLETE", result);
            Assert.Contains("NOT established", result);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task RootExitedWithInheritedPipeHeld_Cancellation_ThrowsInsteadOfHanging()
    {
        using var fixture = new ProcessTreeFixture(rootExitsAfterReady: true);
        using var callerCts = new CancellationTokenSource();
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 60_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);

            callerCts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation must throw while a descendant holds the inherited pipe."));
            Assert.Equal(callerCts.Token, ex.CancellationToken);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    // ====================================================================
    // Pre-cancelled call
    // ====================================================================

    [Fact]
    public async Task PreCancelledCall_LaunchesNothing_AndThrowsWithCallerToken()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        try
        {
            var tools = new BashTools(fixture.WorkDirectory);
            var observed = BashToolsTests.ObserveTimeouts(tools);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000));

            Assert.Equal(callerCts.Token, ex.CancellationToken);

            // Nothing launched: the fixture never published a readiness record and the timing
            // seam was never consulted.
            Assert.Empty(observed);
            Assert.False(fixture.AnyReadinessRecordExists(), "A pre-cancelled call must not launch a process.");
            Assert.Empty(fixture.ReadRecordedProcesses());
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    // ====================================================================
    // Cancellation / timeout arbitration
    // ====================================================================

    [Fact]
    public async Task CancellationDuringExecution_WinsOverConcurrentlyElapsingTimeout()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var readyForRace = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tools = fixture.CreateTools();

            // Deterministic arbitration: the existing timing seam elapses only after the tree is
            // established, and it requests caller cancellation immediately *before* completing.
            // The timeout therefore genuinely wins the internal race while cancellation is
            // concurrently pending — so an implementation that let the timeout win would return a
            // timeout string here instead of throwing.
            tools.DelayFactory = async (_, token) =>
            {
                await readyForRace.Task.WaitAsync(token).ConfigureAwait(false);
                callerCts.Cancel();
            };

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            readyForRace.TrySetResult(true);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation must win over a concurrently elapsing timeout."));
            Assert.Equal(callerCts.Token, ex.CancellationToken);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task CancellationDuringOutputDrain_WinsOverConcurrentlyElapsingTimeout()
    {
        // Root exits but the grandchild keeps the inherited pipe open, so the call is parked in
        // the output drain when the timeout elapses and cancellation is requested.
        using var fixture = new ProcessTreeFixture(rootExitsAfterReady: true);
        using var callerCts = new CancellationTokenSource();
        try
        {
            var readyForRace = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tools = fixture.CreateTools();
            tools.DelayFactory = async (_, token) =>
            {
                await readyForRace.Task.WaitAsync(token).ConfigureAwait(false);
                callerCts.Cancel();
            };

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            await fixture.WaitUntilRootExitedAsync(LivenessGuardMs, Ct);
            readyForRace.TrySetResult(true);

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation during drain must win over the timeout."));
            Assert.Equal(callerCts.Token, ex.CancellationToken);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task CancellationObservedDuringCleanup_WinsOverAnAlreadyElapsedTimeout()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var tools = fixture.CreateTools();

            // The timeout elapses cleanly first; cancellation is then requested from inside the
            // cleanup pass itself. Cancellation observed before the outcome is committed must
            // still win, so no timeout string may be returned.
            var terminator = new StubTreeTerminator(supported: true, onTerminate: () => callerCts.Cancel());
            tools.TreeTerminator = terminator;

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            fixture.TripTimeout();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation observed during cleanup must win."));
            Assert.Equal(callerCts.Token, ex.CancellationToken);
            Assert.True(terminator.TerminateTreeCalled);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task TimeoutWithoutCancellation_StillReturnsTheTimeoutString()
    {
        // Negative control for the arbitration tests above: with no cancellation in play the same
        // elapsed timeout must produce a string result, not an OperationCanceledException.
        using var fixture = new ProcessTreeFixture();
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            fixture.TripTimeout();

            var result = await WithGuardAsync(run, LivenessGuardMs, "An uncancelled timeout must return a string.");
            Assert.Contains("timed out after 30000ms", result);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    // ====================================================================
    // Degraded tree-kill capability
    // ====================================================================

    [Fact]
    public async Task UnsupportedTreeKillCapability_FallsBackToRootOnly_AndReportsDegradedCleanup()
    {
        using var fixture = new ProcessTreeFixture();
        try
        {
            var log = new RecordingLogger();
            var tools = fixture.CreateTools(log);
            var capability = new StubTreeTerminator(supported: false);
            tools.TreeTerminator = capability;

            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            fixture.TripTimeout();

            var result = await WithGuardAsync(run, LivenessGuardMs, "Degraded cleanup must still complete.");

            Assert.False(capability.TerminateTreeCalled, "An unsupported capability must never be invoked.");

            // Root-only fallback actually happened: the recorded root is gone.
            var root = fixture.RecordedProcesses.Single(p => p.Role == "root");
            Assert.True(
                await WaitUntilNotRunningAsync(root.Pid, LivenessGuardMs, Ct),
                "The root-only fallback kill must still terminate the root process.");

            // Explicit degraded diagnostics in the result and the logs; never a success claim.
            Assert.Contains("--- CLEANUP DIAGNOSTICS ---", result);
            Assert.Contains("Descendant cleanup was NOT established", result);
            Assert.Contains("Process.Kill(entireProcessTree)", result);
            AssertNoTreeCleanupSuccessClaim(result);
            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("descendant cleanup was not established", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task ThrowingTreeKillCapability_FallsBackToRootOnly_AndReportsDegradedCleanup()
    {
        using var fixture = new ProcessTreeFixture();
        try
        {
            var log = new RecordingLogger();
            var tools = fixture.CreateTools(log);
            var capability = new StubTreeTerminator(
                supported: true,
                onTerminate: () => throw new InvalidOperationException("simulated-tree-kill-failure"));
            tools.TreeTerminator = capability;

            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            fixture.TripTimeout();

            var result = await WithGuardAsync(run, LivenessGuardMs, "Degraded cleanup must still complete.");

            Assert.True(capability.TerminateTreeCalled, "A supported capability must be invoked.");

            var root = fixture.RecordedProcesses.Single(p => p.Role == "root");
            Assert.True(
                await WaitUntilNotRunningAsync(root.Pid, LivenessGuardMs, Ct),
                "The root-only fallback kill must still terminate the root process.");

            Assert.Contains("Descendant cleanup was NOT established", result);
            Assert.Contains("simulated-tree-kill-failure", result);
            AssertNoTreeCleanupSuccessClaim(result);
            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("descendant cleanup was not established", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    private static void AssertNoTreeCleanupSuccessClaim(string result)
    {
        Assert.DoesNotContain("executed successfully", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Process-tree termination (entireProcessTree) was requested",
            result,
            StringComparison.Ordinal);
    }

    // ====================================================================
    // Output contracts on completed runs
    // ====================================================================

    [Fact]
    public async Task CompletedRun_RetainsBothStdoutAndStderrFromTheFixture()
    {
        using var fixture = new ProcessTreeFixture(maxDepth: 0, lifetimeMs: 1);
        try
        {
            var tools = new BashTools(fixture.WorkDirectory);
            var result = await WithGuardAsync(
                tools.execute_bash_command(fixture.RootCommand(), Ct, 60_000),
                LivenessGuardMs,
                "A depth-0 fixture run must complete on its own.");

            Assert.Contains("--- STDOUT ---", result);
            Assert.Contains("--- STDERR ---", result);
            Assert.Contains($"FIXTURE-STDOUT role=root id={fixture.RootIdentity}", result);
            Assert.Contains($"FIXTURE-STDERR role=root id={fixture.RootIdentity}", result);
            Assert.Contains("--- EXIT CODE: 0 ---", result);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task SilentZeroExit_IsReportedAsSuccess()
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command("exit 0", Ct, 30_000);

        Assert.Contains("Command executed successfully with no output.", result);
        Assert.Contains("--- EXIT CODE: 0 ---", result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(42)]
    public async Task SilentNonZeroExit_IsNeverLabelledSuccessful(int exitCode)
    {
        var tools = new BashTools(Environment.CurrentDirectory);
        var result = await tools.execute_bash_command($"exit {exitCode}", Ct, 30_000);

        Assert.DoesNotContain("successfully", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Command failed with exit code {exitCode}", result);
        Assert.Contains($"--- EXIT CODE: {exitCode} ---", result);
    }

    [Fact]
    public async Task OrdinaryExecutionError_KeepsTruthfulErrorText_AndInventsNoExitCode()
    {
        // A working directory that does not exist makes Process.Start throw, which is the
        // ordinary-execution-error path (distinct from timeout and from cancellation).
        var missingDirectory = Path.Combine(Path.GetTempPath(), "sharpcoder-missing-" + Guid.NewGuid().ToString("N"));
        var tools = new BashTools(missingDirectory);

        var result = await tools.execute_bash_command("echo error-path", Ct, 30_000);

        Assert.StartsWith("Error executing command: ", result);
        Assert.DoesNotContain("--- EXIT CODE:", result);
        Assert.DoesNotContain("executed successfully", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out after", result);
    }

    // ====================================================================
    // Resource / reader settlement
    // ====================================================================

    [Fact]
    public async Task RepeatedTimeouts_DoNotLeakUnobservedReadFaults_OrTimersAndEvents()
    {
        var unobserved = new ConcurrentBag<Exception>();
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Add(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        var fixtures = new List<ProcessTreeFixture>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var fixture = new ProcessTreeFixture(rootExitsAfterReady: true);
                fixtures.Add(fixture);

                var tools = fixture.CreateTools();
                var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 30_000);
                await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
                fixture.TripTimeout();

                var result = await WithGuardAsync(run, LivenessGuardMs, "Each timed-out run must return.");
                Assert.Contains("timed out after 30000ms", result);
            }

            // Force finalization so any unobserved read fault would have surfaced by now.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
            foreach (var fixture in fixtures)
            {
                fixture.ReleaseRecordedProcesses();
                fixture.Dispose();
            }
        }
    }

    // ====================================================================
    // Cancellation exception/log contract
    // ====================================================================

    [Fact]
    public async Task CancellationPath_ReturnsNoString_LogsStatus_ButNeverCopiesRawOutput()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var log = new RecordingLogger();
            var tools = fixture.CreateTools(log);

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 60_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);

            // Give the readers a chance to actually capture the fixture's banner lines, so the
            // "no raw output copied" assertion is meaningful rather than vacuous.
            await fixture.WaitUntilAllIdentitiesFlushedAsync(LivenessGuardMs, Ct);
            callerCts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation must throw."));

            // No string result at all, and the caller token is carried.
            Assert.Equal(callerCts.Token, ex.CancellationToken);

            // Interruption + cleanup status are logged.
            Assert.Contains(log.Entries, e => e.Message.Contains("cancelled by the caller", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(log.Entries, e =>
                e.Message.Contains("cleanup completed=", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Incomplete cleanup", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Degraded cleanup", StringComparison.OrdinalIgnoreCase));

            // But no raw stdout/stderr content is copied into the logs or the exception payload.
            // The out= marker is printed ONLY to the fixture's streams; it never appears on a
            // command line, so finding it anywhere here would prove captured output leaked.
            var logText = log.Text;
            var exceptionText = ex.ToString();
            foreach (var token in fixture.OutputOnlyTokens)
            {
                Assert.DoesNotContain(token, logText, StringComparison.Ordinal);
                Assert.DoesNotContain(token, exceptionText, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("FIXTURE-STDOUT", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE-STDERR", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE-STDOUT", exceptionText, StringComparison.Ordinal);
            Assert.DoesNotContain("FIXTURE-STDERR", exceptionText, StringComparison.Ordinal);
            Assert.DoesNotContain("--- STDOUT ---", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("--- STDERR ---", exceptionText, StringComparison.Ordinal);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task TimeoutPath_ReturnsAStringResult_AndDoesNotThrow()
    {
        using var fixture = new ProcessTreeFixture();
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 25_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            fixture.TripTimeout();

            var result = await WithGuardAsync(run, LivenessGuardMs, "A timeout must return a string.");

            Assert.StartsWith("Command timed out after 25000ms.", result);
            Assert.DoesNotContain("--- EXIT CODE:", result);
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    // ====================================================================
    // Cleanup allowance: the single fixed five-second budget
    // ====================================================================

    /// <summary>
    /// The exact cleanup allowance this test class pins. Any change to
    /// <c>BashTools.CleanupAllowanceMs</c> must fail here and in the behavioural tests below.
    /// </summary>
    private const int ExpectedCleanupAllowanceMs = 5000;

    [Fact]
    public void CleanupAllowance_IsExactlyFiveSeconds()
    {
        // Read the production constant through reflection so the assertion binds to the real
        // value at run time rather than being folded into this assembly at compile time.
        var field = typeof(BashTools).GetField(
            "CleanupAllowanceMs",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        Assert.NotNull(field);
        Assert.True(field!.IsLiteral, "CleanupAllowanceMs must remain a compile-time constant.");
        Assert.Equal(typeof(int), field.FieldType);

        var value = Assert.IsType<int>(field.GetRawConstantValue());
        Assert.Equal(ExpectedCleanupAllowanceMs, value);
    }

    /// <summary>
    /// Behavioural counterpart to <see cref="CleanupAllowance_IsExactlyFiveSeconds"/>: when a
    /// descendant holds the inherited pipe open forever, the cleanup pass must give up after
    /// exactly the allowance. The measured window is deliberately tight enough that raising the
    /// allowance to 6000ms (or lowering it to 4000ms) falls outside it, while the observed spread
    /// at 5000ms is only a few milliseconds.
    /// </summary>
    [Fact]
    public async Task CleanupAllowance_BoundsTheDrainWaitToTheExactBudget()
    {
        using var fixture = new ProcessTreeFixture(rootExitsAfterReady: true);
        try
        {
            var tools = fixture.CreateTools();
            var run = tools.execute_bash_command(fixture.RootCommand(), Ct, 40_000);

            // The drain can only be bounded by the allowance once the root is gone and a
            // descendant still owns the pipe, so establish that state first.
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            await fixture.WaitUntilRootExitedAsync(LivenessGuardMs, Ct);

            // Start measuring at the instant the timeout is tripped: everything after this point
            // is the single bounded cleanup allowance.
            var elapsed = Stopwatch.StartNew();
            fixture.TripTimeout();
            var result = await WithGuardAsync(run, LivenessGuardMs, "The bounded cleanup must complete.");
            elapsed.Stop();

            // The wait really was bounded by the allowance (rather than by the pipe closing).
            Assert.Contains("Output capture is INCOMPLETE", result);

            var observed = elapsed.ElapsedMilliseconds;

            // Lower edge: the allowance must actually be honoured, so a shortened budget fails.
            Assert.True(
                observed >= ExpectedCleanupAllowanceMs - CleanupAllowanceToleranceMs,
                $"The cleanup drain returned after {observed}ms, which is below the {ExpectedCleanupAllowanceMs}ms allowance.");

            // Upper edge: tight enough that a 6000ms allowance fails, generous enough to absorb
            // scheduling jitter at 5000ms (observed spread on this host: 0-3ms).
            Assert.True(
                observed <= ExpectedCleanupAllowanceMs + CleanupAllowanceToleranceMs,
                $"The cleanup drain returned after {observed}ms, which exceeds the {ExpectedCleanupAllowanceMs}ms allowance.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    /// <summary>
    /// Discrimination window around <see cref="ExpectedCleanupAllowanceMs"/>. It must stay well
    /// below the 1000ms gap to the mutant value of 6000ms.
    /// </summary>
    private const int CleanupAllowanceToleranceMs = 400;

    /// <summary>
    /// The allowance is a single budget shared by termination and the drain, and it must be
    /// independent of the caller's token and of the caller's execution timer. A cleanup pass whose
    /// termination step consumes part of the budget must therefore still finish within the same
    /// total allowance, not restart it.
    /// <para>
    /// The fixture root is deliberately kept ALIVE through the cleanup trigger so that
    /// <c>CleanupAsync</c> actually reaches its termination step and invokes the seam; the seam
    /// records the call and burns a known, synchronous slice of the budget without terminating
    /// anything, so the remaining steps must run on what is left of the same allowance.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CleanupAllowance_IsOneSharedBudget_NotRestartedPerStep()
    {
        // Root stays alive (no rootExitsAfterReady) so termination is genuinely requested.
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();

            // Burn a known slice of the allowance inside the termination step. The seam records
            // the invocation and deliberately does NOT terminate the tree, so the subsequent
            // root-exit and drain waits must come out of the remaining budget.
            const int terminationCostMs = 1500;
            var terminator = new CapturingTreeTerminator(
                observer,
                inner: null,
                onTerminate: () => Thread.Sleep(terminationCostMs));
            tools.TreeTerminator = terminator;

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 40_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);

            // The root must still be live when cleanup starts, otherwise termination is skipped.
            var root = fixture.RecordedProcesses.Single(p => p.Role == "root");
            Assert.True(ProcessProbe.IsRunning(root.Pid), "The fixture root must be live when cleanup begins.");

            var elapsed = Stopwatch.StartNew();

            // Cancel the caller token as well: the allowance must not inherit it, so cleanup still
            // runs to completion under the same fixed budget.
            callerCts.Cancel();
            fixture.TripTimeout();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cleanup must complete even for a cancelled caller."));
            elapsed.Stop();

            // (i) The termination step really ran, so its cost really was charged to the budget.
            Assert.True(
                terminator.TerminateTreeCalled,
                "CleanupAsync must invoke the tree terminator while the root is still alive.");

            var observed = elapsed.ElapsedMilliseconds;

            // (ii) The termination cost reduces the remaining budget rather than restarting it, so
            // the total stays at the allowance instead of becoming allowance + terminationCost (or
            // allowance + terminationCost + a fresh budget for the output-read wait).
            Assert.True(
                observed <= ExpectedCleanupAllowanceMs + CleanupAllowanceToleranceMs,
                $"Cleanup took {observed}ms; the single {ExpectedCleanupAllowanceMs}ms allowance must cover termination and drain together.");

            // And it is genuinely the allowance that bounded it, not the termination sleep.
            Assert.True(
                observed >= ExpectedCleanupAllowanceMs - CleanupAllowanceToleranceMs,
                $"Cleanup took only {observed}ms; the {ExpectedCleanupAllowanceMs}ms allowance was not honoured.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    // ====================================================================
    // Per-path resource release
    // ====================================================================

    /// <summary>
    /// Observes the resource-release contract for one execution path by instrumenting the state
    /// that the production <c>finally</c> block is responsible for:
    /// <list type="bullet">
    ///   <item>the <see cref="Process"/> object is disposed (member access throws);</item>
    ///   <item>the <c>Exited</c> handler is detached (the process' handler slot is empty);</item>
    ///   <item>the internal timeout <see cref="CancellationTokenSource"/> is cancelled and
    ///   disposed (its token's wait handle throws <see cref="ObjectDisposedException"/>);</item>
    ///   <item>the caller-token registration is disposed (the caller CTS holds no live callback).</item>
    /// </list>
    /// </summary>
    private sealed class ResourceReleaseObserver
    {
        private readonly List<Process> _processes = new();
        private readonly List<CancellationToken> _timeoutTokens = new();

        /// <summary>
        /// Wraps the timing seam so the timeout CTS' token is captured (the seam receives exactly
        /// that token) and the timeout still elapses only when the test releases it.
        /// </summary>
        public Func<int, CancellationToken, Task> WrapDelayFactory(Func<Task> gate) =>
            (_, token) =>
            {
                lock (_timeoutTokens) _timeoutTokens.Add(token);
                return gate().WaitAsync(token);
            };

        /// <summary>
        /// Records the live <see cref="Process"/> handed to the termination seam so its disposal
        /// can be probed after the call returns. Used on paths that reach cleanup termination.
        /// </summary>
        public void RecordProcess(Process process)
        {
            lock (_processes) _processes.Add(process);
        }

        public IReadOnlyList<Process> Processes
        {
            get { lock (_processes) return _processes.ToList(); }
        }

        public IReadOnlyList<CancellationToken> TimeoutTokens
        {
            get { lock (_timeoutTokens) return _timeoutTokens.ToList(); }
        }

        /// <summary>Asserts the recorded process object was disposed and its handler detached.</summary>
        public void AssertProcessReleased(string path)
        {
            var processes = Processes;
            Assert.True(processes.Count > 0, $"[{path}] no Process was captured, so disposal cannot be proven.");

            foreach (var process in processes)
            {
                Assert.True(
                    ProcessIsDisposed(process),
                    $"[{path}] the Process object was not disposed after the call completed.");

                Assert.Null(GetExitedHandler(process));
            }
        }

        /// <summary>Asserts the internal timeout CTS was cancelled and then disposed.</summary>
        public void AssertTimeoutSourceReleased(string path)
        {
            var tokens = TimeoutTokens;
            Assert.True(tokens.Count > 0, $"[{path}] the timing seam was never invoked, so its CTS cannot be probed.");

            foreach (var token in tokens)
            {
                // Cancelled: production cancels the timeout CTS as soon as the race is decided.
                Assert.True(
                    token.IsCancellationRequested,
                    $"[{path}] the internal timeout CancellationTokenSource was never cancelled.");

                // Disposed: a live CTS hands out a WaitHandle; a disposed one throws.
                Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            }
        }

        /// <summary>
        /// Reads <see cref="Process"/>' private <c>Exited</c> handler slot. A detached handler
        /// leaves it null; the mutant that skips detaching leaves it set even after disposal.
        /// </summary>
        private static Delegate? GetExitedHandler(Process process)
        {
            foreach (var field in typeof(Process).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(EventHandler))
                {
                    return field.GetValue(process) as Delegate;
                }
            }

            Assert.Fail("Could not locate the Process.Exited handler field on this runtime.");
            return null;
        }

        private static bool ProcessIsDisposed(Process process)
        {
            try
            {
                _ = process.StandardOutput;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                // Redirection state is gone too; still an unusable, released handle.
                return true;
            }
        }
    }

    /// <summary>
    /// Probes whether a caller-token registration is still live on the caller's own
    /// <see cref="CancellationTokenSource"/>. Production registers a callback on the caller token
    /// and must dispose that registration in its <c>finally</c> block.
    /// </summary>
    private static bool HasLiveCallerRegistration(CancellationTokenSource callerCts)
    {
        var registrationsField = typeof(CancellationTokenSource).GetField(
            "_registrations",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registrationsField);

        var registrations = registrationsField!.GetValue(callerCts);
        if (registrations is null) return false;

        var callbacksField = registrations.GetType().GetField(
            "Callbacks",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(callbacksField);

        return callbacksField!.GetValue(registrations) is not null;
    }

    [Fact]
    public async Task SuccessPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration()
    {
        using var fixture = new ProcessTreeFixture(maxDepth: 0, lifetimeMs: 1);
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();

            // The seam never completes on its own: the process exits and wins the race, which is
            // the success path. Capturing the token here proves the timeout CTS was released.
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.DelayFactory = observer.WrapDelayFactory(() => never.Task);

            // The success path never requests termination, so TreeTerminator is never invoked and
            // cannot be used to capture the Process here. The launch-observation seam hands the
            // test the very same Process instance the execution path owns, so its disposal and
            // Exited-handler detachment are genuinely observable rather than assumed.
            tools.ProcessStarted = observer.RecordProcess;

            var result = await WithGuardAsync(
                tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000),
                LivenessGuardMs,
                "A depth-0 fixture run must complete on its own.");

            Assert.Contains("--- EXIT CODE: 0 ---", result);

            observer.AssertProcessReleased("success");
            observer.AssertTimeoutSourceReleased("success");
            Assert.False(
                HasLiveCallerRegistration(callerCts),
                "[success] the caller-token registration was not disposed.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task TimeoutPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.DelayFactory = observer.WrapDelayFactory(() => gate.Task);

            // The termination seam receives the live Process object, which is exactly the object
            // the production finally block must dispose.
            tools.TreeTerminator = new CapturingTreeTerminator(observer, ProcessTreeTerminator.Default);

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            gate.TrySetResult(true);

            var result = await WithGuardAsync(run, LivenessGuardMs, "A timeout must return a string.");
            Assert.Contains("timed out after 30000ms", result);

            observer.AssertProcessReleased("timeout");
            observer.AssertTimeoutSourceReleased("timeout");
            Assert.False(
                HasLiveCallerRegistration(callerCts),
                "[timeout] the caller-token registration was not disposed.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task CancellationPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.DelayFactory = observer.WrapDelayFactory(() => never.Task);
            tools.TreeTerminator = new CapturingTreeTerminator(observer, ProcessTreeTerminator.Default);

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            callerCts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WithGuardAsync(run, LivenessGuardMs, "Cancellation must throw."));
            Assert.Equal(callerCts.Token, ex.CancellationToken);

            observer.AssertProcessReleased("cancellation");
            observer.AssertTimeoutSourceReleased("cancellation");

            // NARROWED CONTRACT (deliberate, empirically justified — not an oversight).
            //
            // The caller-registration disposal assertion carried by the four other paths is NOT
            // asserted here, because on this runtime no observable of it survives cancellation:
            //
            //  1. Cancel() executes and then CLEARS the source's registration list, so the
            //     reflection probe used elsewhere (HasLiveCallerRegistration) reports "no live
            //     registration" whether or not production disposed its registration. Asserting it
            //     here would be vacuous — it would pass even against the mutant.
            //  2. The other documented observable, "Dispose() blocks while this registration's own
            //     callback is running", cannot be reached either: production's caller-token
            //     callback only completes a TaskCompletionSource, so it is never still running by
            //     the time the finally block disposes the registration.
            //
            // Both facts are pinned as executable evidence by
            // CallerRegistrationDisposal_HasNoObservableAfterCancellation_OnThisRuntime below, so
            // this justification fails loudly if a future runtime changes either behaviour.
            //
            // Coverage is therefore carried by the four uncancelled paths (success, timeout,
            // ordinary error, degraded fallback), each of which asserts caller-registration
            // disposal and each of which fails if production stops disposing the registration.
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    /// <summary>
    /// Executable evidence for the narrowed contract documented in
    /// <see cref="CancellationPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration"/>.
    /// <para>
    /// It pins the two runtime behaviours that justify omitting the caller-registration assertion
    /// on the cancellation path, so the justification fails loudly rather than silently rotting if
    /// a future runtime changes either one:
    /// </para>
    /// <list type="number">
    ///   <item>the reflection probe used on the other paths is sensitive to disposal while the
    ///   source is NOT cancelled, but becomes insensitive once <c>Cancel()</c> has run (it reports
    ///   "no live registration" for a still-undisposed registration);</item>
    ///   <item><c>CancellationTokenRegistration.Dispose()</c> only blocks while that
    ///   registration's own callback is still executing — which never happens for production's
    ///   caller-token callback, since it merely completes a <see cref="TaskCompletionSource{T}"/>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void CallerRegistrationDisposal_HasNoObservableAfterCancellation_OnThisRuntime()
    {
        // (1a) Before cancellation the probe genuinely discriminates: live vs disposed.
        using (var uncancelled = new CancellationTokenSource())
        {
            var registration = uncancelled.Token.Register(() => { });
            Assert.True(
                HasLiveCallerRegistration(uncancelled),
                "The probe must observe a live registration on an uncancelled source.");

            registration.Dispose();
            Assert.False(
                HasLiveCallerRegistration(uncancelled),
                "The probe must observe disposal on an uncancelled source.");
        }

        // (1b) After cancellation the probe is insensitive: an UNDISPOSED registration already
        // reads as "not live", so asserting it on the cancellation path would be vacuous.
        using (var cancelled = new CancellationTokenSource())
        {
            var undisposed = cancelled.Token.Register(() => { });
            cancelled.Cancel();

            Assert.False(
                HasLiveCallerRegistration(cancelled),
                "Cancellation is expected to clear the registration list on this runtime; if this " +
                "fails the cancellation path can and must assert caller-registration disposal.");

            // The registration object itself is still undisposed here — proving the probe's
            // "false" above is about cancellation, not about disposal.
            undisposed.Dispose();
        }

        // (2) Dispose() only blocks for a callback that is still running. Production's caller
        // callback completes immediately, so there is no window to observe.
        using (var source = new CancellationTokenSource())
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = source.Token.Register(() => signal.TrySetResult(true));

            source.Cancel();
            Assert.True(signal.Task.IsCompleted, "The production-shaped callback completes synchronously with Cancel().");

            var elapsed = Stopwatch.StartNew();
            registration.Dispose();
            elapsed.Stop();

            Assert.True(
                elapsed.ElapsedMilliseconds < 250,
                $"Disposing a registration whose callback already finished must not block (took {elapsed.ElapsedMilliseconds}ms); " +
                "if it did block, that blocking would be an observable the cancellation path could assert.");
        }
    }

    [Fact]
    public async Task DegradedFallbackPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.DelayFactory = observer.WrapDelayFactory(() => gate.Task);

            // Supported but throwing: the degraded root-only fallback path.
            tools.TreeTerminator = new CapturingTreeTerminator(
                observer,
                inner: null,
                onTerminate: () => throw new InvalidOperationException("degraded-release-probe"));

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            gate.TrySetResult(true);

            var result = await WithGuardAsync(run, LivenessGuardMs, "Degraded cleanup must still complete.");
            Assert.Contains("Descendant cleanup was NOT established", result);

            observer.AssertProcessReleased("degraded");
            observer.AssertTimeoutSourceReleased("degraded");
            Assert.False(
                HasLiveCallerRegistration(callerCts),
                "[degraded] the caller-token registration was not disposed.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    [Fact]
    public async Task OrdinaryErrorPath_ReleasesProcess_Handler_TimeoutSource_AndCallerRegistration()
    {
        using var fixture = new ProcessTreeFixture();
        using var callerCts = new CancellationTokenSource();
        try
        {
            var observer = new ResourceReleaseObserver();
            var tools = fixture.CreateTools();

            // A faulting timing seam drives the ordinary-execution-error path *after* the process
            // has been started and the tree is established, so the same resources are in flight as
            // on the other paths (and the fixture records exist for identity-scoped release).
            var faultGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tools.DelayFactory = observer.WrapDelayFactory(() => faultGate.Task);
            tools.TreeTerminator = new CapturingTreeTerminator(observer, ProcessTreeTerminator.Default);

            var run = tools.execute_bash_command(fixture.RootCommand(), callerCts.Token, 30_000);
            await fixture.WaitUntilTreeReadyAsync(BarrierGuardMs, Ct);
            faultGate.TrySetException(new InvalidOperationException("ordinary-error-release-probe"));

            var result = await WithGuardAsync(run, LivenessGuardMs, "An ordinary error must return a string.");

            Assert.Contains("Error executing command: ordinary-error-release-probe", result);

            observer.AssertProcessReleased("ordinary-error");
            observer.AssertTimeoutSourceReleased("ordinary-error");
            Assert.False(
                HasLiveCallerRegistration(callerCts),
                "[ordinary-error] the caller-token registration was not disposed.");
        }
        finally
        {
            fixture.ReleaseRecordedProcesses();
        }
    }

    /// <summary>
    /// Termination seam that records the live <see cref="Process"/> the production code is
    /// operating on, then optionally delegates to a real terminator (or throws to drive the
    /// degraded path). Recording the object here is what lets the tests probe its disposal.
    /// </summary>
    private sealed class CapturingTreeTerminator : IProcessTreeTerminator
    {
        private readonly ResourceReleaseObserver _observer;
        private readonly IProcessTreeTerminator? _inner;
        private readonly Action? _onTerminate;

        public CapturingTreeTerminator(
            ResourceReleaseObserver observer,
            IProcessTreeTerminator? inner,
            Action? onTerminate = null)
        {
            _observer = observer;
            _inner = inner;
            _onTerminate = onTerminate;
        }

        public bool IsTreeTerminationSupported => true;

        /// <summary>True once the production cleanup pass actually requested tree termination.</summary>
        public bool TerminateTreeCalled { get; private set; }

        public void TerminateTree(Process process)
        {
            TerminateTreeCalled = true;
            _observer.RecordProcess(process);
            _onTerminate?.Invoke();
            _inner?.TerminateTree(process);
        }
    }

    // ====================================================================
    // Shared helpers
    // ====================================================================

    /// <summary>
    /// Bounded polling of a recorded PID. The bound is a failure guard: the success mechanism is
    /// the observed transition to "not running", never the elapsed time.
    /// </summary>
    private static async Task<bool> WaitUntilNotRunningAsync(int pid, int guardMs, CancellationToken ct)
    {
        var guard = Stopwatch.StartNew();
        while (guard.ElapsedMilliseconds < guardMs)
        {
            if (!ProcessProbe.IsRunning(pid)) return true;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        return !ProcessProbe.IsRunning(pid);
    }

    /// <summary>Failure guard around an awaited operation that must not hang.</summary>
    private static async Task<T> WithGuardAsync<T>(Task<T> task, int guardMs, string because)
    {
        var completed = await Task.WhenAny(task, Task.Delay(guardMs)).ConfigureAwait(false);
        Assert.True(completed == task, because + $" (still pending after {guardMs}ms)");
        return await task.ConfigureAwait(false);
    }

    // --------------------------------------------------------------------
    // Test doubles
    // --------------------------------------------------------------------

    /// <summary>Substitutes the internal tree-termination capability seam.</summary>
    private sealed class StubTreeTerminator : IProcessTreeTerminator
    {
        private readonly bool _supported;
        private readonly Action? _onTerminate;

        public StubTreeTerminator(bool supported, Action? onTerminate = null)
        {
            _supported = supported;
            _onTerminate = onTerminate;
        }

        public bool TerminateTreeCalled { get; private set; }

        public bool IsTreeTerminationSupported => _supported;

        public void TerminateTree(Process process)
        {
            TerminateTreeCalled = true;
            _onTerminate?.Invoke();
        }
    }

    /// <summary>Captures log entries so the cancellation log contract can be asserted.</summary>
    internal sealed class RecordingLogger : ILogger
    {
        private readonly List<Entry> _entries = new();

        internal sealed record Entry(LogLevel Level, string Message);

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public string Text => string.Join(Environment.NewLine, Entries.Select(e => e.Message));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null) message += " | " + exception;
            lock (_entries) _entries.Add(new Entry(logLevel, message));
        }
    }

    // --------------------------------------------------------------------
    // Process probing
    // --------------------------------------------------------------------

    internal static class ProcessProbe
    {
        /// <summary>
        /// Identity-scoped liveness probe for a recorded PID. Never enumerates or matches by name.
        /// </summary>
        public static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // No such process.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Identity-scoped best-effort kill of a single recorded PID.</summary>
        public static void KillByPid(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
        }
    }

    // --------------------------------------------------------------------
    // Fixtures
    // --------------------------------------------------------------------

    /// <summary>
    /// An unrelated process launched directly by the test (never through <see cref="BashTools"/>),
    /// used to prove that cleanup is identity-scoped and does not sweep unrelated processes.
    /// </summary>
    private sealed class SentinelProcess : IDisposable
    {
        private readonly string _rendezvous;
        private readonly Process _process;

        public SentinelProcess()
        {
            _rendezvous = FixturePaths.NewRendezvousDirectory();
            Identity = "sentinel-" + Guid.NewGuid().ToString("N");

            var startInfo = FixturePaths.CreateStartInfo(
                "grandchild",
                "--dir", _rendezvous,
                "--grandchild-id", Identity,
                "--lifetime-ms", "60000",
                "--barrier-timeout-ms", "60000",
                "--expect", "grandchild");
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the sentinel process.");
        }

        public string Identity { get; }

        public int Pid => _process.Id;

        public bool IsRunning => ProcessProbe.IsRunning(_process.Id);

        public void Release()
        {
            ProcessProbe.KillByPid(_process.Id);
            FixturePaths.TryDeleteDirectory(_rendezvous);
        }

        public void Dispose()
        {
            Release();
            try { _process.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Owns a real fixture process tree: the working directory, the rendezvous directory used for
    /// readiness barriers, the recorded identities/PIDs and identity-scoped release.
    /// </summary>
    private sealed class ProcessTreeFixture : IDisposable
    {
        private readonly string _rendezvous;
        private readonly bool _rootExitsAfterReady;
        private readonly int _maxDepth;
        private readonly int _lifetimeMs;
        private readonly TaskCompletionSource<bool> _timeoutGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<RecordedProcess> _recorded = new();
        private readonly List<int> _observedTimeouts = new();

        public ProcessTreeFixture(bool rootExitsAfterReady = false, int maxDepth = 2, int lifetimeMs = 60_000)
        {
            _rendezvous = FixturePaths.NewRendezvousDirectory();
            WorkDirectory = FixturePaths.FixtureDirectory;
            _rootExitsAfterReady = rootExitsAfterReady;
            _maxDepth = maxDepth;
            _lifetimeMs = lifetimeMs;

            RootIdentity = "root-" + Guid.NewGuid().ToString("N");
            ChildIdentity = "child-" + Guid.NewGuid().ToString("N");
            GrandchildIdentity = "grandchild-" + Guid.NewGuid().ToString("N");
        }

        public string WorkDirectory { get; }
        public string RootIdentity { get; }
        public string ChildIdentity { get; }
        public string GrandchildIdentity { get; }

        /// <summary>
        /// Markers that the fixture prints ONLY to stdout/stderr. They never appear on any command
        /// line, so their absence from a log or exception proves captured output was not copied.
        /// </summary>
        public IReadOnlyList<string> OutputOnlyTokens => new[]
        {
            "OUT-" + RootIdentity,
            "OUT-" + ChildIdentity,
            "OUT-" + GrandchildIdentity
        };

        public IReadOnlyList<RecordedProcess> RecordedProcesses
        {
            get { lock (_recorded) return _recorded.ToList(); }
        }

        private IReadOnlyList<string> ExpectedRoles => _maxDepth switch
        {
            0 => new[] { "root" },
            1 => new[] { "root", "child" },
            _ => new[] { "root", "child", "grandchild" }
        };

        /// <summary>The shell command that launches the fixture root through <see cref="BashTools"/>.</summary>
        public string RootCommand()
        {
            var parts = new List<string>(FixturePaths.LaunchPrefix()) { "root" };
            parts.AddRange(new[]
            {
                "--dir", _rendezvous,
                "--root-id", RootIdentity,
                "--child-id", ChildIdentity,
                "--grandchild-id", GrandchildIdentity,
                "--lifetime-ms", _lifetimeMs.ToString(CultureInfo.InvariantCulture),
                "--barrier-timeout-ms", BarrierGuardMs.ToString(CultureInfo.InvariantCulture),
                "--max-depth", _maxDepth.ToString(CultureInfo.InvariantCulture),
                "--expect", string.Join(",", ExpectedRoles)
            });

            if (_rootExitsAfterReady)
            {
                parts.Add("--exit-after-ready");
                parts.Add("root");
            }

            return string.Join(" ", parts.Select(Quote));
        }

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        /// <summary>
        /// Releases the timing seam used by <see cref="BashTools.DelayFactory"/> for this fixture's
        /// run. Installed lazily by <see cref="Install"/>.
        /// </summary>
        public void TripTimeout() => _timeoutGate.TrySetResult(true);

        /// <summary>
        /// Waits until every expected fixture role has published its readiness record, recording
        /// each role's identity and PID. The bound is a failure guard, not the readiness proof.
        /// </summary>
        public async Task WaitUntilTreeReadyAsync(int guardMs, CancellationToken ct)
        {
            var guard = Stopwatch.StartNew();
            while (guard.ElapsedMilliseconds < guardMs)
            {
                var records = ReadRecordedProcesses();
                if (records.Count == ExpectedRoles.Count)
                {
                    lock (_recorded)
                    {
                        _recorded.Clear();
                        _recorded.AddRange(records);
                    }

                    foreach (var record in records)
                    {
                        // The root is allowed to have exited already only in the explicit
                        // root-exited vector; every other recorded process must be live at readiness.
                        var mayHaveExited = _rootExitsAfterReady && record.Role == "root";
                        Assert.True(
                            mayHaveExited || ProcessProbe.IsRunning(record.Pid),
                            $"Recorded fixture process {record.Role} (pid={record.Pid}) was not running at readiness.");
                    }

                    return;
                }

                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            var found = string.Join(", ", ReadRecordedProcesses().Select(r => r.Role));
            Assert.Fail($"Fixture readiness barrier was not reached within {guardMs}ms. Roles observed: [{found}].");
        }

        /// <summary>Waits until the recorded root process is no longer running (failure-guarded).</summary>
        public async Task WaitUntilRootExitedAsync(int guardMs, CancellationToken ct)
        {
            var root = RecordedProcesses.SingleOrDefault(p => p.Role == "root");
            Assert.NotNull(root);

            var guard = Stopwatch.StartNew();
            while (guard.ElapsedMilliseconds < guardMs)
            {
                if (!ProcessProbe.IsRunning(root!.Pid)) return;
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            Assert.Fail($"The fixture root (pid={root!.Pid}) did not exit within {guardMs}ms.");
        }

        /// <summary>
        /// Waits until every expected role has flushed its identity banner. Readiness records are
        /// only published after the banner is written and flushed, so the records are the proof.
        /// </summary>
        public Task WaitUntilAllIdentitiesFlushedAsync(int guardMs, CancellationToken ct) =>
            WaitUntilTreeReadyAsync(guardMs, ct);

        public bool AnyReadinessRecordExists() =>
            Directory.Exists(_rendezvous) && Directory.GetFiles(_rendezvous, "*.ready").Length > 0;

        /// <summary>Parses the fixture's own readiness records into recorded identities and PIDs.</summary>
        public IReadOnlyList<RecordedProcess> ReadRecordedProcesses()
        {
            var results = new List<RecordedProcess>();
            if (!Directory.Exists(_rendezvous)) return results;

            foreach (var role in ExpectedRoles)
            {
                var path = Path.Combine(_rendezvous, role + ".ready");
                if (!File.Exists(path)) continue;

                string content;
                try
                {
                    content = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue;
                }

                var fields = content
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split(new[] { '=' }, 2))
                    .Where(kv => kv.Length == 2)
                    .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);

                if (fields.TryGetValue("pid", out var pidText) &&
                    int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                    fields.TryGetValue("id", out var identity))
                {
                    results.Add(new RecordedProcess(role, identity, pid));
                }
            }

            return results;
        }

        /// <summary>
        /// Identity-scoped release of every recorded fixture process. Only PIDs recorded by the
        /// fixture itself are touched; nothing is matched by name and nothing is swept.
        /// </summary>
        public void ReleaseRecordedProcesses()
        {
            TripTimeout();

            // Re-read so descendants that published after the last snapshot are released too.
            var known = new Dictionary<int, RecordedProcess>();
            foreach (var record in RecordedProcesses.Concat(ReadRecordedProcesses()))
            {
                known[record.Pid] = record;
            }

            // Deepest first, so a parent cannot respawn work while descendants are being released.
            foreach (var record in known.Values.OrderByDescending(r => r.Role switch
            {
                "grandchild" => 2,
                "child" => 1,
                _ => 0
            }))
            {
                ProcessProbe.KillByPid(record.Pid);
            }
        }

        public void Dispose()
        {
            ReleaseRecordedProcesses();
            FixturePaths.TryDeleteDirectory(_rendezvous);
        }

        /// <summary>
        /// Creates a <see cref="BashTools"/> bound to this fixture with the existing
        /// <see cref="BashTools.DelayFactory"/> seam installed so the timeout elapses only when
        /// <see cref="TripTimeout"/> is called. The effective millisecond value is still selected
        /// by production code and is recorded in <see cref="ObservedTimeouts"/>.
        /// </summary>
        public BashTools CreateTools(ILogger? logger = null)
        {
            var tools = new BashTools(WorkDirectory, 120_000, logger);
            tools.DelayFactory = (timeoutMs, token) =>
            {
                lock (_observedTimeouts) _observedTimeouts.Add(timeoutMs);
                return _timeoutGate.Task.WaitAsync(token);
            };

            return tools;
        }

        public IReadOnlyList<int> ObservedTimeouts
        {
            get { lock (_observedTimeouts) return _observedTimeouts.ToList(); }
        }
    }

    internal sealed record RecordedProcess(string Role, string Identity, int Pid);

    /// <summary>
    /// Locates the copied fixture assets and builds launch commands for them. The fixture is
    /// launched through the same runtime host that is running the tests, so no ambient
    /// <c>DOTNET_ROOT</c> is required.
    /// </summary>
    internal static class FixturePaths
    {
        private const string FixtureSubdirectory = "ProcessFixture";
        private const string FixtureAssemblyName = "SharpCoder.ProcessFixture";

        public static string FixtureDirectory { get; } = ResolveFixtureDirectory();

        public static string FixtureAssemblyPath { get; } =
            Path.Combine(FixtureDirectory, FixtureAssemblyName + ".dll");

        private static string ResolveFixtureDirectory()
        {
            var baseDirectory = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDirectory, FixtureSubdirectory);
            if (File.Exists(Path.Combine(candidate, FixtureAssemblyName + ".dll")))
            {
                return candidate;
            }

            throw new InvalidOperationException(
                $"The process fixture was not found at '{candidate}'. " +
                "The test project must copy the SharpCoder.ProcessFixture output into that directory.");
        }

        /// <summary>
        /// The command prefix used to launch the fixture. The <c>dotnet</c> muxer that hosts the
        /// current runtime is preferred (it is guaranteed to exist and needs no ambient
        /// <c>DOTNET_ROOT</c>); the fixture apphost is the fallback.
        /// </summary>
        public static IReadOnlyList<string> LaunchPrefix() => LaunchPrefixValue;

        private static readonly IReadOnlyList<string> LaunchPrefixValue = ResolveLaunchPrefix();

        private static IReadOnlyList<string> ResolveLaunchPrefix()
        {
            var muxer = TryResolveMuxer();
            if (muxer is not null)
            {
                return new[] { muxer, FixtureAssemblyPath };
            }

            var appHost = Path.Combine(
                FixtureDirectory,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? FixtureAssemblyName + ".exe"
                    : FixtureAssemblyName);

            if (!File.Exists(appHost))
            {
                throw new InvalidOperationException(
                    $"Neither the dotnet muxer nor the fixture apphost ('{appHost}') could be located.");
            }

            return new[] { appHost };
        }

        /// <summary>
        /// Derives the muxer path from the loaded runtime directory
        /// (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;ver&gt;/</c> =&gt; <c>&lt;root&gt;/dotnet</c>),
        /// falling back to <c>DOTNET_ROOT</c> and finally to the current process when it is the muxer.
        /// </summary>
        private static string? TryResolveMuxer()
        {
            var muxerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var root = new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent?.FullName;
            if (root is not null)
            {
                var candidate = Path.Combine(root, muxerName);
                if (File.Exists(candidate)) return candidate;
            }

            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                var candidate = Path.Combine(dotnetRoot!, muxerName);
                if (File.Exists(candidate)) return candidate;
            }

            var host = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(host) &&
                string.Equals(Path.GetFileName(host), muxerName, StringComparison.OrdinalIgnoreCase))
            {
                return host;
            }

            return null;
        }

        public static ProcessStartInfo CreateStartInfo(params string[] arguments)
        {
            var prefix = LaunchPrefix();
            var startInfo = new ProcessStartInfo
            {
                FileName = prefix[0],
                WorkingDirectory = FixtureDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            for (var i = 1; i < prefix.Count; i++)
            {
                startInfo.ArgumentList.Add(prefix[i]);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        public static string NewRendezvousDirectory()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "sharpcoder-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        public static void TryDeleteDirectory(string directory)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
