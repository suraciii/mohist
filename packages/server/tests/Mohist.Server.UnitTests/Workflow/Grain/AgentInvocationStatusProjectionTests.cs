using Mohist.Server.Agent.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public sealed class AgentInvocationStatusProjectionTests
{
    [Theory]
    [InlineData(AgentJobStatus.Pending, "queued")]
    [InlineData(AgentJobStatus.Running, "executing")]
    [InlineData(AgentJobStatus.Unknown, "executing")]
    [InlineData(AgentJobStatus.Completed, "completed")]
    [InlineData(AgentJobStatus.Failed, "failed")]
    [InlineData(AgentJobStatus.Cancelled, "cancelled")]
    public void Map_UsesAgentJobLifecycle(AgentJobStatus jobStatus, string expected)
    {
        Assert.Equal(expected, AgentInvocationStatusProjection.Map(jobStatus));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Map_FailedTerminalWithRecoveryDecision_IsRecovering(bool pending, bool applying)
    {
        Assert.Equal(
            "recovering",
            AgentInvocationStatusProjection.Map(AgentJobStatus.Failed, pending, applying));
    }

    [Fact]
    public void Map_NonFailedTerminalIgnoresRecoveryFlags()
    {
        Assert.Equal("completed", AgentInvocationStatusProjection.Map(AgentJobStatus.Completed, true, true));
        Assert.Equal("cancelled", AgentInvocationStatusProjection.Map(AgentJobStatus.Cancelled, true, true));
    }

    [Fact]
    public void HasPendingRecoveryDecision_UsesUnsettledFailedReceiptAndBudget()
    {
        var task = new TaskRun
        {
            Id = "task.1",
            DefinitionId = "task",
            Attempt = 1,
            Title = "Task",
            Status = TaskRunStatus.Running,
            Recovery = new RecoveryDefinition(
                Budget: 1,
                Handlers: [new RecoveryHandlerDefinition("error.code=x", [], RetrySelf: false)])
        };
        task.AgentInvocationSettlement = new AgentInvocationSettlement
        {
            Terminal = new AgentInvocationTerminal(
                "delivery",
                "invocation",
                "project",
                "workflow",
                task.Id,
                "work",
                "job",
                "session",
                AgentInvocationTerminalStatus.Failed,
                "failed",
                "error",
                "error",
                1,
                null,
                null,
                TestTime.UtcNow),
            ReceivedAt = TestTime.UtcNow,
        };

        Assert.True(AgentInvocationStatusProjection.HasPendingRecoveryDecision(task));

        task.AgentInvocationSettlement.SettlementApplied = true;
        task.AgentInvocationSettlement.OutcomeApplied = true;
        task.AgentInvocationSettlement.AdvancementApplied = true;
        task.AgentInvocationSettlement.ArtifactsBound = true;
        task.AgentInvocationSettlement.SetVarsApplied = true;
        Assert.False(AgentInvocationStatusProjection.HasPendingRecoveryDecision(task));
    }
}
