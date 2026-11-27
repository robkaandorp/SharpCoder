namespace SharpCoder;

/// <summary>
/// Which shell <c>execute_bash_command</c> launches.
/// The default, <see cref="Auto"/>, prefers PowerShell 7+ (pwsh) on Windows, then Windows
/// PowerShell, then cmd.exe; on Linux/macOS it prefers bash on PATH and falls back to /bin/sh.
/// </summary>
public enum ShellPreference
{
    /// <summary>
    /// Auto-detect: on Windows prefer <c>pwsh</c> (PowerShell 7+), then Windows PowerShell
    /// (powershell.exe), then cmd.exe. On other platforms prefer bash on PATH, falling back to /bin/sh.
    /// </summary>
    Auto = 0,

    /// <summary>PowerShell 7+ (<c>pwsh</c>). Windows only; must be installed and on PATH.</summary>
    Pwsh = 1,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>). Windows only.</summary>
    PowerShell = 2,

    /// <summary>Windows cmd.exe. Windows only.</summary>
    Cmd = 3,

    /// <summary>bash (from PATH on Windows — Git for Windows or WSL — or /bin/bash on Unix). Not Windows-only, unlike the other explicit choices.</summary>
    Bash = 4,
}
