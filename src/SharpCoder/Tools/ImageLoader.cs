using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder.Tools;

/// <summary>
/// Loads image/PDF attachments from disk, enforcing work-directory containment,
/// supported media types, and count/size limits.
/// </summary>
internal static class ImageLoader
{
    internal const int MaxImageCount = 8;
    internal const long MaxTotalBytes = 20_971_520; // 20 MiB

    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Test seam. When non-null, it is used instead of the real filesystem to probe a file's
    /// length and to open its content stream. Keyed by the resolved (canonical) path.
    /// </summary>
    internal static Func<string, (long length, Func<Stream> open)>? FileProbe;

    /// <summary>Outcome of a load attempt; either a set of attachments or an error message, never both.</summary>
    internal sealed class ImageLoadResult
    {
        private ImageLoadResult(bool success, IReadOnlyList<ImageAttachment> attachments, string? error)
        {
            Success = success;
            Attachments = attachments;
            Error = error;
        }

        /// <summary>True when all requested attachments loaded successfully.</summary>
        public bool Success { get; }

        /// <summary>The loaded attachments; empty when <see cref="Success"/> is false.</summary>
        public IReadOnlyList<ImageAttachment> Attachments { get; }

        /// <summary>The failure reason; null when <see cref="Success"/> is true.</summary>
        public string? Error { get; }

        public static ImageLoadResult CreateSuccess(IReadOnlyList<ImageAttachment> attachments)
        {
            if (attachments is null) throw new ArgumentNullException(nameof(attachments));
            return new ImageLoadResult(true, attachments, null);
        }

        public static ImageLoadResult CreateFailure(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                throw new ArgumentException("A failure result requires an error message.", nameof(error));
            return new ImageLoadResult(false, Array.Empty<ImageAttachment>(), error);
        }
    }

    /// <summary>
    /// Loads the given paths, confining every path to <paramref name="workingDirectory"/>.
    /// This is the original single-root path: it performs exactly one containment check per path
    /// and never consults an additional root.
    /// </summary>
    internal static Task<ImageLoadResult> LoadAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        if (workingDirectory is null)
            throw new ArgumentNullException(nameof(workingDirectory));
        if (workingDirectory.Trim().Length == 0)
            throw new ArgumentException("Working directory must not be empty or whitespace.", nameof(workingDirectory));
        if (paths is null)
            throw new ArgumentNullException(nameof(paths));

        for (var i = 0; i < paths.Count; i++)
        {
            if (paths[i] is null)
                throw new ArgumentException($"Path element at index {i} is null.", nameof(paths));
        }

        return LoadCoreAsync(workingDirectory, paths, ct);
    }

    /// <summary>
    /// Loads the given paths, confining every path to <paramref name="workingDirectory"/> or, when
    /// configured, to <paramref name="additionalRoot"/>.
    /// <para>
    /// When <paramref name="additionalRoot"/> is null this call is indistinguishable from
    /// <see cref="LoadAsync(string, IReadOnlyList{string}, CancellationToken)"/>: the original
    /// single-root loader runs, with no extra per-path work.
    /// </para>
    /// <para>
    /// When an additional root IS configured, absolute paths are accepted when they are contained
    /// within EITHER root, and relative paths are resolved against the primary root first — only
    /// when the resolved file does not exist there is the additional root tried, so the primary
    /// root always wins when the same relative path exists under both. Containment is enforced
    /// separately for each root, so <c>../</c> escapes from either root are still rejected.
    /// </para>
    /// </summary>
    internal static Task<ImageLoadResult> LoadAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        string? additionalRoot,
        CancellationToken ct)
    {
        // Not configured → the untouched original single-root loader, argument validation included.
        if (additionalRoot is null)
        {
            return LoadAsync(workingDirectory, paths, ct);
        }

        if (workingDirectory is null)
            throw new ArgumentNullException(nameof(workingDirectory));
        if (workingDirectory.Trim().Length == 0)
            throw new ArgumentException("Working directory must not be empty or whitespace.", nameof(workingDirectory));
        if (paths is null)
            throw new ArgumentNullException(nameof(paths));
        if (additionalRoot.Trim().Length == 0)
            throw new ArgumentException("Additional root must not be empty or whitespace.", nameof(additionalRoot));

        for (var i = 0; i < paths.Count; i++)
        {
            if (paths[i] is null)
                throw new ArgumentException($"Path element at index {i} is null.", nameof(paths));
        }

        return LoadDualRootCoreAsync(workingDirectory, additionalRoot, paths, ct);
    }

    /// <summary>
    /// The ORIGINAL single-root loader, byte-for-byte unchanged by the additional-root feature:
    /// one <see cref="PathSafety.ResolveWithinRoot"/> call per path, no extra branching, no
    /// knowledge of any second root.
    /// </summary>
    private static async Task<ImageLoadResult> LoadCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var attachments = new List<ImageAttachment>();
        long cumulativeBytes = 0;

        foreach (var path in paths)
        {
            if (attachments.Count >= MaxImageCount)
            {
                return ImageLoadResult.CreateFailure(
                    $"Too many images: at most {MaxImageCount} images may be attached.");
            }

            ct.ThrowIfCancellationRequested();

            if (path.Trim().Length == 0)
            {
                return ImageLoadResult.CreateFailure("Image path must not be blank.");
            }

            // Path.GetFullPath (inside ResolveWithinRoot) throws on malformed path data — e.g. NUL
            // characters, or illegal characters such as <>|*? on Windows. That is a data error, not
            // a caller error, so it becomes an error result rather than escaping.
            string? resolved;
            try
            {
                resolved = PathSafety.ResolveWithinRoot(workingDirectory, path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ImageLoadResult.CreateFailure($"Path '{path}' could not be resolved: {ex.Message}");
            }

            if (resolved is null)
            {
                return ImageLoadResult.CreateFailure($"Image path '{path}' escapes the work directory.");
            }

            string? mediaType;
            try
            {
                mediaType = InferMediaType(resolved);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ImageLoadResult.CreateFailure($"Path '{path}' could not be resolved: {ex.Message}");
            }

            if (mediaType is null)
            {
                return ImageLoadResult.CreateFailure(
                    $"Unsupported image type for '{path}'. Supported extensions: .png, .jpg, .jpeg, .gif, .webp, .pdf.");
            }

            var probe = FileProbe;
            long length;
            Func<Stream> open;
            try
            {
                if (probe is not null)
                {
                    (length, open) = probe(resolved);
                }
                else
                {
                    length = new FileInfo(resolved).Length;
                    var capturedPath = resolved;
                    open = () => File.OpenRead(capturedPath);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ImageLoadResult.CreateFailure($"Could not read image '{path}': {ex.Message}");
            }

            if (length > MaxTotalBytes)
            {
                return ImageLoadResult.CreateFailure(
                    $"Image '{path}' is too large: images may total at most {MaxTotalBytes} bytes.");
            }

            var remainingAllowance = MaxTotalBytes - cumulativeBytes;
            if (length > remainingAllowance)
            {
                return ImageLoadResult.CreateFailure(
                    $"Images exceed the total size limit of {MaxTotalBytes} bytes.");
            }

            ct.ThrowIfCancellationRequested();

            byte[] data;
            try
            {
                var bounded = await ReadBoundedAsync(open, remainingAllowance, ct).ConfigureAwait(false);
                if (bounded is null)
                {
                    return ImageLoadResult.CreateFailure(
                        $"Images exceed the total size limit of {MaxTotalBytes} bytes.");
                }

                data = bounded;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ImageLoadResult.CreateFailure($"Could not read image '{path}': {ex.Message}");
            }

            attachments.Add(new ImageAttachment
            {
                Data = data,
                MediaType = mediaType,
                Name = path,
            });

            cumulativeBytes += data.Length;
        }

        return ImageLoadResult.CreateSuccess(attachments);
    }

    /// <summary>
    /// Two-root variant of <see cref="LoadCoreAsync"/>, reached ONLY when an additional root is
    /// actually configured, so the single-root path above stays untouched. Path resolution
    /// consults both roots via <see cref="ResolveAcrossRoots"/>, and media-type inference, the
    /// size limits and the bounded read are performed by <see cref="AppendResolvedAsync"/> — a
    /// SEPARATE implementation from the one inlined in <see cref="LoadCoreAsync"/>, which keeps
    /// its own copy so the original single-root path remains byte-for-byte unchanged. Both
    /// implementations enforce the same <see cref="MaxImageCount"/> and <see cref="MaxTotalBytes"/>
    /// limits and are covered independently by tests.
    /// </summary>
    private static async Task<ImageLoadResult> LoadDualRootCoreAsync(
        string workingDirectory,
        string additionalRoot,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var attachments = new List<ImageAttachment>();
        long cumulativeBytes = 0;

        foreach (var path in paths)
        {
            if (attachments.Count >= MaxImageCount)
            {
                return ImageLoadResult.CreateFailure(
                    $"Too many images: at most {MaxImageCount} images may be attached.");
            }

            ct.ThrowIfCancellationRequested();

            if (path.Trim().Length == 0)
            {
                return ImageLoadResult.CreateFailure("Image path must not be blank.");
            }

            string? resolved;
            try
            {
                resolved = ResolveAcrossRoots(workingDirectory, additionalRoot, path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ImageLoadResult.CreateFailure($"Path '{path}' could not be resolved: {ex.Message}");
            }

            if (resolved is null)
            {
                return ImageLoadResult.CreateFailure($"Image path '{path}' escapes the work directory.");
            }

            var outcome = await AppendResolvedAsync(attachments, path, resolved, cumulativeBytes, ct)
                .ConfigureAwait(false);
            if (outcome.Failure is not null) return outcome.Failure;
            cumulativeBytes = outcome.CumulativeBytes;
        }

        return ImageLoadResult.CreateSuccess(attachments);
    }

    /// <summary>
    /// Media-type inference, size limits and the bounded read for one already-resolved path.
    /// Used ONLY by <see cref="LoadDualRootCoreAsync"/>; <see cref="LoadCoreAsync"/> retains its
    /// own inline copy of this logic so the original single-root path is unchanged. The two
    /// implementations must therefore be kept in agreement on <see cref="MaxTotalBytes"/> by hand,
    /// and each is covered by its own size-limit tests.
    /// </summary>
    private static async Task<(ImageLoadResult? Failure, long CumulativeBytes)> AppendResolvedAsync(
        List<ImageAttachment> attachments,
        string path,
        string resolved,
        long cumulativeBytes,
        CancellationToken ct)
    {
        string? mediaType;
        try
        {
            mediaType = InferMediaType(resolved);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (ImageLoadResult.CreateFailure($"Path '{path}' could not be resolved: {ex.Message}"), cumulativeBytes);
        }

        if (mediaType is null)
        {
            return (ImageLoadResult.CreateFailure(
                $"Unsupported image type for '{path}'. Supported extensions: .png, .jpg, .jpeg, .gif, .webp, .pdf."),
                cumulativeBytes);
        }

        var probe = FileProbe;
        long length;
        Func<Stream> open;
        try
        {
            if (probe is not null)
            {
                (length, open) = probe(resolved);
            }
            else
            {
                length = new FileInfo(resolved).Length;
                var capturedPath = resolved;
                open = () => File.OpenRead(capturedPath);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (ImageLoadResult.CreateFailure($"Could not read image '{path}': {ex.Message}"), cumulativeBytes);
        }

        if (length > MaxTotalBytes)
        {
            return (ImageLoadResult.CreateFailure(
                $"Image '{path}' is too large: images may total at most {MaxTotalBytes} bytes."), cumulativeBytes);
        }

        var remainingAllowance = MaxTotalBytes - cumulativeBytes;
        if (length > remainingAllowance)
        {
            return (ImageLoadResult.CreateFailure(
                $"Images exceed the total size limit of {MaxTotalBytes} bytes."), cumulativeBytes);
        }

        ct.ThrowIfCancellationRequested();

        byte[] data;
        try
        {
            var bounded = await ReadBoundedAsync(open, remainingAllowance, ct).ConfigureAwait(false);
            if (bounded is null)
            {
                return (ImageLoadResult.CreateFailure(
                    $"Images exceed the total size limit of {MaxTotalBytes} bytes."), cumulativeBytes);
            }

            data = bounded;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (ImageLoadResult.CreateFailure($"Could not read image '{path}': {ex.Message}"), cumulativeBytes);
        }

        attachments.Add(new ImageAttachment
        {
            Data = data,
            MediaType = mediaType,
            Name = path,
        });

        return (null, cumulativeBytes + data.Length);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against the primary root and the additional root.
    /// Returns the canonical full path, or <c>null</c> when the path is contained in neither root.
    /// Only reached when an additional root is actually configured.
    /// </summary>
    private static string? ResolveAcrossRoots(string workingDirectory, string additionalRoot, string path)
    {
        if (Path.IsPathRooted(path))
        {
            // An absolute path is accepted when contained in EITHER root.
            return PathSafety.ResolveWithinRoot(workingDirectory, path)
                ?? PathSafety.ResolveWithinRoot(additionalRoot, path);
        }

        // Relative path: the primary root wins whenever the file exists there.
        var primary = PathSafety.ResolveWithinRoot(workingDirectory, path);
        if (primary is not null && FileExists(primary))
        {
            return primary;
        }

        var additional = PathSafety.ResolveWithinRoot(additionalRoot, path);
        if (additional is not null && FileExists(additional))
        {
            return additional;
        }

        // Found under neither root: prefer the primary resolution so the caller reports a
        // read failure; when both roots reject the path (e.g. "../" escapes) this is null and
        // the caller reports the escape.
        return primary ?? additional;
    }

    /// <summary>
    /// Existence probe honouring the <see cref="FileProbe"/> test seam: a probe that throws for a
    /// path is treated as "does not exist".
    /// </summary>
    private static bool FileExists(string resolvedPath)
    {
        var probe = FileProbe;
        if (probe is null)
        {
            try
            {
                return File.Exists(resolvedPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return false;
            }
        }

        try
        {
            _ = probe(resolvedPath);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads at most <paramref name="allowance"/> + 1 bytes. Returns null when the stream yields
    /// more than <paramref name="allowance"/> bytes (the file grew between probe and read).
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Func<Stream> open, long allowance, CancellationToken ct)
    {
        var stream = open();
        if (stream is null)
        {
            throw new IOException("The image stream could not be opened.");
        }

        try
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[CopyBufferSize];
            long total = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Never request more than allowance + 1 bytes in total.
                var remaining = allowance + 1 - total;
                if (remaining <= 0) break;

                var toRead = remaining < buffer.Length ? (int)remaining : buffer.Length;
                var read = await stream.ReadAsync(buffer, 0, toRead, ct).ConfigureAwait(false);
                if (read <= 0) break;

                buffered.Write(buffer, 0, read);
                total += read;

                if (total > allowance)
                {
                    return null;
                }
            }

            return buffered.ToArray();
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static string? InferMediaType(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return null;

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
        if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)) return "application/pdf";
        return null;
    }
}
