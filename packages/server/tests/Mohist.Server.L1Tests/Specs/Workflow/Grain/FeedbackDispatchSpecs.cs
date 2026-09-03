using Mohist.Server.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;

namespace Mohist.Server.L1Tests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
[Trait("level", "L1")]
public class FeedbackDispatchSpecs : WorkflowGrainSpecs
{
    public FeedbackDispatchSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AwaitingApproval_RequestChanges_DeactivateThenDispatch_StillCarriesFeedbackContext()
    {
        var workflow = await StartWorkflowAsync(ApprovalStageWithFeedback());

        var (draft, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, draft.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var feedbackId = await workflow.RequestChangesAsync("add a quick start section", "operator-1");

        // Force the workflow grain to deactivate so the next dispatch
        // rehydrates the workflow run from the JSON-serialized state.
        // This exercises the CausedByFeedbackId round-trip path.
        var grain = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await TestLifecycle.Deactivate(grain);

        // Re-register the runner so the next poll picks up the
        // rehydrated apply-feedback task.
        await RegisterRunnerForProjectAsync(TestProjectId(_workflowId!), _runnerId);

        var (feedbackTask, _) = await PollWorkAnyAsync();
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, feedbackTask.OwnerKind);
        Assert.StartsWith("apply-feedback.", feedbackTask.ActionAttemptId);

        using var doc = JsonDocument.Parse(feedbackTask.Variables!);
        Assert.True(
            doc.RootElement.GetProperty("work").TryGetProperty("approvalFeedback", out var feedbackEl),
            "work.approvalFeedback object must still be present after workflow reactivation");
        Assert.Equal(feedbackId, feedbackEl.GetProperty("id").GetString());
    }

    private static WorkflowDefinition ApprovalStageWithFeedback() =>
        ApprovalStage() with
        {
            Approval = new ApprovalConfig(new ApprovalFeedbackConfig([
                new TaskDefinition(
                    "apply-feedback",
                    "Apply approval feedback",
                    "mohist/agent",
                    new Dictionary<string, JsonElement?>
                    {
                        ["name"] = JsonSerializer.SerializeToElement("mohist/builder"),
                        ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
                    })
            ]))
        };
}
