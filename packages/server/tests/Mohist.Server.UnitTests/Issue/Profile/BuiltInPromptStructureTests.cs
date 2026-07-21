using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class BuiltInPromptStructureTests
{
    [Fact]
    public void BuiltInPrompts_AllParseWithRequiredFrontmatterFields()
    {
        var templates = new FilePromptLoader().LoadAllTemplates();

        Assert.NotEmpty(templates);
        foreach (var (key, template) in templates)
        {
            Assert.False(string.IsNullOrWhiteSpace(template.DisplayName), $"{key}: DisplayName is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Description), $"{key}: Description is required");
            Assert.False(string.IsNullOrWhiteSpace(template.Body), $"{key}: Body is required");
        }
    }

    [Fact]
    public void FixPrChecksPrompt_UsesProjectPrContextAndFailureError()
    {
        var body = new FilePromptLoader().LoadAllTemplates()["fix-pr-checks"].Body;

        Assert.Contains("${{ vars.github.pr.number }}", body);
        Assert.Contains("${{ vars.github.pr.url }}", body);
        Assert.Contains("${{ failure.error.message }}", body);
        Assert.DoesNotContain("${{ failure.output", body);
    }

    [Fact]
    public void FixPrChecksPrompt_IsStageAgnosticForCheckAndIntegrate()
    {
        var template = new FilePromptLoader().LoadAllTemplates()["fix-pr-checks"];

        Assert.Null(template.Stage);
        Assert.Contains("check", template.Tags);
        Assert.Contains("integrate", template.Tags);

        Assert.DoesNotContain("merge-github-pr rejected", template.Body);
        Assert.DoesNotContain("only checks PR state", template.Body);
        Assert.Contains("gh pr view", template.Body);
        Assert.Contains("gh run view", template.Body);
    }
}
