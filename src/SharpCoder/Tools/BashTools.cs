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

    [Description("Executes a given bash command in a persistent shell session with optional timeout, ensuring proper handling and security measures.")]
    public async Task<string> execute_bash_command(
        [Description("The command to execute")] string command,
        CancellationToken ct = default)
    {
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

            var timeoutTask = Task.Delay(_timeoutMs, cts.Token);
            var completedTask = await Task.WhenAny(processCompletionSource.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Command timed out after {TimeoutMs}ms: {Command}", _timeoutMs, command);
                KillProcess(process);
                return $"Command timed out after {_timeoutMs}ms.";
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
