using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class WorkflowControlGuardTests
{
    public static TheoryData<string?, WorkflowControlAction, bool> Decisions => new()
    {
        { null, WorkflowControlAction.ActiveOnly, false },
        { null, WorkflowControlAction.RetryOrRerun, false },
        { null, WorkflowControlAction.Stop, false },
        { "stopped", WorkflowControlAction.ActiveOnly, false },
        { "stopped", WorkflowControlAction.RetryOrRerun, false },
        { "stopped", WorkflowControlAction.Stop, false },
        { "completed", WorkflowControlAction.ActiveOnly, false },
        { "completed", WorkflowControlAction.RetryOrRerun, false },
        { "completed", WorkflowControlAction.Stop, false },
        { "failed", WorkflowControlAction.ActiveOnly, false },
        { "failed", WorkflowControlAction.RetryOrRerun, true },
        { "failed", WorkflowControlAction.Stop, true },
        { "blocked", WorkflowControlAction.ActiveOnly, false },
        { "blocked", WorkflowControlAction.RetryOrRerun, false },
        { "blocked", WorkflowControlAction.Stop, true },
        { "pending", WorkflowControlAction.ActiveOnly, true },
        { "ready", WorkflowControlAction.RetryOrRerun, true },
        { "running", WorkflowControlAction.Stop, true },
        { "paused", WorkflowControlAction.ActiveOnly, true },
        { "awaiting-approval", WorkflowControlAction.ActiveOnly, true },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void IsWorkflowControllableForAction_MapsStatusAndAction(
        string? status,
        WorkflowControlAction action,
        bool expected)
    {
        Assert.Equal(expected, WorkflowControlGuard.IsWorkflowControllableForAction(status, action));
    }
}
