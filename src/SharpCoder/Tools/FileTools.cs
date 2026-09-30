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
    [Description("Read a file from the local filesystem. Returns contents with each line prefixed by its line number. Use offset to read specific sections.")]
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

            var lines = await File.ReadAllLinesAsync(fullPath, ct);
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
            
            for (var i = 0; i < count && startIndex + i < lines.Length; i++)
            {
                var lineIndex = startIndex + i;
                result.AppendLine($"{lineIndex + 1}: {lines[lineIndex]}");
            }

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
    /// <param name="content">Complete replacement content to write.</param>
    /// <param name="ct">Token used to cancel the write.</param>
    /// <returns>A success message or an error message.</returns>
    [Description("Writes new content to a file, completely overwriting it. Do not use this to modify existing files - use edit_file instead.")]
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

            await File.WriteAllTextAsync(fullPath, content, ct);
            _logger.LogDebug("Wrote {Chars} chars to {Path}", content.Length, filePath);            return $"Successfully wrote {content.Length} characters to '{filePath}'.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    /// <summary>Replaces one exact, unique text occurrence in a file under the working directory.</summary>
    /// <param name="filePath">Relative or absolute file path, resolved within the working directory.</param>
    /// <param name="oldString">Non-empty text that must occur exactly once in the file.</param>
    /// <param name="newString">Text inserted in place of the matched content.</param>
    /// <param name="ct">Token used to cancel file reads and writes.</param>
    /// <returns>A success message or an error message describing why the replacement was not made.</returns>
    [Description("Performs exact string replacements in files. The oldString must exactly match the file content, including whitespace and indentation. Only one occurrence is replaced per call.")]
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

            var content = await File.ReadAllTextAsync(fullPath, ct);

            var firstIndex = content.IndexOf(oldString, StringComparison.Ordinal);
            if (firstIndex == -1)
            {
                return "Error: oldString not found in file. Ensure exact whitespace/indentation matching.";
            }

            var endIndex = firstIndex + oldString.Length;
            if (endIndex > content.Length)
            {
                return "Error: Internal inconsistency — matched text extends beyond file content.";
            }

            var secondIndex = content.IndexOf(oldString, endIndex, StringComparison.Ordinal);
            if (secondIndex != -1)
            {
                return "Error: Found multiple matches for oldString. Provide more surrounding lines to make it unique.";
            }

            var updatedContent = content.Substring(0, firstIndex)
                + newString
                + content.Substring(endIndex);
            await File.WriteAllTextAsync(fullPath, updatedContent, ct);
            _logger.LogDebug("Edited {Path}: replaced {OldLen} chars with {NewLen} chars at position {Pos}",
                filePath, oldString.Length, newString.Length, firstIndex);
            return $"Successfully replaced 1 occurrence of oldString in '{filePath}'.";
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
                            }
                            sb.AppendLine($"{i + 1}: {lines[i].Trim()}");
                            matchCount++;
                            
                            if (matchCount > 100)
                            {
                                sb.AppendLine("... too many matches, truncating.");
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

            if (matchCount == 0) return "No matches found.";
            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Error performing grep: {ex.Message}";
        }
    }
}
