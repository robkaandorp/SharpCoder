using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder.Tools;

public sealed class FileTools
{
    private readonly string _workingDirectory;

    public FileTools(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    private string GetFullPath(string path)
    {
        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_workingDirectory, path);
        return Path.GetFullPath(fullPath);
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
            
            if (offset < 1) offset = 1;
            var startIndex = offset - 1;
            
            if (startIndex >= lines.Length)
            {
                return $"Error: Offset {offset} is beyond the end of the file (total lines: {lines.Length}).";
            }

            var count = Math.Min(limit, lines.Length - startIndex);
            var result = new StringBuilder();
            
            for (var i = 0; i < count; i++)
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
            return $"Successfully wrote {content.Length} characters to '{filePath}'.";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    [Description("Performs exact string replacements in files. The oldString must exactly match the file content, including whitespace and indentation.")]
    public async Task<string> edit_file(
        [Description("The relative or absolute path to the file")] string filePath,
        [Description("The exact text to replace. Must match the existing file exactly.")] string oldString,
        [Description("The new text to insert in its place.")] string newString,
        CancellationToken ct = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File '{filePath}' does not exist.";
            }

            var content = await File.ReadAllTextAsync(fullPath, ct);
            
            var matchCount = 0;
            var index = 0;
            while ((index = content.IndexOf(oldString, index, StringComparison.Ordinal)) != -1)
            {
                matchCount++;
                index += oldString.Length;
            }

            if (matchCount == 0)
            {
                return "Error: oldString not found in file. Ensure exact whitespace/indentation matching.";
            }
            if (matchCount > 1)
            {
                return $"Error: Found {matchCount} matches for oldString. Provide more surrounding lines to make it unique.";
            }

            var updatedContent = content.Replace(oldString, newString);
            await File.WriteAllTextAsync(fullPath, updatedContent, ct);
            
            return $"Successfully replaced 1 occurrence of oldString in '{filePath}'.";
        }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    [Description("Searches for files matching a glob pattern.")]
    public string search_files(
        [Description("The glob pattern (e.g. '**/*.cs' or 'src/**/*.ts')")] string pattern)
    {
        // Simple search implementation
        try
        {
            var isGlob = pattern.Contains("**");
            var searchPattern = isGlob ? pattern.Replace("**\\", "").Replace("**/", "") : pattern;
            var files = Directory.GetFiles(_workingDirectory, searchPattern, SearchOption.AllDirectories);
            
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
}
