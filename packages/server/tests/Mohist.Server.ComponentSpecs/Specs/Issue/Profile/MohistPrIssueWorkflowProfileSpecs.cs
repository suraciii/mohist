using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.ComponentSpecs.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Profile;

public class MohistGithubPrIssueWorkflowProfileSpecs
{
    [Fact]
    public void IssueWorkflowProfiles_ExposesGithubPrIdConstant()
    {
        Assert.Equal("mohist/github-pr", IssueWorkflowProfiles.GithubPrId);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_ExposesCorrectMetadata()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.Equal("Mohist GitHub PR", profile.DisplayName);
        Assert.False(profile.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Contains("gh", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh auth login", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub PR", profile.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionReadsFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal(MohistWorkflow.ResolveDescription(MohistWorkflow.GithubPrWorkflowDefinition), profile.Description);
        Assert.EndsWith("`gh` CLI on the runner host and `gh auth login` against the target repository.", profile.Description);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_Definition_ComesFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Same(MohistWorkflow.GithubPrWorkflowDefinition, profile.Definition);
        Assert.NotSame(MohistWorkflow.Definition, profile.Definition);
        Assert.Equal("mohist/github-pr", profile.Definition.Id);
    }

    // ===================== Registry exposure =====================
}
