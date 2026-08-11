using Mohist.Server.Infrastructure.Events;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
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
        Assert.Equal(before + 1, (await EventStore.ListAsync(_workflowId!)).Count);

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

    [Fact]
    public async Task ReminderTick_UsesTheFixedDeadlineAndBlocksWithoutFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-2", "turn-2", "opencode", "runtime-session-2");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

        var unknown = await LoadRunAsync(_workflowId!);
        var settlement = Assert.IsType<AgentResultSettlement>(Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement);
        var deadline = Assert.IsType<DateTimeOffset>(settlement.DeadlineAt);
        Assert.Equal(settlement.FirstUnknownAt!.Value.AddMinutes(5), deadline);

        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        Assert.Equal(AgentResultSettlementState.Unknown,
            Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks).AgentResultSettlement!.State);

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        Assert.Equal(TaskRunStatus.Running, blockedTask.Status);
        Assert.Equal(WorkflowRunStatus.Running, blocked.Status);
        Assert.Null(blocked.Failure);
        Assert.Null(blocked.CurrentStage().Failure);
        var eventTypes = (await EventStore.ListAsync(_workflowId!)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.StageBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunBlocked, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);

        var report = new TaskReport(
            work.WorkId,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null);
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(runnerId, work.WorkId, report));
        Assert.Equal(ReportAck.Stale, await workflow.ReceiveTaskReportAsync(runnerId, work.WorkId, report));

        var completed = await LoadRunAsync(_workflowId!);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task UnknownSettlement_DeletesItsSnapshotAndFencesDispatchAndControl()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition("agent", "Agent", "mohist/opencode"),
                new TaskDefinition("after", "After", "spec/task")
            ],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks, candidate => candidate.Id == "agent.1");
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-3", "turn-3", "opencode", "runtime-session-3");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected");
        var snapshots = Services.GetRequiredService<IDispatchSnapshotStore>();
        Assert.NotNull(await snapshots.LoadJsonAsync(_workflowId!, work.WorkId));

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));
        Assert.Null(await snapshots.LoadJsonAsync(_workflowId!, work.WorkId));

        var unresolved = await LoadRunAsync(_workflowId!);
        Assert.True(unresolved.HasUnresolvedAgentResult());
        Assert.Null(unresolved.NextWork());
        Assert.Null(unresolved.CurrentPendingWork());
        Assert.Null(await workflow.ClaimNextAsync(runnerId));
        Assert.Empty((await Services.GetRequiredService<DispatchService>()
            .PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        var pendingWorkflowId = $"{_workflowId}-pending";
        var projectId = TestProjectId(_workflowId!);
        var pendingWorkflow = Grains.GetGrain<IWorkflowGrain>(pendingWorkflowId);
        await SeedWorkflowTemplateAsync(pendingWorkflowId, SingleStage(checks: []), projectId);
        await pendingWorkflow.StartAsync(TestInput(projectId));
        var workKey = $"{WorkDispatchOwnerKinds.Workflow}:{_workflowId}:{work.WorkId}";
        var dispatch = Services.GetRequiredService<DispatchService>();
        Assert.Empty((await dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([workKey], []))).Dispatches);
        Assert.Empty((await dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [workKey]))).Dispatches);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RetryAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RerunAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.AddTaskAsync(
            new RuntimeTaskInput("runtime", "Runtime", "spec/task")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.AddTasksAsync(
            new AddTasksBatchRequest([new AddTasksBatchItem("batch", "Batch", "spec/task")])));
        var rerun = await workflow.RerunFromStageAsync("build");
        Assert.False(rerun.Success);
        Assert.Equal("agent_result_unresolved", rerun.Code);
    }

    [Fact]
    public async Task ExplicitStop_CancelsTheUnresolvedAttemptThenMakesLaterReportsAndObservationsStale()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-4", "turn-4", "pi", "runtime-session-4");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.TargetMissing, "target-missing");
        var snapshots = Services.GetRequiredService<IDispatchSnapshotStore>();

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));
        await workflow.StopAsync("operator confirmed stop");
        await workflow.StopAsync("cleanup replay");

        var stopped = await LoadRunAsync(_workflowId!);
        var cancelled = Assert.Single(stopped.CurrentStage().Tasks);
        Assert.Equal(WorkflowRunStatus.Stopped, stopped.Status);
        Assert.Equal(TaskRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(work.WorkId, cancelled.WorkId);
        Assert.Equal(runnerId, cancelled.WorkerId);
        Assert.NotNull(cancelled.AgentResultSettlement);
        Assert.Null(stopped.Failure);
        Assert.Null(stopped.CurrentStage().Failure);
        Assert.Null(await snapshots.LoadJsonAsync(_workflowId!, work.WorkId));
        Assert.Equal(ReportAck.Stale, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(work.WorkId, TaskReportStatus.Succeeded, Output: null, Artifacts: null)));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(observation));
        var eventTypes = (await EventStore.ListAsync(_workflowId!)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskCancelled, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunStopped, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
    }

    [Fact]
    public async Task UnknownAndBlockedSettlement_HoldsSequentialStageLockUntilExplicitStop()
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

        Assert.Equal(AgentResultSettlementState.Blocked,
            Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks).AgentResultSettlement!.State);
        Assert.Equal(_workflowId, (await lockGrain.GetStateAsync())?.Owner?.WorkflowRunId);

        await workflow.StopAsync("operator stop");

        Assert.Null((await lockGrain.GetStateAsync())?.Owner);
    }
}
