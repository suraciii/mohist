using Xunit;

namespace Mohist.Server.UnitTests.Skills;

public sealed class CreateIssueSkillContentTests
{
    private static string SkillMarkdown =>
        EmbeddedSkillData.ReadText("mohist-create-issue/SKILL.md");

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

        Assert.Contains("mo issue template list", content, StringComparison.Ordinal);
        Assert.Contains("mo issue template get", content, StringComparison.Ordinal);
        Assert.Contains("does external behavior change", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("references/issue-templates.md", content, StringComparison.Ordinal);
    }
}
