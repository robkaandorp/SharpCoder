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
// 7. ImageLoader — additional images root (two-root resolution)
// ===========================================================================
[Collection("ImageLoader")]
public class ImageLoaderAdditionalRootTests : IDisposable
{
    private static readonly byte[] PrimaryBytes = { 0x89, 0x50, 0x4E, 0x47, 0x01 };
    private static readonly byte[] AdditionalBytes = { 0x89, 0x50, 0x4E, 0x47, 0x02 };
    private static readonly byte[] OutsideBytes = { 0x89, 0x50, 0x4E, 0x47, 0x03 };

    private readonly string _tempRoot;
    private readonly string _primaryRoot;
    private readonly string _additionalRoot;
    private readonly string _outsideRoot;

    public ImageLoaderAdditionalRootTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SharpCoderImgAdd_" + Guid.NewGuid().ToString("N"));
        _primaryRoot = Path.Combine(_tempRoot, "primary");
        _additionalRoot = Path.Combine(_tempRoot, "additional");
        _outsideRoot = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(_primaryRoot);
        Directory.CreateDirectory(_additionalRoot);
        Directory.CreateDirectory(_outsideRoot);
    }

    public void Dispose()
    {
        ImageLoader.FileProbe = null;
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    private static string Write(string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task Absolute_Under_AdditionalRoot_Loads()
    {
        var attachmentPath = Write(_additionalRoot, "attachment.png", AdditionalBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        Assert.Single(result.Attachments);
        Assert.Equal("image/png", result.Attachments[0].MediaType);
        Assert.Equal(AdditionalBytes, result.Attachments[0].Data);
    }

    [Fact]
    public async Task Absolute_Under_AdditionalRoot_Rejected_When_Not_Configured()
    {
        // Removal proof for the additional-root plumbing: the SAME absolute path that loads
        // with the additional root configured must be rejected without it.
        var attachmentPath = Write(_additionalRoot, "attachment.png", AdditionalBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { attachmentPath }, null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("escapes the work directory", result.Error!);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task Absolute_Under_PrimaryRoot_Still_Loads_With_AdditionalRoot_Configured()
    {
        var attachmentPath = Write(_primaryRoot, "repo.png", PrimaryBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        Assert.Equal(PrimaryBytes, Assert.Single(result.Attachments).Data);
    }

    [Fact]
    public async Task Absolute_Under_Neither_Root_Escapes()
    {
        var attachmentPath = Write(_outsideRoot, "leak.png", OutsideBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("escapes the work directory", result.Error!);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task Relative_Existing_Only_Under_AdditionalRoot_Loads()
    {
        Write(_additionalRoot, "only-additional.png", AdditionalBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { "only-additional.png" }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        Assert.Equal(AdditionalBytes, Assert.Single(result.Attachments).Data);
    }

    [Fact]
    public async Task Relative_Existing_Under_Both_Roots_Resolves_To_Primary()
    {
        Write(_primaryRoot, "shared.png", PrimaryBytes);
        Write(_additionalRoot, "shared.png", AdditionalBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { "shared.png" }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error ?? "Expected success");
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal(PrimaryBytes, attachment.Data);
        Assert.NotEqual(AdditionalBytes, attachment.Data);
    }

    [Fact]
    public async Task Relative_Existing_Under_Neither_Root_Fails()
    {
        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { "missing.png" }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task Relative_DotDot_Escape_Rejected_From_Both_Roots()
    {
        Write(_outsideRoot, "leak.png", OutsideBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot,
            new[] { Path.Combine("..", "outside", "leak.png") },
            _additionalRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("escapes the work directory", result.Error!);
        Assert.Empty(result.Attachments);
    }

    [Fact]
    public async Task AdditionalRoot_Sibling_Sharing_Prefix_Rejected()
    {
        // Boundary safety must hold for the additional root too.
        var sibling = _additionalRoot + "-other";
        Directory.CreateDirectory(sibling);
        var leakPath = Write(sibling, "leak.png", OutsideBytes);

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { leakPath }, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("escapes the work directory", result.Error!);
    }

    [Fact]
    public async Task NoAdditionalRoot_Behaviour_Unchanged()
    {
        Write(_primaryRoot, "repo.png", PrimaryBytes);
        var outsidePath = Write(_outsideRoot, "leak.png", OutsideBytes);

        var ok = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { "repo.png" }, null, TestContext.Current.CancellationToken);
        Assert.True(ok.Success, ok.Error ?? "Expected success");
        Assert.Equal(PrimaryBytes, Assert.Single(ok.Attachments).Data);

        var escape = await ImageLoader.LoadAsync(
            _primaryRoot, new[] { outsidePath }, null, TestContext.Current.CancellationToken);
        Assert.False(escape.Success);
        Assert.Contains("escapes the work directory", escape.Error!);
    }

    [Fact]
    public async Task Relative_Precedence_Uses_FileProbe_Seam_For_Existence()
    {
        // The seam decides which root "has" the file: the primary-root candidate throws
        // (treated as missing), the additional-root candidate resolves.
        var primaryCandidate = Path.Combine(_primaryRoot, "seam.png");
        var additionalCandidate = Path.Combine(_additionalRoot, "seam.png");
        var probedPaths = new System.Collections.Generic.List<string>();

        ImageLoader.FileProbe = path =>
        {
            probedPaths.Add(path);
            if (string.Equals(path, additionalCandidate, StringComparison.Ordinal))
                return (AdditionalBytes.Length, () => new MemoryStream(AdditionalBytes));
            throw new FileNotFoundException("not here", path);
        };

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { "seam.png" }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error ?? "Expected success");
            Assert.Equal(AdditionalBytes, Assert.Single(result.Attachments).Data);
            // The primary root was consulted FIRST and only then the additional root.
            Assert.Contains(primaryCandidate, probedPaths);
            Assert.Equal(primaryCandidate, probedPaths[0]);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task AdditionalRoot_Whitespace_Throws_ArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ImageLoader.LoadAsync(_primaryRoot, new[] { "a.png" }, "   ", CancellationToken.None));
    }

    [Fact]
    public async Task CountLimit_Unchanged_With_AdditionalRoot()
    {
        for (var i = 0; i < 9; i++)
        {
            Write(_additionalRoot, $"img{i}.png", AdditionalBytes);
        }

        var paths = new string[9];
        for (var i = 0; i < 9; i++) paths[i] = $"img{i}.png";

        var result = await ImageLoader.LoadAsync(
            _primaryRoot, paths, _additionalRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("8", result.Error!);
        Assert.Empty(result.Attachments);
    }

    // -----------------------------------------------------------------------
    // Size limits on the CONFIGURED (two-root) path.
    //
    // The two-root loader has its own size-limit implementation in
    // ImageLoader.AppendResolvedAsync, separate from the untouched single-root
    // LoadCoreAsync. These tests drive images through the ADDITIONAL root so the
    // configured path's own per-file and cumulative guards are exercised: if either
    // guard is removed from AppendResolvedAsync, every one of these tests fails
    // because the over-limit load would succeed.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SingleFileOverLimit_UnderAdditionalRoot_Rejected_WithNoRead()
    {
        // Absolute path under the ADDITIONAL root: it is contained in neither the primary
        // root nor anywhere else, so only the two-root resolver can accept it.
        var attachmentPath = Path.Combine(_additionalRoot, "huge.png");
        var openWasCalled = false;

        ImageLoader.FileProbe = _ => (ImageLoader.MaxTotalBytes + 1, () =>
        {
            openWasCalled = true;
            return new MemoryStream(new byte[ImageLoader.MaxTotalBytes + 1]);
        });

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            // Not an escape: the path IS accepted by the additional root, it is the
            // per-file size guard on the configured path that rejects it.
            Assert.DoesNotContain("escapes the work directory", result.Error!);
            Assert.Contains("too large", result.Error!, StringComparison.OrdinalIgnoreCase);
            // Pre-read fast reject: the stream must never be opened.
            Assert.False(openWasCalled, "open() should NOT have been called for a file exceeding the limit.");
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task SingleFileOverLimit_RelativeUnderAdditionalRoot_Rejected()
    {
        // Relative path that exists ONLY under the additional root: the probe throws for the
        // primary-root candidate (treated as missing) and answers for the additional-root one,
        // so resolution lands on the additional root and the size guard must still fire.
        var additionalCandidate = Path.Combine(_additionalRoot, "huge.png");

        ImageLoader.FileProbe = path =>
        {
            if (!string.Equals(path, additionalCandidate, StringComparison.Ordinal))
                throw new FileNotFoundException("not here", path);
            return (ImageLoader.MaxTotalBytes + 1, () => new MemoryStream(new byte[ImageLoader.MaxTotalBytes + 1]));
        };

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { "huge.png" }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Empty(result.Attachments);
            Assert.DoesNotContain("escapes the work directory", result.Error!);
            Assert.Contains("too large", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task CumulativeOverLimit_UnderAdditionalRoot_Rejected()
    {
        // Two files under the additional root, each 15 MiB: the first is individually legal,
        // the second pushes the cumulative total past 20 MiB.
        const int fifteenMiB = 15_728_640;
        var first = Path.Combine(_additionalRoot, "first.png");
        var second = Path.Combine(_additionalRoot, "second.png");
        var openCount = 0;

        ImageLoader.FileProbe = _ => (fifteenMiB, () =>
        {
            openCount++;
            return new MemoryStream(new byte[fifteenMiB]);
        });

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { first, second }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Attachments);
            Assert.DoesNotContain("escapes the work directory", result.Error!);
            Assert.Contains("size", result.Error!, StringComparison.OrdinalIgnoreCase);
            // Removal proof: the cumulative allowance check rejects the SECOND file BEFORE its
            // stream is opened. Without that guard the bounded read would open it (openCount 2).
            Assert.Equal(1, openCount);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task CumulativeLimit_Counts_Across_Both_Roots()
    {
        // 15 MiB from the PRIMARY root plus 15 MiB from the ADDITIONAL root must still trip the
        // single cumulative allowance — the additional root does not get its own budget.
        const int fifteenMiB = 15_728_640;
        var fromPrimary = Path.Combine(_primaryRoot, "primary.png");
        var fromAdditional = Path.Combine(_additionalRoot, "additional.png");
        var openCount = 0;

        ImageLoader.FileProbe = _ => (fifteenMiB, () =>
        {
            openCount++;
            return new MemoryStream(new byte[fifteenMiB]);
        });

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot,
                new[] { fromPrimary, fromAdditional },
                _additionalRoot,
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Empty(result.Attachments);
            Assert.DoesNotContain("escapes the work directory", result.Error!);
            Assert.Contains("size", result.Error!, StringComparison.OrdinalIgnoreCase);
            // Removal proof: the second file is rejected before its stream is opened.
            Assert.Equal(1, openCount);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task ExactlyLimit_SingleFile_UnderAdditionalRoot_Allowed()
    {
        // Boundary: exactly 20 MiB is still allowed on the configured path, proving the guard
        // rejects only what is genuinely over the limit.
        var exactlyLimit = (int)ImageLoader.MaxTotalBytes;
        var data = new byte[exactlyLimit];
        var attachmentPath = Path.Combine(_additionalRoot, "exact.png");

        ImageLoader.FileProbe = _ => (exactlyLimit, () => new MemoryStream(data));

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error ?? "Expected success");
            Assert.Equal(exactlyLimit, Assert.Single(result.Attachments).Data.Length);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task CheckVsReadMismatch_UnderAdditionalRoot_BoundedReadCatchesIt()
    {
        // The probe under-reports the length but the stream yields far more than the allowance.
        // The configured path's bounded read must cap the read and reject.
        var attachmentPath = Path.Combine(_additionalRoot, "mismatch.png");
        var remainingAllowance = ImageLoader.MaxTotalBytes;
        var streamSize = (int)(remainingAllowance + 10_000);
        var countingStream = new CountingStream(new byte[streamSize]);

        ImageLoader.FileProbe = _ => (1_000L, () => countingStream);

        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { attachmentPath }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Empty(result.Attachments);
            Assert.Contains("size", result.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                countingStream.TotalBytesRead <= remainingAllowance + 1,
                $"Expected at most {remainingAllowance + 1} bytes read, but {countingStream.TotalBytesRead} were read.");
            Assert.True(streamSize > remainingAllowance + 1,
                "Test setup error: stream must contain more bytes than the cap for the test to be meaningful.");
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task DualRoot_PerFile_Error_Message_Matches_SingleRoot()
    {
        // Removal-proof for message drift: the dual-root per-file size guard must produce the
        // SAME error message format as the single-root per-file guard. If someone changed
        // AppendResolvedAsync's error text to diverge from LoadCoreAsync's, this test fails
        // because it asserts the exact message a single-root load produces for the same path.
        var overLimit = ImageLoader.MaxTotalBytes + 1;
        var path = "huge.png";

        // Capture the single-root error message.
        ImageLoader.FileProbe = _ => (overLimit, () => new MemoryStream(new byte[overLimit]));
        string singleRootError;
        try
        {
            var singleResult = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { path }, null, TestContext.Current.CancellationToken);
            Assert.False(singleResult.Success);
            singleRootError = singleResult.Error!;
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }

        // Capture the dual-root error message for the same path under the additional root.
        var additionalCandidate = Path.Combine(_additionalRoot, path);
        ImageLoader.FileProbe = p =>
        {
            if (!string.Equals(p, additionalCandidate, StringComparison.Ordinal))
                throw new FileNotFoundException("not here", p);
            return (overLimit, () => new MemoryStream(new byte[overLimit]));
        };
        try
        {
            var dualResult = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { path }, _additionalRoot, TestContext.Current.CancellationToken);
            Assert.False(dualResult.Success);
            Assert.Equal(singleRootError, dualResult.Error);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task DualRoot_Cumulative_Error_Message_Matches_SingleRoot()
    {
        // Removal-proof for message drift: the dual-root cumulative size guard must produce
        // the SAME error message as the single-root cumulative guard.
        const int fifteenMiB = 15_728_640;
        var first = "first.png";
        var second = "second.png";

        // Single-root: both files under the primary root.
        ImageLoader.FileProbe = _ => (fifteenMiB, () => new MemoryStream(new byte[fifteenMiB]));
        string singleRootError;
        try
        {
            var singleResult = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { first, second }, null, TestContext.Current.CancellationToken);
            Assert.False(singleResult.Success);
            singleRootError = singleResult.Error!;
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }

        // Dual-root: both files under the additional root.
        var firstCandidate = Path.Combine(_additionalRoot, first);
        var secondCandidate = Path.Combine(_additionalRoot, second);
        ImageLoader.FileProbe = p =>
        {
            if (string.Equals(p, firstCandidate, StringComparison.Ordinal) ||
                string.Equals(p, secondCandidate, StringComparison.Ordinal))
                return (fifteenMiB, () => new MemoryStream(new byte[fifteenMiB]));
            throw new FileNotFoundException("not here", p);
        };
        try
        {
            var dualResult = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { first, second }, _additionalRoot, TestContext.Current.CancellationToken);
            Assert.False(dualResult.Success);
            Assert.Equal(singleRootError, dualResult.Error);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    [Fact]
    public async Task ExactlyLimit_TwoFiles_CumulativeBoundary_UnderAdditionalRoot_Allowed()
    {
        // Two files that together total exactly MaxTotalBytes through the additional root.
        // Each is individually under the limit and together they are exactly at the cumulative
        // boundary — the guard must accept this, not reject it with an off-by-one error.
        var half = ImageLoader.MaxTotalBytes / 2;
        var first = Path.Combine(_additionalRoot, "half1.png");
        var second = Path.Combine(_additionalRoot, "half2.png");

        ImageLoader.FileProbe = _ => ((long)half, () => new MemoryStream(new byte[half]));
        try
        {
            var result = await ImageLoader.LoadAsync(
                _primaryRoot, new[] { first, second }, _additionalRoot, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error ?? "Expected success at exact cumulative boundary");
            Assert.Equal(2, result.Attachments.Count);
            Assert.Equal(half, result.Attachments[0].Data.Length);
            Assert.Equal(half, result.Attachments[1].Data.Length);
        }
        finally
        {
            ImageLoader.FileProbe = null;
        }
    }

    /// <summary>
    /// A MemoryStream subclass that counts the total bytes read, used to prove the bounded read
    /// on the two-root path reads at most allowance + 1 bytes.
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
// 8. ImageLoader — cancellation
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