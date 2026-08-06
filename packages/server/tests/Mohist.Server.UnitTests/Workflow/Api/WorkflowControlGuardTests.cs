using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Api;

public class WorkflowControlGuardTests
{
    public static TheoryData<string> ActiveStatuses => new()
    {
        "created",
        "pending",
        "ready",
        "running",
        "awaiting-approval",
        "paused",
    };

    public static TheoryData<string> TerminalStatuses => new()
    {
        "stopped",
        "completed",
    };

    [Theory]
    [MemberData(nameof(ActiveStatuses))]
    public void ActiveOrAwaitingStatus_AdmitsEveryControlAction(string status)
    {
        Assert.True(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.ActiveOnly));
        Assert.True(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.RetryOrRerun));
        Assert.True(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.Stop));
    }

    [Theory]
    [MemberData(nameof(TerminalStatuses))]
    public void StoppedOrCompletedStatus_RejectsEveryControlAction(string status)
    {
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.ActiveOnly));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.RetryOrRerun));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(status, WorkflowControlAction.Stop));
    }

    [Theory]
    [InlineData(WorkflowControlAction.ActiveOnly, false)]
    [InlineData(WorkflowControlAction.RetryOrRerun, true)]
    [InlineData(WorkflowControlAction.Stop, true)]
    public void FailedStatus_AdmitsOnlyRetryRerunAndStop(WorkflowControlAction action, bool expected)
    {
        Assert.Equal(expected, WorkflowControlGuard.IsWorkflowControllableForAction("failed", action));
    }

    [Fact]
    public void NullStatus_RejectsEveryControlAction()
    {
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(null, WorkflowControlAction.ActiveOnly));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(null, WorkflowControlAction.RetryOrRerun));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction(null, WorkflowControlAction.Stop));
    }

    [Theory]
    [InlineData(WorkflowControlAction.ActiveOnly)]
    [InlineData(WorkflowControlAction.RetryOrRerun)]
    [InlineData(WorkflowControlAction.Stop)]
    public void UnknownStatus_FallsThroughToAdmit(WorkflowControlAction action)
    {
        Assert.True(WorkflowControlGuard.IsWorkflowControllableForAction("not-a-real-status", action));
    }
}
