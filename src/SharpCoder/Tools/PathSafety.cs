using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpCoder.Tools;

/// <summary>
/// Shared helper for confining paths to the agent work directory.
/// Lexical path containment only. Does NOT resolve symlinks or reparse points
/// (separate concern per ideas-path-containment-hardening).
/// </summary>
internal static class PathSafety
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="workingDirectory"/> and returns the
    /// canonicalized full path when it is contained within the working directory root (boundary-safe:
    /// the path must equal the root or sit beneath a directory separator boundary), or <c>null</c>
    /// when the path escapes the root.
    /// </summary>
    internal static string? ResolveWithinRoot(string workingDirectory, string path)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        var canonicalRoot = Path.GetFullPath(workingDirectory);

        string boundary;
        string rootExact;
        if (EndsWithSeparator(canonicalRoot))
        {
            // The trailing separator already IS the boundary — appending another would produce a
            // doubled separator and falsely reject everything.
            boundary = canonicalRoot;

            // A filesystem root ("/" or "C:\") must never be trimmed: that would turn "/" into ""
            // or "C:\" into "C:", making the prefix match boundary-unsafe.
            rootExact = IsFilesystemRoot(canonicalRoot)
                ? canonicalRoot
                : canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else
        {
            boundary = canonicalRoot + Path.DirectorySeparatorChar;
            rootExact = canonicalRoot;
        }

        if (string.Equals(fullPath, rootExact, PathComparison))
        {
            return fullPath;
        }

        return fullPath.StartsWith(boundary, PathComparison) ? fullPath : null;
    }

    private static bool EndsWithSeparator(string value) =>
        value.Length > 0 &&
        (value[value.Length - 1] == Path.DirectorySeparatorChar ||
         value[value.Length - 1] == Path.AltDirectorySeparatorChar);

    private static bool IsFilesystemRoot(string canonicalRoot) =>
        string.Equals(Path.GetPathRoot(canonicalRoot), canonicalRoot, PathComparison);
}
