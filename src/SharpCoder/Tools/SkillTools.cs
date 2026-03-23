using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCoder.Tools;

public sealed class SkillTools
{
    private readonly string _workingDirectory;
    private static readonly Regex FrontmatterRegex = new Regex(@"^---\s*[\r\n]+(.*?)\n---\s*[\r\n]+", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NameRegex = new Regex(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex DescriptionRegex = new Regex(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public SkillTools(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    [Description("Load a specialized skill that provides domain-specific instructions and workflows. Before using a skill, verify it exists using list_skills.")]
    public async Task<string> load_skill(
        [Description("The name of the skill to load (without the .md extension)")] string name,
        CancellationToken ct = default)
    {
        var skillFile = ResolveSkillFile(name);
        if (skillFile is null)
            return $"Error: Skill '{name}' not found. Use list_skills to see available skills.";

        try
        {
            var content = await File.ReadAllTextAsync(skillFile, ct);
            return $"Loaded skill '{name}':\n\n{content}";
        }
        catch (Exception ex)
        {
            return $"Error loading skill: {ex.Message}";
        }
    }

    [Description("Lists all available skills in the project.")]
    public Task<string> list_skills(CancellationToken ct = default)
    {
        return Task.FromResult(ListSkillsSummary() ?? "No skills found.");
    }

    /// <summary>
    /// Returns a short summary of available skills for injection into the system prompt,
    /// or null if no skills directory exists.
    /// </summary>
    internal string? ListSkillsSummary()
    {
        var skillsDir = Path.Combine(_workingDirectory, ".github", "skills");
        if (!Directory.Exists(skillsDir))
            return null;

        var entries = EnumerateSkillEntries(skillsDir);
        if (entries.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("Available skills (use load_skill to read full instructions):");
        foreach (var (name, description) in entries)
            sb.AppendLine($"- {name}: {description}");
        return sb.ToString();
    }

    /// <summary>
    /// Resolves a skill name to a file path, supporting both flat files
    /// (<c>.github/skills/{name}.md</c>) and subdirectory format
    /// (<c>.github/skills/{name}/SKILL.md</c>).
    /// </summary>
    private string? ResolveSkillFile(string name)
    {
        var skillsDir = Path.Combine(_workingDirectory, ".github", "skills");
        if (!Directory.Exists(skillsDir))
            return null;

        // Flat file: .github/skills/{name}.md
        var flatFile = Path.Combine(skillsDir, $"{name}.md");
        if (File.Exists(flatFile))
            return flatFile;

        // Subdirectory: .github/skills/{name}/SKILL.md
        var subdirFile = Path.Combine(skillsDir, name, "SKILL.md");
        if (File.Exists(subdirFile))
            return subdirFile;

        return null;
    }

    /// <summary>
    /// Enumerates all skill entries from the skills directory, supporting both flat
    /// files and subdirectory format.
    /// </summary>
    private static List<(string Name, string Description)> EnumerateSkillEntries(string skillsDir)
    {
        var entries = new List<(string Name, string Description)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Flat files: .github/skills/*.md
        try
        {
            foreach (var file in Directory.GetFiles(skillsDir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!seen.Add(name)) continue;
                var description = ParseDescription(file);
                entries.Add((name, description));
            }
        }
        catch { /* ignore enumeration errors */ }

        // Subdirectories: .github/skills/{name}/SKILL.md
        try
        {
            foreach (var dir in Directory.GetDirectories(skillsDir))
            {
                var name = Path.GetFileName(dir);
                if (!seen.Add(name)) continue;
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var description = ParseDescription(skillFile);
                entries.Add((name, description));
            }
        }
        catch { /* ignore enumeration errors */ }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    private static string ParseDescription(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var fmMatch = FrontmatterRegex.Match(content);
            if (fmMatch.Success)
            {
                var frontmatter = fmMatch.Groups[1].Value;
                var descMatch = DescriptionRegex.Match(frontmatter);
                if (descMatch.Success)
                    return descMatch.Groups[1].Value.Trim();
            }
        }
        catch { /* ignore parse errors */ }

        return "No description provided.";
    }
}
