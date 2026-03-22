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

public sealed class FileTools
{
    private readonly string _workingDirectory;
    private readonly ILogger _logger;

    public FileTools(string workingDirectory, ILogger? logger = null)
    {
        _workingDirectory = workingDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    private string GetFullPath(string path)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_workingDirectory, path));
        var root = Path.GetFullPath(_workingDirectory) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, Path.GetFullPath(_workingDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path '{path}' escapes the work directory.");
        }

        return fullPath;
    }

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

            // Security: ensure search root is within working directory
            var rootFull = Path.GetFullPath(searchRoot);
            var wdFull = Path.GetFullPath(_workingDirectory);
            if (!rootFull.StartsWith(wdFull, StringComparison.OrdinalIgnoreCase))
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
