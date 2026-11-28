using SharpCoder;

namespace SharpCoder.Cli;

/// <summary>
/// Writes session JSON atomically. <see cref="AgentSession.SaveAsync(string, CancellationToken)"/>
/// writes directly to the destination, so an interruption (Ctrl+C, crash, power loss) can leave a
/// truncated file that cannot be loaded again. Saving to a sibling temp file and replacing the
/// destination in one move guarantees readers see either the old session or the complete new one.
/// </summary>
internal static class SessionFileWriter
{
    /// <summary>
    /// Saves <paramref name="session"/> to <paramref name="path"/> via a temp file + atomic replace.
    /// The temp file lives next to the destination so the replace stays on the same volume.
    /// </summary>
    public static async Task SaveAtomicAsync(AgentSession session, string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // A unique suffix makes concurrent saves (or a leftover temp from a previous crash) harmless.
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await session.SaveAsync(tempPath, ct).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            // Only reached when the move failed or was never attempted; a successful move removes it.
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
