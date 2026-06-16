using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

/// <summary>
/// Verifies the packaged <c>mohist-explore</c> skill guidance carries the
/// structured issue-body production workflow introduced by issue-102/T-005.
/// These specs read the actual packaged asset on disk so they catch drift
/// between the served skill content and the spec.
/// </summary>
[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Skills)]
public sealed class ExploreSkillContentSpecs
{
    private static readonly string SkillDataRoot = ResolveSkillDataRoot();

    private static string SkillMarkdown => File.ReadAllText(Path.Combine(SkillDataRoot, "mohist-explore", "SKILL.md"));

    private static string ReferencesRoot => Path.Combine(SkillDataRoot, "mohist-explore", "references");

    [Fact]
    public void PackagedExploreSkill_PreservesAgentFrontmatter()
    {
        var content = SkillMarkdown;

        Assert.StartsWith("---\n", content, StringComparison.Ordinal);
        Assert.Contains("name: mohist-explore", content, StringComparison.Ordinal);
        Assert.Contains("description: 从产品和用户视角探索 mohist 项目", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_KeepsExistingIntroPhrase()
    {
        Assert.Contains(
            "Use this skill to explore Mohist from the product and user perspective",
            SkillMarkdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsAllFrontmatterFields()
    {
        var content = SkillMarkdown;

        Assert.Contains("`recommended_workflow`", content, StringComparison.Ordinal);
        Assert.Contains("`recommended_workflow_reason`", content, StringComparison.Ordinal);
        Assert.Contains("`risk`", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_ProvidesBodySectionTemplate()
    {
        var content = SkillMarkdown;

        Assert.Contains("## Background", content, StringComparison.Ordinal);
        Assert.Contains("## Goal", content, StringComparison.Ordinal);
        Assert.Contains("## Non-goals", content, StringComparison.Ordinal);
        Assert.Contains("## Acceptance criteria", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_InstructsWorkflowDiscoveryCommand()
    {
        Assert.Contains("mo workflow list --described", SkillMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsSuitableForMatchingLogic()
    {
        var content = SkillMarkdown;

        Assert.Contains("suitable_for", content, StringComparison.Ordinal);
        Assert.Contains("matching", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsDefaultFallback()
    {
        var content = SkillMarkdown;

        Assert.Contains("mohist/default", content, StringComparison.Ordinal);
        Assert.Contains("fallback", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsRiskEnum()
    {
        var content = SkillMarkdown;

        Assert.Contains("`low`", content, StringComparison.Ordinal);
        Assert.Contains("`medium`", content, StringComparison.Ordinal);
        Assert.Contains("`high`", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsUserConfirmationStep()
    {
        var content = SkillMarkdown;

        Assert.Contains("confirmation", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo issue create", content, StringComparison.Ordinal);
        Assert.Contains("--body-file", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DocumentsBodyFileHandoff()
    {
        var content = SkillMarkdown;

        Assert.Contains("mo issue create", content, StringComparison.Ordinal);
        Assert.Contains("--body-file", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_ProvidesReferenceTemplateFile()
    {
        var templatePath = Path.Combine(ReferencesRoot, "issue-body-template.md");

        Assert.True(File.Exists(templatePath), $"Expected issue body template at '{templatePath}'.");

        var template = File.ReadAllText(templatePath);
        Assert.Contains("recommended_workflow:", template, StringComparison.Ordinal);
        Assert.Contains("recommended_workflow_reason:", template, StringComparison.Ordinal);
        Assert.Contains("risk:", template, StringComparison.Ordinal);
        Assert.Contains("## Background", template, StringComparison.Ordinal);
        Assert.Contains("## Goal", template, StringComparison.Ordinal);
        Assert.Contains("## Non-goals", template, StringComparison.Ordinal);
        Assert.Contains("## Acceptance criteria", template, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_IsStillValidAccordingToSkillAssetService()
    {
        var files = new FakeFileSystem();
        var environment = new MockEnvironmentVariableProvider();
        var isolatedRoot = Path.Combine("/tmp", $"mohist-explore-content-{Guid.NewGuid():N}", "skill-data");
        CopyDirectory(SkillDataRoot, isolatedRoot, files);
        SkillAssetManifest.Write(
            isolatedRoot,
            SkillAssetManifest.ResolveCurrentBuildIdentity(),
            SkillAssetService.BuiltInSkillNames.ToArray(),
            files);

        var resolver = new SkillAssetRootResolver(
            files,
            environment,
            getOverrideAssetRoot: () => isolatedRoot,
            getManagedAssetRoot: null,
            getUserHome: () => isolatedRoot);
        var service = new SkillAssetService(files, environment, resolver);

        var result = service.GetSkill("mohist-explore", includeSupplementaryFiles: true);

        Assert.True(result.Found, result.Error);
        Assert.NotNull(result.Skill);
        Assert.Equal("mohist-explore", result.Skill!.Name);
        Assert.NotEmpty(result.Skill.SupplementaryFiles);
        Assert.Contains(
            result.Skill.SupplementaryFiles,
            file => string.Equals(file.RelativePath, "references/issue-body-template.md", StringComparison.Ordinal));
    }

    [Fact]
    public void PackagedExploreSkill_ManifestListsExploreAndMohistFilesExist()
    {
        var manifestPath = Path.Combine(SkillDataRoot, SkillAssetManifest.FileName);

        Assert.True(File.Exists(manifestPath), $"Manifest missing at '{manifestPath}'.");
        var read = SkillAssetManifest.TryRead(SkillDataRoot);
        Assert.True(read.IsFound, read.Error ?? "Manifest could not be read.");
        Assert.Contains("mohist", read.Data!.Skills);
        Assert.Contains("mohist-explore", read.Data!.Skills);

        foreach (var skill in read.Data!.Skills)
        {
            var skillFile = Path.Combine(SkillDataRoot, skill, "SKILL.md");
            Assert.True(File.Exists(skillFile), $"Manifest lists '{skill}' but '{skillFile}' is missing.");
        }
    }

    private static string ResolveSkillDataRoot()
    {
        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "cli", "Mohist.Cli", "skill-data"));
        if (!Directory.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Packaged skill-data directory was not found at '{candidate}'. Test cannot run.");
        }

        return candidate;
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot, FakeFileSystem files)
    {
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(targetRoot, relative);
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                files.AddDirectory(directory);
            files.AddFile(target, File.ReadAllText(file));
        }
    }
}
