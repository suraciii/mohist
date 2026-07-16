using Mohist.Server.UnitTests.Support;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.UnitTests.Skills;

public sealed class SkillAssetServiceTests
{
    private const string VirtualRoot =
        "/mohist-tests/skill-asset-service";

    private readonly FakeFileSystem _files = new();

    [Fact]
    public void ListVisibleSkills_ReturnsExplicitBuiltInsSortedByName()
    {
        EmbeddedSkillData.Populate(_files);
        var service = new SkillAssetService(
            _files,
            EmbeddedSkillData.VirtualRoot);

        var skills = service.ListVisibleSkills();

        Assert.Collection(
            skills,
            skill => Assert.Equal("mohist", skill.Name),
            skill => Assert.Equal("mohist-create-epic", skill.Name),
            skill => Assert.Equal("mohist-create-issue", skill.Name),
            skill => Assert.Equal("mohist-explore", skill.Name));
        Assert.All(
            skills,
            skill => Assert.False(
                string.IsNullOrWhiteSpace(
                    skill.Description)));
    }

    [Fact]
    public void GetSkill_UsesOverrideAssetRoot_WhenProvided()
    {
        var root = Path.Combine(
            VirtualRoot,
            "override-assets");
        WriteSkill(root, "mohist", "override");
        WriteSkill(root, "mohist-explore", "explore");

        var service = new SkillAssetService(_files, root);

        var result = service.GetSkill(
            "mohist",
            includeSupplementaryFiles: false);

        Assert.True(result.Found);
        Assert.Equal(
            Path.Combine(root, "mohist"),
            result.Skill!.DirectoryPath);
        Assert.Contains(
            "# override",
            result.Skill.SkillMarkdown);
    }

    [Fact]
    public void GetSkill_ReturnsClearFailure_ForUnknownSkill()
    {
        EmbeddedSkillData.Populate(_files);
        var service = new SkillAssetService(
            _files,
            EmbeddedSkillData.VirtualRoot);

        var result = service.GetSkill(
            "unknown-skill",
            includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.Equal(
            "Unknown Mohist built-in skill " +
            "'unknown-skill'.",
            result.Error);
    }

    [Fact]
    public void GetSkill_FullContent_AppendsSupplementaryFilesInDeterministicOrder()
    {
        var root = Path.Combine(
            VirtualRoot,
            "ordered-assets");
        WriteSkill(root, "mohist", "body");
        WriteSkill(root, "mohist-explore", "explore");
        _files.AddDirectory(
            Path.Combine(root, "mohist", "references"));
        _files.AddDirectory(
            Path.Combine(
                root,
                "mohist",
                "templates",
                "nested"));
        _files.AddFile(
            Path.Combine(
                root,
                "mohist",
                "templates",
                "z-last.md"),
            "z");
        _files.AddFile(
            Path.Combine(
                root,
                "mohist",
                "references",
                "a-first.md"),
            "a");
        _files.AddFile(
            Path.Combine(
                root,
                "mohist",
                "templates",
                "nested",
                "m-middle.md"),
            "m");

        var service = new SkillAssetService(_files, root);

        var result = service.GetSkill(
            "mohist",
            includeSupplementaryFiles: true);

        Assert.True(result.Found);
        Assert.Equal(
            [
                "references/a-first.md",
                "templates/nested/m-middle.md",
                "templates/z-last.md",
            ],
            result.Skill!.SupplementaryFiles
                .Select(file => file.RelativePath)
                .ToArray());
    }

    [Fact]
    public void Service_DoesNotTouchDotMohistSkills()
    {
        var root = Path.Combine(VirtualRoot, "assets");
        WriteSkill(root, "mohist", "body");
        WriteSkill(root, "mohist-explore", "explore");
        var mohistSkillsDir = Path.Combine(
            root,
            ".mohist",
            "skills");
        _files.AddDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(
            mohistSkillsDir,
            "sentinel.txt");
        _files.AddFile(sentinelPath, "keep");

        var service = new SkillAssetService(_files, root);

        _ = service.ListVisibleSkills();
        _ = service.GetSkill(
            "mohist",
            includeSupplementaryFiles: true);

        Assert.True(_files.HasFile(sentinelPath));
        Assert.Equal(
            "keep",
            _files.ReadAllText(sentinelPath));
    }

    private void WriteSkill(
        string root,
        string name,
        string heading)
    {
        _files.AddDirectory(Path.Combine(root, name));
        _files.AddFile(
            Path.Combine(root, name, "SKILL.md"),
            $"---\nname: {name}\n" +
            $"description: {DescriptionFor(name)}\n" +
            $"---\n\n# {heading}\n");
    }

    private static string DescriptionFor(string name) =>
        name switch
        {
            "mohist" =>
                "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。",
            "mohist-explore" =>
                "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
            _ => throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                null),
        };
}
