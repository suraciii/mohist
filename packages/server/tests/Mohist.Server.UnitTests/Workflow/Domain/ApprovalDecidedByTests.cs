using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

/// <summary>
/// issue-491 T-002: declaration-model validation for the approval operator.
/// Threading is mirrored across <see cref="WorkflowRunExtensions"/> so the
/// spec scenario "the resulting approval decision SHALL carry that author as
/// decidedBy" holds at every layer.
/// </summary>
public class ApprovalDecidedByTests
{
    private const string Operator = "supervisor";

    private static WorkflowRun BuildAwaitingApprovalRun()
    {
        var run = WorkflowRun.Create("wf-1", ApprovalStageDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        run.StartTask("draft.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        return run;
    }

    private static WorkflowDefinition ApprovalStageDefinition() =>
        new([
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true)
        ]);

    [Fact]
    public void Approve_StampsDecidedByInApprovalStatus()
    {
        var run = BuildAwaitingApprovalRun();

        run.Approve(DateTimeOffset.UnixEpoch, Operator);

        var current = run.CurrentStage();
        Assert.NotNull(current.ApprovalStatus);
        Assert.Equal("approved", current.ApprovalStatus!.Result);
        Assert.Equal(Operator, current.ApprovalStatus.DecidedBy);
    }

    [Fact]
    public void Approve_EmitsStageApprovalResolved_WithDecidedBy()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.Approve(DateTimeOffset.UnixEpoch, Operator);

        var resolved = events
            .Select(WorkflowEventSerializer.Unwrap)
            .OfType<StageApprovalResolved>()
            .Single();
        Assert.Equal(Operator, resolved.DecidedBy);
    }

    [Fact]
    public void Reject_StampsDecidedByInApprovalStatus()
    {
        var run = BuildAwaitingApprovalRun();

        run.Reject("needs more detail", DateTimeOffset.UnixEpoch, Operator);

        var current = run.CurrentStage();
        Assert.NotNull(current.ApprovalStatus);
        Assert.Equal("rejected", current.ApprovalStatus!.Result);
        Assert.Equal(Operator, current.ApprovalStatus.DecidedBy);
    }

    [Fact]
    public void Reject_EmitsStageApprovalResolved_WithDecidedBy()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.Reject("not enough detail", DateTimeOffset.UnixEpoch, Operator);

        var resolved = events
            .Select(WorkflowEventSerializer.Unwrap)
            .OfType<StageApprovalResolved>()
            .Single();
        Assert.Equal(Operator, resolved.DecidedBy);
        Assert.Equal(ApprovalResult.Rejected, resolved.Result);
    }

    [Fact]
    public void RequestChanges_StampsDecidedByInApprovalStatus()
    {
        var run = BuildAwaitingApprovalRun();

        run.RequestChanges("needs more detail", "fb_1", DateTimeOffset.UnixEpoch, Operator);

        var current = run.CurrentStage();
        Assert.NotNull(current.ApprovalStatus);
        Assert.Equal(Operator, current.ApprovalStatus!.DecidedBy);
        Assert.Null(current.ApprovalStatus.Result);
    }

    [Fact]
    public void RequestChanges_EmitsStageApprovalResolved_WithDecidedBy()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.RequestChanges("needs more detail", "fb_1", DateTimeOffset.UnixEpoch, Operator);

        var resolved = events
            .Select(WorkflowEventSerializer.Unwrap)
            .OfType<StageApprovalResolved>()
            .Single();
        Assert.Equal(Operator, resolved.DecidedBy);
        Assert.Equal(ApprovalResult.Rejected, resolved.Result);
    }

    [Fact]
    public void RequestChanges_EmitsFeedbackRequested_AlongsideStageApprovalResolved()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.RequestChanges("needs more detail", "fb_1", DateTimeOffset.UnixEpoch, Operator);

        var unwrapped = events.Select(WorkflowEventSerializer.Unwrap).ToList();
        Assert.Single(unwrapped.OfType<FeedbackRequested>());
        Assert.Single(unwrapped.OfType<StageApprovalResolved>());
    }
}
