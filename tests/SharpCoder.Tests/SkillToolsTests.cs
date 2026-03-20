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
    public async Task ListSkills_NoDir_ReturnsNotFound()
    {
        var result = await _tools.list_skills(TestContext.Current.CancellationToken);
        Assert.Contains("No skills directory found.", result);
    }

    [Fact]
    public async Task LoadSkill_Exists_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var skillsDir = Path.Combine(_testDir, ".github", "skills");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(Path.Combine(skillsDir, "test-skill.md"), "Do this skill well.", ct);

        var listResult = await _tools.list_skills(ct);
        Assert.Contains("test-skill", listResult);

        var loadResult = await _tools.load_skill("test-skill", ct);
        Assert.Contains("Do this skill well.", loadResult);
    }

    [Fact]
    public async Task ListSkills_WithFrontmatter_ReturnsParsedInfo()
    {
        var ct = TestContext.Current.CancellationToken;
        var skillsDir = Path.Combine(_testDir, ".github", "skills");
        Directory.CreateDirectory(skillsDir);
        
        var content = @"---
name: Awesome Skill
description: This skill does awesome things.
---

Here is the content.";

        await File.WriteAllTextAsync(Path.Combine(skillsDir, "awesome.md"), content, ct);

        var listResult = await _tools.list_skills(ct);
        Assert.Contains("- awesome: Awesome Skill - This skill does awesome things.", listResult);
    }
}
