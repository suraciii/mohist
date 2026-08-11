using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class AgentResultSettlementSpecs : WorkflowGrainSpecs
{
    public AgentResultSettlementSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task BoundObservationReplayPreservesTaskOutcomeUntilAnAuthoritativeReport()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id,
            work.WorkId,
            runnerId,
            "session-1",
            "turn-1",
            "opencode",
            "runtime-session-1");
        var observation = new AgentExecutionObservation(
            binding,
            AgentExecutionObservationKind.StopUnconfirmed,
            "stop-unconfirmed",
            "transport did not confirm stop",
            "stop-1");
        var before = (await EventStore.ListAsync(_workflowId!)).Count;

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Stale, await workflow.BindAgentExecutionAsync(binding with { AgentSessionId = "other-session" }));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(
            observation with { Binding = binding with { AgentTurnId = "other-turn" } }));

        var unresolved = await LoadRunAsync(_workflowId!);
        var settlement = Assert.IsType<AgentResultSettlement>(Assert.Single(unresolved.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.StopUnconfirmed, settlement.LastObservation);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(unresolved.CurrentStage().Tasks).Status);
        Assert.Equal(WorkflowRunStatus.Running, unresolved.Status);
        Assert.Null(unresolved.Failure);
        Assert.True(unresolved.HasUnresolvedAgentResult());
        Assert.Equal(before, (await EventStore.ListAsync(_workflowId!)).Count);

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(work.WorkId, TaskReportStatus.Succeeded, Output: null, Artifacts: null)));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(observation));

        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.False(completed.HasUnresolvedAgentResult());
        Assert.Contains(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
    }
}
