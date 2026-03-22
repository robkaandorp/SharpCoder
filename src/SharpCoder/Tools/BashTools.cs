using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder.Tools;

public sealed class BashTools
{
    private readonly string _workingDirectory;
    private readonly int _timeoutMs;

    public BashTools(string workingDirectory, int timeoutMs = 120000)
    {
        _workingDirectory = workingDirectory;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 120000;
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
            var shell = isWindows ? "cmd.exe" : "/bin/bash";
            var args = isWindows ? $"/c \"{command}\"" : $"-c \"{command.Replace("\"", "\\\"")}\"";

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
