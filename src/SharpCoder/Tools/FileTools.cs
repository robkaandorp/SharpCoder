using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpCoder.Tools;

/// <summary>Provides file read, write, edit, and search operations rooted in a configured working directory.</summary>
public sealed class FileTools
{
    private readonly string _workingDirectory;
    private readonly ILogger _logger;

    /// <summary>Creates file tools rooted at the specified working directory.</summary>
    /// <param name="workingDirectory">Root directory that bounds file operations.</param>
    /// <param name="logger">Optional logger for file-operation diagnostics.</param>
    public FileTools(string workingDirectory, ILogger? logger = null)
    {
        _workingDirectory = workingDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    private string GetFullPath(string path)
    {
        return PathSafety.ResolveWithinRoot(_workingDirectory, path)
            ?? throw new UnauthorizedAccessException($"Path '{path}' escapes the work directory.");
    }

    /// <summary>Reads a section of a file under the working directory, prefixing each returned line with its 1-based line number.</summary>
    /// <param name="filePath">Relative or absolute file path, resolved within the working directory.</param>
    /// <param name="offset">1-based starting line; values below 1 are treated as 1.</param>
    /// <param name="limit">Maximum lines to return; values below 1 are treated as 1. Defaults to 2000.</param>
    /// <param name="ct">Token used to cancel the file read.</param>
    /// <returns>Numbered file lines, an end-of-file continuation hint, or an error message.</returns>
    [Description("Read a file from the local filesystem. Returns contents with each line prefixed by its line number. Use offset to read specific sections. The header line reports the file's line-ending style (LF or CRLF) and whether it contains a UTF-8 BOM.")]
    public async Task<string> read_file(
        [Description("The relative or absolute path to the file")] string filePath,
        [Description("The line number to start reading from (1-indexed)")] int offset = 1,
        [Description("The maximum number of lines to read")] int limit = 2000,
        CancellationToken ct = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File '{filePath}' does not exist.";
            }

            var (raw, hadBom) = ReadRaw(fullPath);
            var lineEnding = DetectLineEnding(raw);
            var lines = SplitLines(raw);
            if (lines.Length > 5000)
                _logger.LogDebug("Reading large file {Path} ({Lines} lines)", filePath, lines.Length);
            if (offset < 1) offset = 1;
            if (limit < 1) limit = 1;
            var startIndex = offset - 1;
            
            if (startIndex >= lines.Length)
            {
                return $"Error: Offset {offset} is beyond the end of the file (total lines: {lines.Length}).";
            }

            var count = Math.Max(0, Math.Min(limit, lines.Length - startIndex));
            var result = new StringBuilder();
            result.AppendLine($"[file: {lineEnding} line endings, UTF-8{(hadBom ? " with BOM" : string.Empty)}, {lines.Length} line(s)]");

            // Publish the exact metrics as structured data rather than leaving hosts to scrape the
            // numbered rendering below. charactersRead is the raw content length (excluding the
            // "N: " prefixes and the header), so it matches what the model actually received.
            long charactersRead = 0;
            for (var i = 0; i < count && startIndex + i < lines.Length; i++)
            {
                var lineIndex = startIndex + i;
                result.AppendLine($"{lineIndex + 1}: {lines[lineIndex]}");
                charactersRead += lines[lineIndex].Length;
            }

            ToolCallMetrics.Publish(new ToolCallFacts
            {
                StartLine = count > 0 ? startIndex + 1 : (int?)null,
                EndLine = count > 0 ? startIndex + count : (int?)null,
                CharactersRead = (int)Math.Min(charactersRead, int.MaxValue),
            });

            if (startIndex + count < lines.Length)
            {
                result.AppendLine($"... {lines.Length - (startIndex + count)} more lines unread. Use offset={startIndex + count + 1} to read more.");
            }

            return result.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    /// <summary>Creates or completely overwrites a file under the working directory, creating parent directories as needed.</summary>
    /// <param name="filePath">Relative or absolute file path, resolved within the working directory.</param>
    /// <param name="content">Complete replacement content. Line endings are normalised to the file's existing style when overwriting, or to LF for new files; a UTF-8 BOM is preserved when overwriting a file that had one.</param>
    /// <param name="ct">Token used to cancel the write.</param>
    /// <returns>A success message or an error message.</returns>
    [Description("Writes new content to a file, completely overwriting it. Do not use this to modify existing files - use edit_file instead. When overwriting an existing file, LF/CRLF line endings and a UTF-8 BOM are preserved from the existing file; new files are written with LF and no BOM.")]
    public async Task<string> write_file(
        [Description("The relative or absolute path to the file")] string filePath,
        [Description("The content to write")] string content,
        CancellationToken ct = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var (existingText, hadBom) = File.Exists(fullPath) ? ReadRaw(fullPath) : (null, false);
            var lineEnding = existingText is not null ? DetectLineEnding(existingText) : "LF";
            var preservedBom = existingText is not null && hadBom;
            var payload = NormalizeLineEndings(content, lineEnding);
            await WriteRawAsync(fullPath, payload, preservedBom, ct);
            _logger.LogDebug("Wrote {Chars} chars to {Path}", payload.Length, filePath);
            ToolCallMetrics.Publish(new ToolCallFacts
            {
                CharactersWritten = payload.Length,
                CharactersAdded = payload.Length,
            });
            return $"Successfully wrote {payload.Length} characters to '{filePath}' ({lineEnding} line endings{(preservedBom ? ", UTF-8 BOM preserved" : string.Empty)}).";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    /// <summary>Replaces one exact, unique text occurrence in a file under the working directory.</summary>
    /// <param name="filePath">Relative or absolute file path, resolved within the working directory.</param>
    /// <param name="oldString">Non-empty text that must occur exactly once in the file. Line endings are matched leniently: an LF oldString matches a CRLF file (and vice versa).</param>
    /// <param name="newString">Text inserted in place of the matched content. Line endings are normalised to the file's own line-ending style; a UTF-8 BOM, if any, is preserved.</param>
    /// <param name="ct">Token used to cancel file reads and writes.</param>
    /// <returns>A success message or an error message describing why the replacement was not made.</returns>
    [Description("Performs exact string replacements in files. The oldString must match the file content exactly, including whitespace and indentation, except that line endings (LF vs CRLF) are matched leniently and the file's original line-ending style and BOM are preserved. Only one occurrence is replaced per call.")]
    public async Task<string> edit_file(
        [Description("The relative or absolute path to the file")] string filePath,
        [Description("The exact text to replace. Must match the existing file exactly.")] string oldString,
        [Description("The new text to insert in its place.")] string newString,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(oldString))
            {
                return "Error: oldString cannot be empty.";
            }

            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File '{filePath}' does not exist.";
            }

            var (content, hadBom) = ReadRaw(fullPath);
            var lineEnding = DetectLineEnding(content);

            // Match in LF-normalized space so LF needles work against CRLF files (and vice
            // versa) and against files with mixed endings. The result is re-emitted using
            // the file's dominant line ending, preserving BOM if present.
            var normalizedContent = NormalizeLineEndings(content, "LF");
            var needle = NormalizeLineEndings(oldString, "LF");
            var replacement = NormalizeLineEndings(newString, "LF");

            var firstIndex = normalizedContent.IndexOf(needle, StringComparison.Ordinal);
            if (firstIndex == -1)
            {
                return $"Error: oldString not found in file (file uses {lineEnding} line endings). Check whitespace/indentation and line-ending style.";
            }

            var endIndex = firstIndex + needle.Length;
            var secondIndex = normalizedContent.IndexOf(needle, endIndex, StringComparison.Ordinal);
            if (secondIndex != -1)
            {
                return "Error: Found multiple matches for oldString. Provide more surrounding lines to make it unique.";
            }

            var updatedNormalized = normalizedContent.Substring(0, firstIndex)
                + replacement
                + normalizedContent.Substring(endIndex);
            var output = NormalizeLineEndings(updatedNormalized, lineEnding);
            await WriteRawAsync(fullPath, output, hadBom, ct);
            _logger.LogDebug("Edited {Path}: replaced {OldLen} chars with {NewLen} chars at position {Pos}",
                filePath, needle.Length, replacement.Length, firstIndex);
            // Metrics are measured on the LF-normalized needle/replacement that was actually
            // matched, so they are exact even on CRLF files (the on-disk delta can differ by the
            // line-ending conversion).
            ToolCallMetrics.Publish(new ToolCallFacts
            {
                OccurrencesReplaced = 1,
                CharactersRemoved = needle.Length,
                CharactersAdded = replacement.Length,
            });
            return $"Successfully replaced 1 occurrence of oldString in '{filePath}' ({lineEnding} line endings{(hadBom ? ", UTF-8 BOM preserved" : string.Empty)}).";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    /// <summary>Finds files matching a glob-like pattern under the working directory, returning at most 100 paths.</summary>
    /// <param name="pattern">Pattern to match, such as <c>**/*.cs</c>; search roots outside the working directory are rejected.</param>
    /// <returns>Relative matching paths, a no-results message, or an error message.</returns>
    [Description("Searches for files matching a glob pattern.")]
    public string glob(
        [Description("The glob pattern (e.g. '**/*.cs' or 'src/**/*.ts')")] string pattern)
    {
        try
        {
            var normalized = pattern.Replace('/', Path.DirectorySeparatorChar)
                                    .Replace('\\', Path.DirectorySeparatorChar);

            string searchRoot;
            string filePattern;
            SearchOption searchOption;

            var globIndex = normalized.IndexOf("**", StringComparison.Ordinal);
            if (globIndex >= 0)
            {
                // Has ** — extract prefix as search root, remainder as file pattern
                var prefix = globIndex > 0
                    ? normalized.Substring(0, globIndex).TrimEnd(Path.DirectorySeparatorChar)
                    : "";
                var remainder = normalized.Substring(globIndex + 2)
                    .TrimStart(Path.DirectorySeparatorChar);

                searchRoot = string.IsNullOrEmpty(prefix)
                    ? _workingDirectory
                    : Path.GetFullPath(Path.Combine(_workingDirectory, prefix));
                filePattern = string.IsNullOrEmpty(remainder) ? "*" : remainder;
                searchOption = SearchOption.AllDirectories;
            }
            else
            {
                // No ** — check for directory prefix
                var lastSep = normalized.LastIndexOf(Path.DirectorySeparatorChar);
                if (lastSep >= 0)
                {
                    var dirPart = normalized.Substring(0, lastSep);
                    filePattern = normalized.Substring(lastSep + 1);
                    searchRoot = Path.GetFullPath(Path.Combine(_workingDirectory, dirPart));
                    searchOption = SearchOption.TopDirectoryOnly;
                }
                else
                {
                    searchRoot = _workingDirectory;
                    filePattern = normalized;
                    searchOption = SearchOption.TopDirectoryOnly;
                }
            }

            // Security: ensure search root is within working directory (boundary-safe, platform-correct)
            if (PathSafety.ResolveWithinRoot(_workingDirectory, searchRoot) is null)
            {
                return $"Error: Pattern '{pattern}' resolves outside the work directory.";
            }

            if (!Directory.Exists(searchRoot))
            {
                return $"Error: Search directory does not exist: {Path.GetRelativePath(_workingDirectory, searchRoot)}";
            }

            var files = Directory.GetFiles(searchRoot, filePattern, searchOption);

            if (files.Length == 0) return "No files found matching the pattern.";

            var sb = new StringBuilder();
            var limit = Math.Min(files.Length, 100);
            for (var i = 0; i < limit; i++)
            {
                sb.AppendLine(Path.GetRelativePath(_workingDirectory, files[i]));
            }

            if (files.Length > limit)
            {
                sb.AppendLine($"... and {files.Length - limit} more.");
            }

            ToolCallMetrics.Publish(new ToolCallFacts { MatchCount = files.Length });
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    /// <summary>Searches files under the working directory for lines matching a regular expression.</summary>
    /// <param name="pattern">Regular expression applied separately to each line.</param>
    /// <param name="include">Optional file-name pattern; brace extensions such as <c>*.{ts,tsx}</c> are also supported.</param>
    /// <param name="ct">Token used to cancel enumeration and reads.</param>
    /// <returns>Matching paths and numbered lines, a no-results message, or an error message.</returns>
    [Description("Searches file contents using regular expressions.")]
    public async Task<string> grep(
        [Description("The regex pattern to search for in file contents")] string pattern,
        [Description("File pattern to include in the search (e.g. '*.cs', '*.{ts,tsx}')")] string? include = null,
        CancellationToken ct = default)
    {
        try
        {
            System.Text.RegularExpressions.Regex regex;
            try
            {
                regex = new System.Text.RegularExpressions.Regex(
                    pattern, System.Text.RegularExpressions.RegexOptions.Compiled);
            }
            catch (ArgumentException regexEx)
            {
                return $"Error: Invalid regex pattern: {regexEx.Message}";
            }

            // Fallback simplistic grep for cross-platform (not as robust as ripgrep but works natively)
            var searchPattern = string.IsNullOrEmpty(include) || include.Contains("{") ? "*.*" : include;
            var files = Directory.GetFiles(_workingDirectory, searchPattern, SearchOption.AllDirectories);
            
            // Filter out common binaries/obj/bin
            files = files.Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                                     !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                     !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)).ToArray();

            var sb = new StringBuilder();
            int matchCount = 0;
            int fileCount = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relPath = Path.GetRelativePath(_workingDirectory, file);
                
                // Very rudimentary include pattern filtering for multiple extensions if passed like "*.{ts,tsx}"
                if (!string.IsNullOrEmpty(include) && include.Contains("{"))
                {
                    var extensions = include.Replace("*.", "").Replace("{", "").Replace("}", "").Split(',');
                    if (!extensions.Any(ext => file.EndsWith("." + ext.Trim()))) continue;
                }

                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    bool fileHeaderAdded = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            if (!fileHeaderAdded)
                            {
                                sb.AppendLine($"\n{relPath}:");
                                fileHeaderAdded = true;
                                fileCount++;
                            }
                            sb.AppendLine($"{i + 1}: {lines[i].Trim()}");
                            matchCount++;
                            
                            if (matchCount > 100)
                            {
                                sb.AppendLine("... too many matches, truncating.");
                                ToolCallMetrics.Publish(new ToolCallFacts { MatchCount = matchCount, FileCount = fileCount });
                                return sb.ToString();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Ignore binary files or unreadable files
                }
            }

            if (matchCount == 0)
            {
                ToolCallMetrics.Publish(new ToolCallFacts { MatchCount = 0, FileCount = 0 });
                return "No matches found.";
            }
            ToolCallMetrics.Publish(new ToolCallFacts { MatchCount = matchCount, FileCount = fileCount });
            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error performing grep: {ex.Message}";
        }
    }

    // ---------- Line-ending / encoding helpers ----------

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly System.Text.UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Reads a file as UTF-8 text, reporting whether it had a UTF-8 BOM.</summary>
    private static (string Content, bool HadBom) ReadRaw(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hadBom ? 3 : 0;
        var content = System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        return (content, hadBom);
    }

    private static Task WriteRawAsync(string fullPath, string content, bool withBom, CancellationToken ct)
        => File.WriteAllTextAsync(fullPath, content, withBom ? Utf8WithBom : Utf8NoBom, ct);

    /// <summary>Returns "CRLF" when the text contains at least one CRLF and at least as many CRLFs as lone LFs; otherwise "LF".</summary>
    private static string DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }

        return crlf > 0 && crlf >= lf ? "CRLF" : "LF";
    }

    /// <summary>Splits on LF, stripping a trailing CR from each line. A trailing newline does not produce a phantom final empty line.</summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith("\r", StringComparison.Ordinal))
            {
                lines[i] = lines[i].Substring(0, lines[i].Length - 1);
            }
        }

        if (lines.Length > 0 && lines[lines.Length - 1].Length == 0 && text.EndsWith("\n", StringComparison.Ordinal))
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        return lines;
    }

    /// <summary>Normalizes all line breaks in <paramref name="text"/> to <paramref name="lineEnding"/> ("LF" or "CRLF").</summary>
    private static string NormalizeLineEndings(string text, string lineEnding)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return lineEnding == "CRLF" ? lf.Replace("\n", "\r\n") : lf;
    }
}