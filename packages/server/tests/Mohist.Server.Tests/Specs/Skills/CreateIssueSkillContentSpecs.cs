using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Skills)]
public sealed class CreateIssueSkillContentSpecs
{
    private static readonly string SkillDataRoot = ResolveSkillDataRoot();

    private static string SkillMarkdown => File.ReadAllText(Path.Combine(SkillDataRoot, "mohist-create-issue", "SKILL.md"));

    [Fact]
    public void PackagedCreateIssueSkill_StopsWhenNoWorkflowProfileIsEnabled()
    {
        var content = SkillMarkdown;

        Assert.Contains("the first enabled profile, else fail with an actionable error", content, StringComparison.Ordinal);
        Assert.Contains("ask the user to enable a workflow first", content, StringComparison.Ordinal);
        Assert.Contains("Do not invent a recommendation or create frontmatter", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Never leave recommended_workflow blank", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unconditional fallback", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guaranteed to exist", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedCreateIssueSkill_SelectsIssueTemplateViaCli()
    {
        var content = SkillMarkdown;

        // The skill must discover templates via the CLI, not from a bundled static copy.
        Assert.Contains("mo issue template list", content, StringComparison.Ordinal);
        Assert.Contains("mo issue template get", content, StringComparison.Ordinal);
        // The boundary question drives template selection.
        Assert.Contains("does external behavior change", content, StringComparison.OrdinalIgnoreCase);
        // The static per-template reference file is gone.
        Assert.DoesNotContain("references/issue-templates.md", content, StringComparison.Ordinal);
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
}
