using Mohist.Cli;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Skills;

public sealed class SkillAssetServiceSpecs : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mohist-skill-assets-{Guid.NewGuid():N}");

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void ListVisibleSkills_ReturnsExplicitBuiltInsSortedByName()
    {
        var service = new SkillAssetService(GetPackagedSkillRoot());

        var skills = service.ListVisibleSkills();

        Assert.Collection(
            skills,
            skill => Assert.Equal("mohist", skill.Name),
            skill => Assert.Equal("mohist-explore", skill.Name));
        Assert.All(skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Description)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void GetSkill_UsesOverrideAssetRoot_WhenProvided()
    {
        var root = Path.Combine(_tempRoot, "override-assets");
        Directory.CreateDirectory(Path.Combine(root, "mohist"));
        File.WriteAllText(
            Path.Combine(root, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: 执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。\n---\n\n# override\n");
        Directory.CreateDirectory(Path.Combine(root, "mohist-explore"));
        File.WriteAllText(
            Path.Combine(root, "mohist-explore", "SKILL.md"),
            "---\nname: mohist-explore\ndescription: 把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。\n---\n\n# explore\n");

        var service = new SkillAssetService(root);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: false);

        Assert.True(result.Found);
        Assert.Equal(Path.Combine(root, "mohist"), result.Skill!.DirectoryPath);
        Assert.Contains("# override", result.Skill.SkillMarkdown);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void GetSkill_ReturnsClearFailure_ForUnknownSkill()
    {
        var service = new SkillAssetService(GetPackagedSkillRoot());

        var result = service.GetSkill("unknown-skill", includeSupplementaryFiles: false);

        Assert.False(result.Found);
        Assert.Equal("Unknown Mohist built-in skill 'unknown-skill'.", result.Error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void GetSkill_FullContent_AppendsSupplementaryFilesInDeterministicOrder()
    {
        var root = Path.Combine(_tempRoot, "ordered-assets");
        WriteSkill(root, "mohist", "body");
        WriteSkill(root, "mohist-explore", "explore");
        Directory.CreateDirectory(Path.Combine(root, "mohist", "references"));
        Directory.CreateDirectory(Path.Combine(root, "mohist", "templates", "nested"));
        File.WriteAllText(Path.Combine(root, "mohist", "templates", "z-last.md"), "z");
        File.WriteAllText(Path.Combine(root, "mohist", "references", "a-first.md"), "a");
        File.WriteAllText(Path.Combine(root, "mohist", "templates", "nested", "m-middle.md"), "m");

        var service = new SkillAssetService(root);

        var result = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.True(result.Found);
        Assert.Equal(
            ["references/a-first.md", "templates/nested/m-middle.md", "templates/z-last.md"],
            result.Skill!.SupplementaryFiles.Select(file => file.RelativePath).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Skills)]
    [Fact]
    public void Service_DoesNotTouchDotMohistSkills()
    {
        var root = Path.Combine(_tempRoot, "assets");
        WriteSkill(root, "mohist", "body");
        WriteSkill(root, "mohist-explore", "explore");
        var mohistSkillsDir = Path.Combine(root, ".mohist", "skills");
        Directory.CreateDirectory(mohistSkillsDir);
        var sentinelPath = Path.Combine(mohistSkillsDir, "sentinel.txt");
        File.WriteAllText(sentinelPath, "keep");

        var service = new SkillAssetService(root);

        _ = service.ListVisibleSkills();
        _ = service.GetSkill("mohist", includeSupplementaryFiles: true);

        Assert.True(File.Exists(sentinelPath));
        Assert.Equal("keep", File.ReadAllText(sentinelPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string GetPackagedSkillRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "..",
        "cli",
        "Mohist.Cli",
        "skill-data"));

    private static void WriteSkill(string root, string name, string heading)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(
            Path.Combine(root, name, "SKILL.md"),
            $"---\nname: {name}\ndescription: {DescriptionFor(name)}\n---\n\n# {heading}\n");
    }

    private static string DescriptionFor(string name) => name switch
    {
        "mohist" => "执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。",
        "mohist-explore" => "把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 \"提炼需求\"、\"写 PRD\"、\"沉淀 issue\"、\"需求文档\"、\"探索\"、\"完善 issue\"。",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
