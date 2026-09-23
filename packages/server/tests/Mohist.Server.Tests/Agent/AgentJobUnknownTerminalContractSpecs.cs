using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Workflow.Subscriptions;
using Xunit;

namespace Mohist.Server.Tests.Agent;

[Trait("level", "L0")]
public sealed class AgentJobUnknownTerminalContractSpecs
{
    [Fact]
    public void WorkflowOwner_AcceptsLifecycleFinalUnknownAsAnUnsuccessfulResult()
    {
        var delivery = new AgentJobWorkflowTerminalEventData(
            "job-1",
            "invocation-1",
            "command-1",
            "workflow-1",
            "attempt-1",
            "work-1",
            "build",
            "fingerprint-1",
            "unknown",
            "The execution outcome is unknown.",
            null,
            null,
            "activity-converged:idle",
            "unknown",
            null,
            null,
            "session-1",
            "input-1",
            "turn-1",
            null);

        delivery.Validate();
        Assert.NotEqual("completed", delivery.Status);
        Assert.Equal("unknown", delivery.FailureCategory);
    }

    [Fact]
    public void AttachedSubagent_AcceptsLifecycleFinalUnknownWithoutInventingAnOutcome()
    {
        var delivery = new SubagentTerminalEventData(
            "child-job-1",
            "child-session-1",
            "parent-session-1",
            "parent-agent-1",
            "edge-1",
            "turn-1",
            "unknown",
            "agent-job:child-job-1");

        delivery.Validate();
        Assert.Equal("unknown", delivery.Status);
        Assert.Equal("agent-job:child-job-1", delivery.ResultReference);
    }
}
