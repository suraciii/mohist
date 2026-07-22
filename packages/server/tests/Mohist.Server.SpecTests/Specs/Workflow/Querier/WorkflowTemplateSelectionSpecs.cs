using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowTemplateSelectionSpecs : WorkflowProfileManagerTestFactory
{
    [Fact]
    public async Task LoadTemplate_FallsBackToSystemDefault_WhenRunContextMissing()
    {
        var result = await Manager.LoadTemplateAsync("unknown-run-id");

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/local", result.Id);
    }

    [Fact]
    public async Task LoadTemplate_UsesIssueCustomWithoutRunProfileBinding()
    {
        var runId = "wr_snap01";
        await SeedAsync(projectId: "proj1", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority2_ReturnsIssueCustomTemplate()
    {
        var runId = "wr_issue01";
        await SeedAsync(projectId: "proj2", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority3_ReturnsIssueReferencedTemplate()
    {
        var runId = "wr_ref01";
        await SeedAsync(projectId: "proj3", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: "my-tmpl",
            projectTemplateJson: SerializeDefinition("my-tmpl", stageCount: 4));

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("my-tmpl", result.Id);
        Assert.Equal(4, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultCustomTemplate_NoIssueSelection_UsesProjectDefault()
    {
        var runId = "wr_default01";
        await SeedAsync(projectId: "proj4", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("default-tmpl", result.Id);
        Assert.Equal(5, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultSystemTemplate_FallsBackToSystemTemplate()
    {
        var runId = "wr_system_default01";
        await SeedAsync(projectId: "proj-sys", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local");

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/local", result.Id);
        Assert.Contains(result.Structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadTemplate_DisabledProjectDefaultSystemTemplate_UsesFirstEnabledProfile()
    {
        var runId = "wr_disabled_default01";
        await SeedWithoutRunAsync(projectId: "proj-disabled-default", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local"]);

        var result = await Manager.LoadTemplateAsync(runId, "proj-disabled-default", 1);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/github-pr", result.Id);
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_template";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-template", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manager.LoadTemplateAsync(runId, "proj-all-disabled-template", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsBeforeIssueCustomTemplate()
    {
        var runId = "wr_all_disabled_custom_template";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-custom-template", issueNumber: 1,
            issueTemplateJson: SerializeDefinition("issue-custom-disabled", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manager.LoadTemplateAsync(runId, "proj-all-disabled-custom-template", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_template";
        await SeedAsync(projectId: "proj-existing-disabled-template", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/local", result.Id);
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_NoOverrides_UsesPrSystemTemplate()
    {
        var runId = "wr_issue_pr";
        await SeedAsync(projectId: "proj-issue-pr", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/github-pr", result.Id);
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssueDefaultProfile_NoOverrides_UsesDefaultSystemTemplate()
    {
        var runId = "wr_issue_default";
        await SeedAsync(projectId: "proj-issue-default", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local");

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/local", result.Id);
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:open-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_ProjectDefaultIsDifferent_UsesIssueProfile()
    {
        var runId = "wr_issue_pr_proj_default";
        await SeedAsync(projectId: "proj-issue-pr-proj-default", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("mohist/github-pr", result.Id);
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_CustomYamlOverride_TakesPrecedence()
    {
        var runId = "wr_issue_pr_custom";
        await SeedAsync(projectId: "proj-pr-custom", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("custom-override", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await Manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal("custom-override", result.Id);
        Assert.Single(result.Structure.Stages);
    }

}
