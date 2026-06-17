using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

/// <summary>
/// Verifies the packaged <c>mohist-explore</c> skill guidance carries the
/// three-voice requirement-distillation workflow. The explore skill produces
/// PRD content only; issue-creation mechanics (frontmatter, workflow, risk,
/// CLI handoff) live in the <c>mohist</c> skill and are not expected here.
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
        var content = SkillMarkdown;

        Assert.Contains("dependency chain", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_DoesNotCarryIssueCreationMechanics()
    {
        var content = SkillMarkdown;

        // Execution knowledge was moved to the mohist skill. Explore must stay
        // immune to CLI mechanics so it does not drift with CLI versions.
        Assert.DoesNotContain("mo workflow list --described", content, StringComparison.Ordinal);
        Assert.DoesNotContain("recommended_workflow", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--body-file", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedExploreSkill_ProvidesReferenceTemplateFile()
    {
        var templatePath = Path.Combine(ReferencesRoot, "issue-body-template.md");

        Assert.True(File.Exists(templatePath), $"Expected issue body template at '{templatePath}'.");

        var template = File.ReadAllText(templatePath);
        // The template is pure PRD content — no frontmatter (that is mohist's job).
        Assert.DoesNotContain("recommended_workflow", template, StringComparison.Ordinal);
        Assert.Contains("## User Voice", template, StringComparison.Ordinal);
        Assert.Contains("## Product Shape", template, StringComparison.Ordinal);
        Assert.Contains("## Domain Model", template, StringComparison.Ordinal);
        Assert.Contains("## Acceptance Criteria", template, StringComparison.Ordinal);
        Assert.Contains("## Non-Goals", template, StringComparison.Ordinal);
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
