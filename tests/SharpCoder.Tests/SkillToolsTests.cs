using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpCoder.Tools;

namespace SharpCoder.Tests;

public class SkillToolsTests : IDisposable
{
    private readonly string _testDir;
    private readonly SkillTools _tools;

    public SkillToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SharpCoderSkills_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _tools = new SkillTools(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void ListSkills_NoDir_ReturnsNotFound()
    {
        var result = _tools.list_skills();
        Assert.Contains("No skills directory found.", result);
    }

    [Fact]
    public async Task LoadSkill_Exists_ReturnsContent()
    {
        var skillsDir = Path.Combine(_testDir, ".github", "skills");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "test-skill.md"), "Do this skill well.");

        var listResult = _tools.list_skills();
        Assert.Contains("test-skill", listResult);

        var loadResult = await _tools.load_skill("test-skill");
        Assert.Contains("Do this skill well.", loadResult);
    }
}
