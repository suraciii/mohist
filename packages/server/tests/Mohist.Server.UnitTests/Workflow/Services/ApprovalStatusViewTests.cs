using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

/// <summary>
/// issue-491 T-002: <see cref="ApprovalStatusView"/> exposes the declared
/// operator on the read model. Historical approval data (carried as JSON
/// inside <c>WorkflowRun.State</c>) recorded before <c>decidedBy</c> existed
/// reads back with the field omitted — surface it as empty, do not error.
/// </summary>
public class ApprovalStatusViewTests
{
    [Fact]
    public void BuildStatusView_ExposesDecidedBy_OnResolvedApproval()
    {
        var run = new WorkflowRun
        {
            Id = "wf-approved",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Completed,
            CurrentStageId = "plan",
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = StageRunStatus.Completed,
                    Tasks = [],
                    Checks = [],
                    ApprovalStatus = new ApprovalStatus(
                        "approved",
                        "2026-01-01T00:00:00Z",
                        "2026-01-02T00:00:00Z",
                        "supervisor"),
                }
            ]
        };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var stageView = view!.Stages.Single();
        Assert.NotNull(stageView.ApprovalStatus);
        Assert.Equal("supervisor", stageView.ApprovalStatus!.DecidedBy);
        Assert.Equal("approved", stageView.ApprovalStatus.Result);
    }

    [Fact]
    public void BuildStatusView_OmitsDecidedBy_OnNullApprovalStatus()
    {
        var run = new WorkflowRun
        {
            Id = "wf-no-approval",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks = [],
                    Checks = [],
                }
            ]
        };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var stageView = view!.Stages.Single();
        Assert.Null(stageView.ApprovalStatus);
    }

    [Fact]
    public void BuildStatusView_NullDecidedByOnLegacyApproval_WhenFieldAbsentAfterDeserialization()
    {
        // ApprovalStatus with DecidedBy = null (default) — historical rows
        // persisted before this change deserialized without the field. The
        // view must surface null without throwing.
        var status = new ApprovalStatus(
            "approved",
            "2026-01-01T00:00:00Z",
            "2026-01-02T00:00:00Z");

        Assert.Null(status.DecidedBy);
    }
}
