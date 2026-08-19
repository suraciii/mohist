using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public sealed partial class AgentResultSettlementSpecs
{
    [Fact]
    public async Task UnknownSettlement_HoldsSequentialStageLockUntilTheDeadlineReleasesIt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var resource = $"agent-settlement-{suffix}";
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new TaskDefinition("agent", "Agent", "mohist/opencode")],
                [],
                LockBehavior: "sequential",
                Resources: [resource])
        ]), id: $"wf-agent-settlement-lock-{suffix}");
        var projectId = TestProjectId(_workflowId!);
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id,
            work.WorkId,
            runnerId,
            "session-lock",
            "turn-lock",
            "opencode",
            "runtime-session-lock");
        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(
            WorkflowStageLockKeys.ForProjectResource(projectId, resource));

        Assert.Equal(_workflowId, (await lockGrain.GetStateAsync())?.Owner?.WorkflowRunId);
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(
                binding,
                AgentExecutionObservationKind.Disconnected,
                "runner-disconnected")));
        Assert.Equal(_workflowId, (await lockGrain.GetStateAsync())?.Owner?.WorkflowRunId);

        var unknown = await LoadRunAsync(_workflowId!);
        var deadline = Assert.IsType<DateTimeOffset>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        Assert.Null(blocked.Assignment);
        Assert.Equal(work.WorkId, blockedTask.WorkId);
        Assert.Equal(runnerId, blockedTask.WorkerId);
        Assert.Null((await lockGrain.GetStateAsync())?.Owner);

        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        Assert.Null((await lockGrain.GetStateAsync())?.Owner);
        Assert.Null((await LoadRunAsync(_workflowId!)).Assignment);

        await workflow.StopAsync("operator stop");

        Assert.Null((await lockGrain.GetStateAsync())?.Owner);
    }
}
