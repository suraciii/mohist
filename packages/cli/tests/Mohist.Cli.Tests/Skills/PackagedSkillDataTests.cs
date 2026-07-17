using Mohist.Cli.Tests.Compatibility;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Skills;

public sealed class PackagedSkillDataTests
{
    [Fact]
    public void PackagedExploreSkill_IsLoadableAccordingToSkillAssetService()
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
