using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder.Tools;

public sealed class BashTools
{
    /// <summary>
    /// Single fixed allowance (milliseconds) for <em>all</em> termination and managed cleanup work
    /// after a command has been interrupted or has finished. It is deliberately independent of the
    /// caller's (possibly already cancelled) token so cleanup still runs after cancellation.
    /// The bound covers managed waits only: synchronous OS operations such as process start, kill
    /// and dispose are not forcibly interruptible.
    /// </summary>
    internal const int CleanupAllowanceMs = 5000;

    private readonly string _workingDirectory;
    private readonly int _timeoutMs;
    private readonly ILogger _logger;
    private readonly string? _shellPathOverride;
    private readonly Func<string, string>? _shellArgsFormat;

    public BashTools(string workingDirectory, int timeoutMs = 120000, ILogger? logger = null)
        : this(workingDirectory, timeoutMs, logger, null, null)
    {
    }

    public BashTools(
        string workingDirectory,
        int timeoutMs,
        ILogger? logger,
        string? shellPathOverride,
        Func<string, string>? shellArgsFormat)
    {
        _workingDirectory = workingDirectory;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 120000;
        _logger = logger ?? NullLogger.Instance;
        _shellPathOverride = shellPathOverride;
        _shellArgsFormat = shellArgsFormat;
    }

    /// <summary>
    /// Internal timing seam so tests can observe the effective per-call timeout without
    /// actually waiting for it. Defaults to <see cref="Task.Delay(int, CancellationToken)"/>.
    /// The returned task represents the whole effective budget: it starts right after a
    /// successful process start and stays armed until the root process has exited <em>and</em>
    /// both redirected readers have reached EOF.
    /// </summary>
    internal Func<int, CancellationToken, Task> DelayFactory { get; set; } =
        static (timeoutMs, token) => Task.Delay(timeoutMs, token);

    /// <summary>
    /// Internal termination/capability seam. Defaults to the reflection-based adapter over the
    /// runtime's <c>Process.Kill(entireProcessTree)</c> overload. Tests substitute this to
    /// simulate an unsupported or throwing capability. This is a test seam, not a public
    /// configuration surface.
    /// </summary>
    internal IProcessTreeTerminator TreeTerminator { get; set; } = ProcessTreeTerminator.Default;

    /// <summary>
    /// Internal launch-observation seam. Invoked once with the freshly started process so tests can
    /// hold the same <see cref="Process"/> instance the execution path owns and later assert that it
    /// was released. It exists because the success path never reaches
    /// <see cref="TreeTerminator"/>, which is otherwise the only seam that sees the process.
    /// <para>
    /// This is a pure observation hook and a test seam, not a public configuration surface: it
    /// defaults to <see langword="null"/>, is never invoked in production, and must not be used to
    /// influence execution.
    /// </para>
    /// </summary>
    internal Action<Process>? ProcessStarted { get; set; }

    /// <summary>
    /// Compatibility entry point preserving the original two-parameter signature.
    /// Forwards to the timeout-aware overload using the instance default timeout.
    /// This overload is intentionally not the one registered as an LLM tool.
    /// </summary>
    public Task<string> execute_bash_command(
        string command,
        CancellationToken ct = default)
        => execute_bash_command(command, ct, null);

    /// <summary>
    /// Runs <paramref name="command"/> in a fresh shell process and returns its transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The effective timeout is a deadline over the whole capture: it starts immediately after the
    /// process is started and remains active until the root process has exited <em>and</em> both
    /// redirected output readers have reached EOF. A surviving grandchild that inherited the
    /// stdout/stderr pipes can therefore never make this call wait indefinitely.
    /// </para>
    /// <para>
    /// <strong>Documented limitation.</strong> Descendant termination is only attempted through the
    /// runtime's <c>Process.Kill(entireProcessTree)</c> capability while the root process is still
    /// alive. When the root has already exited, when descendants escaped the process ancestry, or
    /// when they were reparented, this implementation cannot reliably locate or terminate them: no
    /// stale-PID guessing, no process-name killing and no scanning of unrelated processes is
    /// performed. In those cases the timeout and cancellation contracts are still honoured through
    /// pipe-drain handling and stream closure, and the diagnostics explicitly report that
    /// descendant cleanup was not established — a clean tree is never claimed. Reliable ownership
    /// after root exit requires POSIX process groups or Windows Job Objects, which is a separate
    /// design and out of scope here.
    /// </para>
    /// </remarks>
    [Description("Executes a given bash command. Each invocation starts a fresh shell process; no shell state (working directory, variables, background jobs) is carried between calls. An optional per-invocation timeout (timeout_ms, in milliseconds) selects how long the shell is waited on for this call only; when it is omitted the tool's configured default timeout is used (normally 120000 ms). The timeout is a deadline over the whole capture: it stays active until the process has exited and its stdout/stderr have been fully drained, so a lingering child holding the output pipes open cannot make the call hang. Pass a larger budget for known long-running work, for example 900000 for a multi-minute validation run. timeout_ms must be greater than zero; zero or negative values are rejected.")]
    public async Task<string> execute_bash_command(
        [Description("The command to execute")] string command,
        CancellationToken ct,
        [Description("Optional timeout for this invocation only, in milliseconds. Omit to use the configured default timeout (normally 120000 ms). The budget covers waiting for the process to exit and for its stdout/stderr to finish draining. Must be greater than zero; pass a longer budget such as 900000 for a known multi-minute validation run.")] int? timeout_ms = null)
    {
        if (timeout_ms.HasValue && timeout_ms.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout_ms),
                timeout_ms.Value,
                "timeout_ms must be greater than zero when specified.");
        }

        var effectiveTimeoutMs = timeout_ms ?? _timeoutMs;

        // A call that is already cancelled must not launch anything.
        ct.ThrowIfCancellationRequested();

        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        EventHandler? exitedHandler = null;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenRegistration cancelRegistration = default;
        CleanupReport? report = null;
        var exitSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            string shell;
            string args;
            if (!string.IsNullOrEmpty(_shellPathOverride))
            {
                shell = _shellPathOverride!;
                args = _shellArgsFormat is not null
                    ? _shellArgsFormat(command)
                    // Default assumes a bash-compatible shell: -c "<cmd>" with "-escaping.
                    : $"-c \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            }
            else
            {
                shell = isWindows ? "cmd.exe" : "/bin/bash";
                args = isWindows ? $"/c \"{command}\"" : $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = args,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // The synchronous launch itself sits outside the timeout budget.
            process = Process.Start(processStartInfo);
            if (process == null)
            {
                return "Failed to start process.";
            }

            _logger.LogDebug("Executing: {Command} (pid={Pid})", command, process.Id);

            // Observation-only seam (see ProcessStarted). Never set in production; a throwing
            // observer must not be able to change the outcome of the command.
            if (ProcessStarted is { } observer)
            {
                try { observer(process); } catch (Exception) { }
            }

            // From here on the effective budget is armed.
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();

            exitedHandler = (_, _) => exitSignal.TrySetResult(true);
            process.EnableRaisingEvents = true;
            process.Exited += exitedHandler;
            if (SafeHasExited(process))
            {
                exitSignal.TrySetResult(true);
            }

            timeoutCts = new CancellationTokenSource();
            var timeoutTask = DelayFactory(effectiveTimeoutMs, timeoutCts.Token);

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelRegistration = ct.Register(() => cancelSignal.TrySetResult(true));

            // "Done" means: root exited AND both redirected readers reached EOF.
            var captureCompleted = Task.WhenAll(
                Settle(exitSignal.Task),
                Settle(outputTask),
                Settle(errorTask));

            var winner = await Task.WhenAny(captureCompleted, timeoutTask, cancelSignal.Task).ConfigureAwait(false);

            // Release the timer as soon as the race is decided.
            timeoutCts.Cancel();
            Observe(timeoutTask);

            if (winner == timeoutTask && timeoutTask.IsFaulted)
            {
                // A broken timing seam is an ordinary execution error, not a timeout.
                await timeoutTask.ConfigureAwait(false);
            }

            var timedOut = winner == timeoutTask && !ct.IsCancellationRequested;

            if (timedOut)
            {
                _logger.LogWarning("Command timed out after {TimeoutMs}ms: {Command}", effectiveTimeoutMs, command);
            }

            // All termination and managed cleanup happens under one fixed allowance.
            // Termination is only requested when the capture did not complete on its own.
            report = await CleanupAsync(
                process,
                exitSignal.Task,
                outputTask,
                errorTask,
                requiresTermination: winner != captureCompleted).ConfigureAwait(false);
            LogCleanup(report);

            // Cancellation observed before the outcome is committed wins — including cancellation
            // that arrived while cleanup was running.
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Command was cancelled by the caller; cleanup completed={CleanupComplete}, root exit observed={RootExited}, descendant cleanup established={TreeTerminated}, output drained={OutputComplete}.",
                    report.IsComplete,
                    report.RootExitObserved,
                    report.TreeTerminationRequested,
                    report.OutputComplete);
                throw new OperationCanceledException(ct);
            }

            if (timedOut)
            {
                return BuildInterruptedResult($"Command timed out after {effectiveTimeoutMs}ms.", outputTask, errorTask, report);
            }

            return BuildCompletedResult(process, outputTask, errorTask, report);
        }
        catch (OperationCanceledException)
        {
            if (process != null)
            {
                report ??= await CleanupAsync(process, exitSignal.Task, outputTask, errorTask, requiresTermination: true).ConfigureAwait(false);
                LogCleanup(report);
            }

            if (ct.IsCancellationRequested)
            {
                // Never copy captured stdout/stderr into logs or the exception payload here.
                throw new OperationCanceledException(ct);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (process != null)
            {
                report ??= await CleanupAsync(process, exitSignal.Task, outputTask, errorTask, requiresTermination: true).ConfigureAwait(false);
                LogCleanup(report);
            }

            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            _logger.LogWarning("Error executing command: {Message}", ex.Message);
            return BuildInterruptedResult($"Error executing command: {ex.Message}", outputTask, errorTask, report);
        }
        finally
        {
            cancelRegistration.Dispose();

            if (timeoutCts != null)
            {
                try { timeoutCts.Cancel(); } catch (ObjectDisposedException) { }
                timeoutCts.Dispose();
            }

            if (process != null)
            {
                if (exitedHandler != null)
                {
                    try { process.Exited -= exitedHandler; } catch (Exception) { }
                }

                // Any reader still pending will fault once the handles go away; those faults are
                // already observed by the Settle wrappers created above.
                try { process.Dispose(); } catch (Exception) { }
            }
        }
    }

    // ------------------------------------------------------------------
    // Cleanup
    // ------------------------------------------------------------------

    /// <summary>
    /// Record of what the single bounded cleanup pass could actually establish. Nothing here ever
    /// asserts that the descendant tree is clean — only what was requested and observed.
    /// </summary>
    internal sealed class CleanupReport
    {
        /// <summary>True when tree termination was requested through a supported runtime capability.</summary>
        public bool TreeTerminationRequested { get; set; }

        /// <summary>Why descendant cleanup could not be established, when it could not.</summary>
        public string? DegradedReason { get; set; }

        /// <summary>True when a best-effort root-only kill was issued as a fallback.</summary>
        public bool RootOnlyKillAttempted { get; set; }

        /// <summary>True when the root process was observed to exit. Not proof that descendants exited.</summary>
        public bool RootExitObserved { get; set; }

        /// <summary>True when both redirected readers reached EOF successfully.</summary>
        public bool OutputComplete { get; set; }

        /// <summary>True when the owned redirected streams had to be closed to release resources.</summary>
        public bool StreamsClosed { get; set; }

        /// <summary>True when nothing was left incomplete.</summary>
        public bool IsComplete => RootExitObserved && OutputComplete && DegradedReason is null;
    }

    private async Task<CleanupReport> CleanupAsync(
        Process process,
        Task exitSignal,
        Task<string>? outputTask,
        Task<string>? errorTask,
        bool requiresTermination)
    {
        var report = new CleanupReport();
        var allowance = Stopwatch.StartNew();
        int Remaining() => (int)Math.Max(0, CleanupAllowanceMs - allowance.ElapsedMilliseconds);

        if (requiresTermination)
        {
            if (!SafeHasExited(process))
            {
                var terminator = TreeTerminator;
                var supported = false;
                try
                {
                    supported = terminator.IsTreeTerminationSupported;
                }
                catch (Exception ex)
                {
                    report.DegradedReason =
                        $"the process-tree termination capability could not be probed ({ex.GetType().Name}: {ex.Message})";
                }

                if (supported)
                {
                    try
                    {
                        terminator.TerminateTree(process);
                        report.TreeTerminationRequested = true;
                    }
                    catch (Exception ex)
                    {
                        report.DegradedReason =
                            $"process-tree termination failed ({ex.GetType().Name}: {ex.Message}); only a best-effort root-only kill was issued";
                        KillRootOnly(process, report);
                    }
                }
                else
                {
                    report.DegradedReason ??=
                        "this runtime does not expose a usable Process.Kill(entireProcessTree) overload; only a best-effort root-only kill was issued";
                    KillRootOnly(process, report);
                }
            }
            else
            {
                // The root is already gone: surviving descendants cannot be located from here.
                report.DegradedReason =
                    "the root process had already exited, so any surviving descendant could not be located or terminated";
            }
        }

        // Await root termination within the remaining allowance.
        report.RootExitObserved =
            await AwaitWithinAsync(exitSignal, Remaining()).ConfigureAwait(false) || SafeHasExited(process);

        // Settle the output reads within whatever is left of the allowance.
        var reads = Task.WhenAll(Settle(outputTask), Settle(errorTask));
        var settled = await AwaitWithinAsync(reads, Remaining()).ConfigureAwait(false);
        report.OutputComplete = settled && ReadSucceeded(outputTask) && ReadSucceeded(errorTask);

        if (!report.OutputComplete)
        {
            // A descendant is still holding a pipe (or a read failed): close the streams we own so
            // the call cannot block, and let the Settle wrappers observe any resulting fault.
            CloseRedirectedStreams(process, report);
        }

        return report;
    }

    private void KillRootOnly(Process process, CleanupReport report)
    {
        report.RootOnlyKillAttempted = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception ex)
        {
            report.DegradedReason += $"; the root-only kill also failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static void CloseRedirectedStreams(Process process, CleanupReport report)
    {
        try { process.StandardOutput.Dispose(); } catch (Exception) { }
        try { process.StandardError.Dispose(); } catch (Exception) { }
        report.StreamsClosed = true;
    }

    private void LogCleanup(CleanupReport report)
    {
        if (report.DegradedReason is not null)
        {
            _logger.LogWarning(
                "Degraded cleanup: descendant cleanup was not established ({Reason}). Root exit observed={RootExited}, output drained={OutputComplete}.",
                report.DegradedReason,
                report.RootExitObserved,
                report.OutputComplete);
        }

        if (!report.RootExitObserved)
        {
            _logger.LogWarning(
                "Incomplete cleanup: the root process was not observed to exit within the {AllowanceMs}ms cleanup allowance.",
                CleanupAllowanceMs);
        }

        if (!report.OutputComplete)
        {
            _logger.LogWarning(
                "Incomplete cleanup: a redirected output reader did not reach EOF within the {AllowanceMs}ms cleanup allowance; streams closed={StreamsClosed}. Captured output may be truncated.",
                CleanupAllowanceMs,
                report.StreamsClosed);
        }
    }

    // ------------------------------------------------------------------
    // Result construction
    // ------------------------------------------------------------------

    private static string BuildCompletedResult(
        Process process,
        Task<string>? outputTask,
        Task<string>? errorTask,
        CleanupReport report)
    {
        var output = CompletedText(outputTask);
        var error = CompletedText(errorTask);
        var exitCode = TryGetExitCode(process);

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(output))
        {
            sb.AppendLine("--- STDOUT ---");
            sb.AppendLine(output);
        }
        if (!string.IsNullOrEmpty(error))
        {
            sb.AppendLine("--- STDERR ---");
            sb.AppendLine(error);
        }

        if (sb.Length == 0)
        {
            if (exitCode is null)
            {
                sb.AppendLine("Command produced no output; its exit code could not be observed.");
            }
            else if (exitCode.Value == 0)
            {
                sb.AppendLine("Command executed successfully with no output.");
            }
            else
            {
                // A nonzero exit is never labelled successful, even without output.
                sb.AppendLine($"Command failed with exit code {exitCode.Value} and produced no output.");
            }
        }

        sb.AppendLine(exitCode is null
            ? "--- EXIT CODE: not observed ---"
            : $"--- EXIT CODE: {exitCode.Value} ---");

        AppendIncompleteDiagnostics(sb, report);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the string result for a timeout or an ordinary execution error: the truthful headline,
    /// whatever reads have completed, and explicit diagnostics when something could not be finished.
    /// No exit code is invented and no complete transcript is claimed.
    /// </summary>
    private static string BuildInterruptedResult(
        string headline,
        Task<string>? outputTask,
        Task<string>? errorTask,
        CleanupReport? report)
    {
        var output = CompletedText(outputTask);
        var error = CompletedText(errorTask);

        var sb = new StringBuilder();
        sb.AppendLine(headline);
        if (!string.IsNullOrEmpty(output))
        {
            sb.AppendLine("--- STDOUT ---");
            sb.AppendLine(output);
        }
        if (!string.IsNullOrEmpty(error))
        {
            sb.AppendLine("--- STDERR ---");
            sb.AppendLine(error);
        }

        if (report is not null)
        {
            AppendIncompleteDiagnostics(sb, report, alwaysReportTermination: true);
        }

        return sb.ToString();
    }

    private static void AppendIncompleteDiagnostics(
        StringBuilder sb,
        CleanupReport report,
        bool alwaysReportTermination = false)
    {
        var degraded = report.DegradedReason is not null;
        var incompleteOutput = !report.OutputComplete;
        var incompleteExit = !report.RootExitObserved;

        if (!degraded && !incompleteOutput && !incompleteExit && !alwaysReportTermination)
        {
            return;
        }

        sb.AppendLine("--- CLEANUP DIAGNOSTICS ---");

        if (degraded)
        {
            sb.AppendLine($"Descendant cleanup was NOT established: {report.DegradedReason}.");
        }
        else if (report.TreeTerminationRequested)
        {
            sb.AppendLine("Process-tree termination (entireProcessTree) was requested for the root process and its descendants.");
        }

        if (incompleteExit)
        {
            sb.AppendLine($"The root process was NOT observed to exit within the {CleanupAllowanceMs}ms cleanup allowance.");
        }
        else if (alwaysReportTermination)
        {
            sb.AppendLine("The root process exit was observed; root exit alone is not proof that all descendants exited.");
        }

        if (incompleteOutput)
        {
            sb.AppendLine($"Output capture is INCOMPLETE: a redirected reader did not reach EOF within the {CleanupAllowanceMs}ms cleanup allowance, so the captured output above may be truncated.");
            if (report.StreamsClosed)
            {
                sb.AppendLine("The redirected output streams were closed to release resources; descendant cleanup was not established.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    private static string CompletedText(Task<string>? task) =>
        task is not null && task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;

    private static bool ReadSucceeded(Task<string>? task) =>
        task is null || task.Status == TaskStatus.RanToCompletion;

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : (int?)null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            // An unusable handle cannot be waited on either; treat it as gone.
            return true;
        }
    }

    /// <summary>Awaits <paramref name="task"/> for at most <paramref name="remainingMs"/>; never throws.</summary>
    private static async Task<bool> AwaitWithinAsync(Task task, int remainingMs)
    {
        if (task.IsCompleted) return true;
        if (remainingMs <= 0) return false;

        using var delayCts = new CancellationTokenSource();
        var delay = Task.Delay(remainingMs, delayCts.Token);
        var winner = await Task.WhenAny(Settle(task), delay).ConfigureAwait(false);
        if (winner != delay)
        {
            delayCts.Cancel();
            Observe(delay);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Wraps a task so that its completion can be awaited without throwing and so that a fault
    /// arriving later (for example after the redirected streams were closed) is always observed.
    /// </summary>
    private static async Task Settle(Task? task)
    {
        if (task is null) return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally swallowed: the point of this wrapper is to observe the fault.
        }
    }

    /// <summary>Marks a task's eventual fault as observed without waiting for it.</summary>
    private static void Observe(Task? task)
    {
        if (task is null) return;
        _ = Settle(task);
    }
}
