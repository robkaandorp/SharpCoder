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
        var skillsDir = Path.Combine(_workingDirectory, ".github", "skills");
        if (!Directory.Exists(skillsDir))
        {
            return "Error: Skills directory (.github/skills) does not exist.";
        }

        var skillFile = Path.Combine(skillsDir, $"{name}.md");
        if (!File.Exists(skillFile))
        {
            return $"Error: Skill '{name}' not found. Use list_skills to see available skills.";
        }

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
    public async Task<string> list_skills(CancellationToken ct = default)
    {
        var skillsDir = Path.Combine(_workingDirectory, ".github", "skills");
        if (!Directory.Exists(skillsDir))
        {
            return "No skills directory found.";
        }

        try
        {
            var files = Directory.GetFiles(skillsDir, "*.md");
            if (files.Length == 0)
            {
                return "No skills found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Available skills:");
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var content = await File.ReadAllTextAsync(file, ct);
                
                var name = fileName;
                var description = "No description provided.";
                
                var fmMatch = FrontmatterRegex.Match(content);
                if (fmMatch.Success)
                {
                    var frontmatter = fmMatch.Groups[1].Value;
                    
                    var nameMatch = NameRegex.Match(frontmatter);
                    if (nameMatch.Success)
                    {
                        name = nameMatch.Groups[1].Value.Trim();
                    }
                    
                    var descMatch = DescriptionRegex.Match(frontmatter);
                    if (descMatch.Success)
                    {
                        description = descMatch.Groups[1].Value.Trim();
                    }
                }
                
                sb.AppendLine($"- {fileName}: {name} - {description}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error listing skills: {ex.Message}";
        }
    }
}
