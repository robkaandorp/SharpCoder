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
    /// </summary>
    internal Func<int, CancellationToken, Task> DelayFactory { get; set; } =
        static (timeoutMs, token) => Task.Delay(timeoutMs, token);

    /// <summary>
    /// Compatibility entry point preserving the original two-parameter signature.
    /// Forwards to the timeout-aware overload using the instance default timeout.
    /// This overload is intentionally not the one registered as an LLM tool.
    /// </summary>
    public Task<string> execute_bash_command(
        string command,
        CancellationToken ct = default)
        => execute_bash_command(command, ct, null);

    [Description("Executes a given bash command. Each invocation starts a fresh shell process; no shell state (working directory, variables, background jobs) is carried between calls. An optional per-invocation timeout (timeout_ms, in milliseconds) selects how long the shell is waited on for this call only; when it is omitted the tool's configured default timeout is used (normally 120000 ms). Pass a larger budget for known long-running work, for example 900000 for a multi-minute validation run. timeout_ms must be greater than zero; zero or negative values are rejected.")]
    public async Task<string> execute_bash_command(
        [Description("The command to execute")] string command,
        CancellationToken ct,
        [Description("Optional timeout for this invocation only, in milliseconds. Omit to use the configured default timeout (normally 120000 ms). Must be greater than zero; pass a longer budget such as 900000 for a known multi-minute validation run.")] int? timeout_ms = null)
    {
        if (timeout_ms.HasValue && timeout_ms.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout_ms),
                timeout_ms.Value,
                "timeout_ms must be greater than zero when specified.");
        }

        var effectiveTimeoutMs = timeout_ms ?? _timeoutMs;

        Process? process = null;
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

            process = Process.Start(processStartInfo);
            if (process == null)
            {
                return "Failed to start process.";
            }

            _logger.LogDebug("Executing: {Command} (pid={Pid})", command, process.Id);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var processCompletionSource = new TaskCompletionSource<bool>();
            
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => processCompletionSource.TrySetResult(true);
            
            if (process.HasExited)
            {
                processCompletionSource.TrySetResult(true);
            }

            var timeoutTask = DelayFactory(effectiveTimeoutMs, cts.Token);
            var completedTask = await Task.WhenAny(processCompletionSource.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Command timed out after {TimeoutMs}ms: {Command}", effectiveTimeoutMs, command);
                KillProcess(process);
                return $"Command timed out after {effectiveTimeoutMs}ms.";
            }

            cts.Cancel();

            var output = await outputTask;
            var error = await errorTask;

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
                sb.AppendLine("Command executed successfully with no output.");
            }

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            KillProcess(process);
            return $"Error executing command: {ex.Message}";
        }
    }

    private static void KillProcess(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch { }
    }
}
