using Mohist.Cli.Tests.Compatibility;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Skills;

public sealed class ExploreSkillContentTests
{
    private static string SkillMarkdown =>
        EmbeddedSkillData.ReadText("mohist-explore/SKILL.md");

    [Fact]
    public void PackagedExploreSkill_PreservesAgentFrontmatter()
    {
        var content = SkillMarkdown;

        Assert.StartsWith("---\n", content, StringComparison.Ordinal);
        Assert.Contains("name: mohist-explore", content, StringComparison.Ordinal);
        Assert.Contains("description: 把模糊的产品想法提炼成清晰的", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_IntroducesDistillationPurpose()
    {
        Assert.Contains(
            "Use this skill to **distill** a fuzzy idea into a clear",
            SkillMarkdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsThreeVoiceModel()
    {
        var content = SkillMarkdown;

        Assert.Contains("User Voice", content, StringComparison.Ordinal);
        Assert.Contains("Product Shape", content, StringComparison.Ordinal);
        Assert.Contains("Domain Model", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_EnforcesSerialDependencyChain()
    {
        Assert.Contains("dependency chain", SkillMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DoesNotCarryIssueCreationMechanics()
    {
        var content = SkillMarkdown;

        Assert.DoesNotContain("mo workflow list --described", content, StringComparison.Ordinal);
        Assert.DoesNotContain("recommended_workflow", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--body-file", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DoesNotCarryBodyTemplateReference()
    {
        Assert.DoesNotContain(
            "mohist-explore/references/issue-body-template.md",
            EmbeddedSkillData.Paths());
        Assert.DoesNotContain(
            EmbeddedSkillData.Paths(),
            path => path.StartsWith("mohist-explore/references/", StringComparison.Ordinal));
    }

    [Fact]
    public void PackagedExploreSkill_IsStillValidAccordingToSkillAssetService()
    {
        var files = new FakeFileSystem();
        EmbeddedSkillData.Populate(files);
        var environment = new MockEnvironmentVariableProvider();
        var resolver = new SkillAssetRootResolver(
            files,
            environment,
            getOverrideAssetRoot: () => EmbeddedSkillData.VirtualRoot,
            getManagedAssetRoot: null,
            getUserHome: () => "/mohist-tests/user");
        var service = new SkillAssetService(files, environment, resolver);

        var result = service.GetSkill("mohist-explore", includeSupplementaryFiles: true);

        Assert.True(result.Found, result.Error);
        Assert.NotNull(result.Skill);
        Assert.Equal("mohist-explore", result.Skill!.Name);
        Assert.Empty(result.Skill.SupplementaryFiles);
    }

    [Fact]
    public void PackagedSkillData_ContainsMohistAndExploreSkills()
    {
        var skillFiles = EmbeddedSkillData.Paths()
            .Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("mohist/SKILL.md", skillFiles);
        Assert.Contains("mohist-explore/SKILL.md", skillFiles);
    }
}
