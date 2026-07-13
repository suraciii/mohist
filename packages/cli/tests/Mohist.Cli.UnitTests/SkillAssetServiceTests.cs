using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.UnitTests;

public sealed class SkillAssetServiceTests
{
    private readonly FakeFileSystem _files = new();
    private readonly MockEnvironmentVariableProvider _environment = new();

    [Fact]
    public void ListVisibleSkills_ReturnsMetadataSortedByName()
    {
        var root = "/assets";
        WriteSkill(root, "mohist-explore", "explore");
        WriteSkill(root, "mohist", "body");
        var service = CreateService(root);

        var skills = service.ListVisibleSkills();

        Assert.Collection(
            skills,
            skill =>
            {
                Assert.Equal("mohist", skill.Name);
                Assert.False(string.IsNullOrWhiteSpace(skill.Description));
            },
            skill =>
            {
                Assert.Equal("mohist-explore", skill.Name);
                Assert.False(string.IsNullOrWhiteSpace(skill.Description));
            });
    }

    [Fact]
    public void GetSkill_UsesSelectedAssetRoot()
    {
        var root = "/override-assets";
        WriteSkill(root, "mohist", "override");
        var service = CreateService(root);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.True(result.Found);
        Assert.Equal(Path.Combine(root, "mohist"), result.Skill!.DirectoryPath);
        Assert.Contains("# override", result.Skill.SkillMarkdown);
    }

    [Fact]
    public void GetSkill_ReturnsClearFailure_ForUnknownSkill()
    {
        var root = "/assets";
        WriteSkill(root, "mohist", "body");
        var service = CreateService(root);

        var result = service.GetSkill("unknown-skill", includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.Equal("Unknown Mohist built-in skill 'unknown-skill'.", result.Error);
    }

    [Fact]
    public void GetSkill_FullContent_AppendsSupplementaryFilesInDeterministicOrder()
    {
        var root = "/ordered-assets";
        WriteSkill(root, "mohist", "body");
        _files.AddDirectory(Path.Combine(root, "mohist", "references"));
        _files.AddDirectory(Path.Combine(root, "mohist", "templates", "nested"));
        _files.AddFile(Path.Combine(root, "mohist", "templates", "z-last.md"), "z");
        _files.AddFile(Path.Combine(root, "mohist", "references", "a-first.md"), "a");
        _files.AddFile(Path.Combine(root, "mohist", "templates", "nested", "m-middle.md"), "m");
        var service = CreateService(root);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.True(result.Found);
        Assert.Equal(
            ["references/a-first.md", "templates/nested/m-middle.md", "templates/z-last.md"],
            result.Skill!.SupplementaryFiles.Select(file => file.RelativePath).ToArray());
    }

    [Fact]
    public void Service_DoesNotTouchDotMohistSkills()
    {
        var root = "/assets";
        WriteSkill(root, "mohist", "body");
        var mohistSkillsDir = Path.Combine(root, ".mohist", "skills");
        _files.AddDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(mohistSkillsDir, "sentinel.txt");
        _files.AddFile(sentinelPath, "keep");
        var service = CreateService(root);

        _ = service.ListVisibleSkills();
        _ = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.True(_files.HasFile(sentinelPath));
        Assert.Equal("keep", _files.ReadAllText(sentinelPath));
    }

    private SkillAssetService CreateService(string root)
    {
        var resolver = new SkillAssetRootResolver(
            _files,
            _environment,
            getOverrideAssetRoot: () => root,
            getManagedAssetRoot: null,
            getUserHome: () => "/home/test");
        return new SkillAssetService(_files, _environment, resolver);
    }

    private void WriteSkill(string root, string name, string heading)
    {
        _files.AddDirectory(root);
        var skillDirectory = Path.Combine(root, name);
        _files.AddDirectory(skillDirectory);
        _files.AddFile(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {name}\ndescription: test {name}\n---\n\n# {heading}\n");
    }
}
