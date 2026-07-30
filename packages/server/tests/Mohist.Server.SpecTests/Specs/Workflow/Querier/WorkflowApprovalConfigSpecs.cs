using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowApprovalConfigSpecs : WorkflowDefinitionResolverTestFactory
{
    [Fact]
    public async Task LoadApprovalConfigAsync_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_approval";
        await SeedAsync(projectId: "proj-existing-disabled-approval", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var approval = await DefinitionResolver.LoadApprovalConfigAsync(runId);

        var task = Assert.Single(approval!.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsConfiguredFeedbackTask_WhenDefined()
    {
        var runId = "wr_approval_defined";
        var feedbackConfig = new ApprovalFeedbackConfig(
            Tasks: [new TaskDefinition(
                Id: "apply-feedback",
                Title: "Apply Feedback",
                Uses: "spec/task",
                With: null)]);
        var approval = new ApprovalConfig(Feedback: feedbackConfig);
        var def = new WorkflowDefinition(
            new List<StageDefinition>
            {
                new("plan",
                    new List<TaskDefinition>(),
                    new List<CheckDefinition>(),
                    RequiresApproval: true),
            },
            Approval: approval);
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(def);

        await SeedProjectTemplateAsync("approval_proj", runId, "approval-template", templateJson);

        var loaded = await DefinitionResolver.LoadApprovalConfigAsync(runId);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Feedback);
        var task = Assert.Single(loaded.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("spec/task", task.Uses);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsNull_WhenNoApprovalConfig()
    {
        var runId = "wr_approval_null";
        var templateJson = SerializeDefinitionWithStages("no-approval-template",
            ("plan", Array.Empty<TaskDefinition>(), Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("no_approval_proj", runId, "no-approval-template", templateJson);

        var loaded = await DefinitionResolver.LoadApprovalConfigAsync(runId);

        Assert.Null(loaded);
    }

    // --- helpers ---

}
