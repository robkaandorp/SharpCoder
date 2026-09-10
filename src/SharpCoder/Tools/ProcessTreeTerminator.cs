using System;
using System.Diagnostics;
using System.Reflection;

namespace SharpCoder.Tools;

/// <summary>
/// Narrow internal seam over the runtime's process-tree termination capability.
/// <para>
/// This exists so <see cref="BashTools"/> can request "kill the whole process tree" semantics
/// without compiling a reference to <c>Process.Kill(bool)</c> — an overload that does not exist
/// in the <c>netstandard2.1</c> reference assemblies this library targets. Implementations are
/// also substitutable in tests so an unsupported or throwing capability can be simulated.
/// </para>
/// </summary>
internal interface IProcessTreeTerminator
{
    /// <summary>
    /// True when the running runtime exposes a usable <c>Process.Kill(entireProcessTree)</c>
    /// overload. When false, callers must fall back to a best-effort root-only kill and report
    /// the cleanup as degraded.
    /// </summary>
    bool IsTreeTerminationSupported { get; }

    /// <summary>
    /// Requests termination of <paramref name="process"/> and its descendants.
    /// Throws when the capability is unavailable or the underlying invocation fails; callers are
    /// expected to catch, fall back to a root-only kill and report degraded cleanup.
    /// </summary>
    void TerminateTree(Process process);
}

/// <summary>
/// Default <see cref="IProcessTreeTerminator"/>: a reflection-based runtime capability adapter for
/// the public <c>Process.Kill(bool)</c> overload family.
/// <para>
/// The overload is resolved once per process. Only the exact <c>Kill(bool)</c> or
/// <c>Kill(bool, bool)</c> signatures are accepted; the first argument (entire-process-tree) is
/// passed as <see langword="true"/> and any additional boolean parameter is passed as
/// <see langword="false"/> (the conservative, non-throwing choice for the known
/// <c>throwIfNotStarted</c>-style parameter).
/// </para>
/// <para>
/// Limitation: this only terminates descendants the OS still attributes to the live root process.
/// If the root has already exited, or descendants escaped ancestry / were reparented, they cannot
/// be reliably located or terminated here. No process-name killing, no stale-PID guessing, and no
/// scanning of unrelated processes is performed.
/// </para>
/// </summary>
internal sealed class ProcessTreeTerminator : IProcessTreeTerminator
{
    /// <summary>Shared default instance (the resolution result is cached statically).</summary>
    internal static readonly ProcessTreeTerminator Default = new();

    private static readonly MethodInfo? KillWithTreeMethod = ResolveKillWithTreeMethod();

    /// <inheritdoc />
    public bool IsTreeTerminationSupported => KillWithTreeMethod is not null;

    /// <inheritdoc />
    public void TerminateTree(Process process)
    {
        if (process is null) throw new ArgumentNullException(nameof(process));

        var method = KillWithTreeMethod
            ?? throw new PlatformNotSupportedException(
                "This runtime does not expose a Process.Kill(entireProcessTree) overload.");

        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        for (var i = 0; i < args.Length; i++)
        {
            // args[0] is entireProcessTree; any further boolean flag (e.g. throwIfNotStarted)
            // is passed as false so a race with normal exit does not raise.
            args[i] = i == 0;
        }

        try
        {
            method.Invoke(process, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real failure so the caller can report why tree cleanup was not established.
            throw ex.InnerException;
        }
    }

    private static MethodInfo? ResolveKillWithTreeMethod()
    {
        try
        {
            var processType = typeof(Process);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            var single = processType.GetMethod("Kill", flags, binder: null, types: new[] { typeof(bool) }, modifiers: null);
            if (single is not null) return single;

            return processType.GetMethod("Kill", flags, binder: null, types: new[] { typeof(bool), typeof(bool) }, modifiers: null);
        }
        catch
        {
            // Capability probing must never break command execution; treat as unsupported.
            return null;
        }
    }
}
