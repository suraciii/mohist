using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Skills;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Skills)]
public sealed class CreateIssueSkillContentSpecs
{
    private static readonly string SkillDataRoot = ResolveSkillDataRoot();

    private static string SkillMarkdown => File.ReadAllText(Path.Combine(SkillDataRoot, "mohist-create-issue", "SKILL.md"));

    private static string IssueTemplatesMarkdown => File.ReadAllText(Path.Combine(
        SkillDataRoot,
        "mohist-create-issue",
        "references",
        "issue-templates.md"));

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
    public void PackagedCreateIssueTemplate_UsesDiscoveredEnabledWorkflowProfileOnly()
    {
        var content = IssueTemplatesMarkdown;

        Assert.Contains("An enabled profile id returned by `mo workflow list --described`", content, StringComparison.Ordinal);
        Assert.Contains("stop and ask the user to enable a workflow first", content, StringComparison.Ordinal);
        Assert.Contains("<enabled-profile-id-from-discovery>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("mohist/local when nothing matches", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("falling back to", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("or `mohist/local`", content, StringComparison.OrdinalIgnoreCase);
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
