using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpCoder;
using SharpCoder.Tools;
using Xunit;

namespace SharpCoder.Tests;

/// <summary>
/// xUnit collection that serializes all ImageLoader tests. The ImageLoader.FileProbe
/// static test seam is process-global, so tests that set it (or rely on it being null)
/// must not run in parallel with each other.
/// </summary>
[CollectionDefinition("ImageLoader")]
public class ImageLoaderCollection : ICollectionFixture<ImageLoaderCollectionFixture> { }

public class ImageLoaderCollectionFixture { }

// ===========================================================================
// 1. ImageAttachment — plain get/set POCO
// ===========================================================================
public class ImageAttachmentTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var attachment = new ImageAttachment();
        Assert.Same(Array.Empty<byte>(), attachment.Data);
        Assert.Equal(string.Empty, attachment.MediaType);
        Assert.Null(attachment.Name);
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var attachment = new ImageAttachment
        {
            Data = bytes,
            MediaType = "image/png",
            Name = "screenshot.png",
        };

        Assert.Same(bytes, attachment.Data);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal("screenshot.png", attachment.Name);
    }
}

// ===========================================================================
// 2. PathSafety — boundary-safe, platform-correct lexical containment
// ===========================================================================
public class PathSafetyTests
{
    [Fact]
    public void RootEscape_ReturnsNull()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "SharpCoderPS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var result = PathSafety.ResolveWithinRoot(workDir, "../../etc/passwd");
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }

    [Fact]
    public void CaseComparison_IsPlatformCorrect()
    {
        // Create a real temp working directory, then resolve a differently-cased
        // sibling path that differs only in case.
        var workDir = Path.Combine(Path.GetTempPath(), "TestDir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            // Build a sibling path with different casing on the last segment.
            var parent = Directory.GetParent(workDir)!.FullName;
            var dirName = Path.GetFileName(workDir);
            // Flip the case of the first letter for the sibling.
            var flipped = char.IsUpper(dirName[0])
                ? char.ToLower(dirName[0]) + dirName.Substring(1)
                : char.ToUpper(dirName[0]) + dirName.Substring(1);
            var siblingPath = Path.Combine(parent, flipped, "file.txt");

            var result = PathSafety.ResolveWithinRoot(workDir, siblingPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows is case-insensitive → the differently-cased path resolves
                // to the same directory, so it should be accepted.
                Assert.NotNull(result);
            }
            else
            {
                // Linux/macOS are case-sensitive → the differently-cased path is a
                // different directory and must be rejected.
                Assert.Null(result);
            }
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }

    [Fact]
    public void BoundarySafePrefixMatch_SiblingSharingPrefix_Rejected()
    {
        // Create two sibling directories under temp: one named SharpCoderTest_app
        // and one named SharpCoderTest_app-other. A path resolving into the
        // sibling must be rejected even though it string-startsWith the root.
        var tempRoot = Path.Combine(Path.GetTempPath(), "SharpCoderPrefixTest_" + Guid.NewGuid().ToString("N"));
        var workDir = Path.Combine(tempRoot, "SharpCoderTest_app");
        var siblingDir = Path.Combine(tempRoot, "SharpCoderTest_app-other");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(siblingDir);
        try
        {
            var siblingFile = Path.Combine(siblingDir, "file.txt");
            var result = PathSafety.ResolveWithinRoot(workDir, siblingFile);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void FilesystemRootWorkingDir_AcceptsValidDescendants()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use a drive root, e.g. C:\
            var driveRoot = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
            var descendant = Path.Combine(driveRoot, "Users", "file.txt");
            var result = PathSafety.ResolveWithinRoot(driveRoot, descendant);
            Assert.NotNull(result);
        }
        else
        {
            // Unix: use "/" as working directory.
            var result = PathSafety.ResolveWithinRoot("/", "/tmp/somefile.txt");
            Assert.NotNull(result);
        }
    }

    [Fact]
    public void TrailingSeparator_WorkingDir_NoDoubleSeparatorFalseReject()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "SharpCoderTrail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            // Add trailing separator manually.
            var withTrail = workDir + Path.DirectorySeparatorChar;
            var relativeFile = "subdir" + Path.DirectorySeparatorChar + "file.txt";
            var fullPath = Path.Combine(workDir, "subdir", "file.txt");

            var result = PathSafety.ResolveWithinRoot(withTrail, relativeFile);
            Assert.NotNull(result);
            Assert.Equal(fullPath, result);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }
}

// ===========================================================================
// 3. FileTools glob regression — sibling-prefix boundary safety
// ===========================================================================
public class FileToolsGlobRegressionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workDir;
    private readonly string _siblingDir;
    private readonly FileTools _tools;

    public FileToolsGlobRegressionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SharpCoderGlob_" + Guid.NewGuid().ToString("N"));
        _workDir = Path.Combine(_tempRoot, "work");
        _siblingDir = Path.Combine(_tempRoot, "work-other");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_siblingDir);

        // Write a file in the sibling directory.
        File.WriteAllText(Path.Combine(_siblingDir, "leak.txt"), "sensitive");

        _tools = new FileTools(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    [Fact]
    public void Glob_SiblingSharingPrefix_Rejected()
    {
        var result = _tools.glob("../work-other/*.txt");
        Assert.Contains("resolves outside the work directory", result);
    }
}

// ===========================================================================
// 4 & 5. ImageLoader — basic loading, inference, contract / error handling
// ===========================================================================
[Collection("ImageLoader")]
public class ImageLoaderBasicTests : IDisposable
{
    private readonly string _workDir;

    public ImageLoaderBasicTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "SharpCoderImg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true);
    }

    [Fact]
    public async Task ValidSmallPng_Loads()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var filePath = Path.Combine(_workDir, "tiny.png");
        await File.WriteAllBytesAsync(filePath, pngBytes, TestContext.Current.CancellationToken);

        var result = await ImageLoader.LoadAsync(_workDir, new[] { "tiny.png" }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        Assert.Single(result.Attachments);
        var att = result.Attachments[0];
        Assert.Equal("image/png", att.MediaType);
        Assert.Equal(pngBytes, att.Data);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ValidSmallPdf_Loads()
    {
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }; // %PDF-
        var filePath = Path.Combine(_workDir, "doc.pdf");
        await File.WriteAllBytesAsync(filePath, pdfBytes, TestContext.Current.CancellationToken);

        var result = await ImageLoader.LoadAsync(_workDir, new[] { "doc.pdf" }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        Assert.Single(result.Attachments);
        Assert.Equal("application/pdf", result.Attachments[0].MediaType);
        Assert.Equal(pdfBytes, result.Attachments[0].Data);
    }

    [Theory]
    [InlineData(".PNG", "image/png")]
    [InlineData(".Png", "image/png")]
    [InlineData(".JPG", "image/jpeg")]
    [InlineData(".Jpg", "image/jpeg")]
    [InlineData(".JPEG", "image/jpeg")]
    [InlineData(".GIF", "image/gif")]
    [InlineData(".WEBP", "image/webp")]
    [InlineData(".PDF", "application/pdf")]
    public async Task InferenceTable_CaseInsensitive(string extension, string expectedMediaType)
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var fileName = "test" + extension;
        var filePath = Path.Combine(_workDir, fileName);
        await File.WriteAllBytesAsync(filePath, data, TestContext.Current.CancellationToken);

        var result = await ImageLoader.LoadAsync(_workDir, new[] { fileName }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? $"Expected success for {extension}");
        Assert.Equal(expectedMediaType, result.Attachments[0].MediaType);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".bmp")]
    public async Task UnknownExtension_ReturnsError(string extension)
    {
        var data = new byte[] { 1, 2, 3 };
        var fileName = "unknown" + extension;
        var filePath = Path.Combine(_workDir, fileName);
        await File.WriteAllBytesAsync(filePath, data, TestContext.Current.CancellationToken);

        var result = await ImageLoader.LoadAsync(_workDir, new[] { fileName }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Attachments);
        Assert.Contains(extension, result.Error!);
    }
}

[Collection("ImageLoader")]
public class ImageLoaderContractTests : IDisposable
{
    private readonly string _workDir;

    public ImageLoaderContractTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "SharpCoderImgC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true);
    }

    [Fact]
    public async Task LoadFailure_ReturnsSuccessFalse_WithError_AndEmptyAttachments()
    {
        // Use an unknown extension to trigger a data error.
        await File.WriteAllBytesAsync(
            Path.Combine(_workDir, "bad.txt"),
            new byte[] { 1 },
            TestContext.Current.CancellationToken);

        var result = await ImageLoader.LoadAsync(_workDir, new[] { "bad.txt" }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task NullPaths_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ImageLoader.LoadAsync(_workDir, null!, CancellationToken.None));
    }

    [Fact]
    public async Task NullElementInPaths_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ImageLoader.LoadAsync(_workDir, new string[] { null! }, CancellationToken.None));
    }

    [Fact]
    public async Task NullWorkingDirectory_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ImageLoader.LoadAsync(null!, new[] { "file.png" }, CancellationToken.None));
    }

    [Fact]
    public async Task WhitespaceWorkingDirectory_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ImageLoader.LoadAsync("   ", new[] { "file.png" }, CancellationToken.None));
    }

    [Fact]
    public async Task BlankPathElement_ReturnsErrorResult_DoesNotThrow()
    {
        var result = await ImageLoader.LoadAsync(_workDir, new[] { "   " }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task MalformedPath_WithIllegalCharacters_ReturnsErrorResult_DoesNotThrow()
    {
        // Path.GetFullPath (inside PathSafety.ResolveWithinRoot) throws ArgumentException
        // for paths with illegal characters such as an embedded NUL (\0), which is
        // universally illegal on all platforms. The loader must catch this and return
        // an error result rather than letting the exception escape.
        var result = await ImageLoader.LoadAsync(_workDir, new[] { "file\0bad.png" }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Attachments);
    }
}

// ===========================================================================
// 6. ImageLoader — size limits via FileProbe seam
// ===========================================================================
[Collection("ImageLoader")]
public class ImageLoaderSizeLimitTests : IDisposable
{
    private readonly string _workDir;

    public ImageLoaderSizeLimitTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "SharpCoderImgSize_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        // Always reset the test seam.
        ImageLoader.FileProbe = null;
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true);
    }

    private static (long length, Func<Stream> open) MakeProbeEntry(long reportedLength, byte[]? actualData = null)
    {
        var data = actualData ?? new byte[reportedLength];
        return (reportedLength, () => new MemoryStream(data));
    }

    [Fact]
    public async Task NineImages_Rejected()
    {
        // Provide 9 paths. FileProbe returns small files for all.
        var paths = new string[9];
        for (var i = 0; i < 9; i++)
        {
            paths[i] = $"img{i}.png";
        }

        ImageLoader.FileProbe = _ => MakeProbeEntry(10, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        try
        {
            var result = await ImageLoader.LoadAsync(_workDir, paths, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            // The error should mention the count limit.
            Assert.Contains("8", result.Error!);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task SingleFileOverLimit_RejectedWithNoRead()
    {
        var openWasCalled = false;

        ImageLoader.FileProbe = _ =>
        {
            return (20_971_521, () =>
            {
                openWasCalled = true;
                return new MemoryStream(new byte[20_971_521]);
            });
        };

        try
        {
            var result = await ImageLoader.LoadAsync(_workDir, new[] { "big.png" }, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            // The open function must never have been called — the pre-read fast reject.
            Assert.False(openWasCalled, "open() should NOT have been called for a file exceeding the limit.");
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task CumulativeOverLimit_Rejected()
    {
        // Two files, each 15 MiB (15,728,640 bytes). First loads, second pushes
        // cumulative over 20 MiB.
        var fifteenMiB = 15_728_640;

        ImageLoader.FileProbe = _ => MakeProbeEntry(fifteenMiB, new byte[fifteenMiB]);

        try
        {
            var result = await ImageLoader.LoadAsync(
                _workDir,
                new[] { "first.png", "second.png" },
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            // Error should mention size limit.
            Assert.Contains("size", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task ExactlyLimit_SingleFile_Allowed()
    {
        var exactlyLimit = 20_971_520; // exactly 20 MiB
        var data = new byte[exactlyLimit];

        ImageLoader.FileProbe = _ => (exactlyLimit, () => new MemoryStream(data));

        try
        {
            var result = await ImageLoader.LoadAsync(_workDir, new[] { "exact.png" }, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error ?? "Expected success");
            Assert.Single(result.Attachments);
            Assert.Equal(exactlyLimit, result.Attachments[0].Data.Length);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task CheckVsReadMismatch_BoundedReadCatchesIt()
    {
        // FileProbe reports length = 1_000 (small), but the stream actually yields
        // MUCH more data than the remaining allowance. The bounded read must read at
        // most remainingAllowance + 1 bytes and then reject — it must NOT read the
        // entire stream. We instrument the stream with CountingStream to prove the
        // read is actually capped.
        var ct = TestContext.Current.CancellationToken;
        var reportedLength = 1_000L;
        var remainingAllowance = ImageLoader.MaxTotalBytes; // cumulative is 0 → full allowance
        // Provide significantly more bytes than allowance + 1 so the test would fail
        // if the implementation read everything.
        var streamSize = (int)(remainingAllowance + 10_000);
        var countingStream = new CountingStream(new byte[streamSize]);

        ImageLoader.FileProbe = _ => (reportedLength, () => countingStream);

        try
        {
            var result = await ImageLoader.LoadAsync(_workDir, new[] { "mismatch.png" }, ct);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            // The error should indicate the file is larger than expected / size limit.
            Assert.Contains("size", result.Error!, StringComparison.OrdinalIgnoreCase);

            // CRITICAL: verify the bounded read actually capped the bytes read.
            // The implementation must read at most remainingAllowance + 1 bytes, NOT
            // the entire stream (which has streamSize = remainingAllowance + 10_000 bytes).
            Assert.True(
                countingStream.TotalBytesRead <= remainingAllowance + 1,
                $"Expected at most {remainingAllowance + 1} bytes read, but {countingStream.TotalBytesRead} were read from the stream.");
            // And verify the stream actually had more data than the cap, proving the
            // test is non-vacuous.
            Assert.True(streamSize > remainingAllowance + 1,
                "Test setup error: stream must contain more bytes than the cap for the test to be meaningful.");
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    /// <summary>
    /// A MemoryStream subclass that counts the total bytes read, used to prove the
    /// bounded read in ImageLoader.ReadBoundedAsync reads at most allowance + 1 bytes.
    /// </summary>
    private sealed class CountingStream : MemoryStream
    {
        public int TotalBytesRead { get; private set; }

        public CountingStream(byte[] buffer) : base(buffer) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = base.Read(buffer, offset, count);
            TotalBytesRead += read;
            return Task.FromResult(read);
        }
    }
}

// ===========================================================================
// 7. ImageLoader — cancellation
// ===========================================================================
[Collection("ImageLoader")]
public class ImageLoaderCancellationTests : IDisposable
{
    private readonly string _workDir;

    public ImageLoaderCancellationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "SharpCoderImgCancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        ImageLoader.FileProbe = null;
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Set up FileProbe so that if the code somehow gets past the initial
        // ct.ThrowIfCancellationRequested(), we still have a probe. But the
        // cancellation check at the start of the loop should catch it first.
        ImageLoader.FileProbe = _ => (100, () => new MemoryStream(new byte[100]));

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ImageLoader.LoadAsync(_workDir, new[] { "cancel.png" }, cts.Token));
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }
}