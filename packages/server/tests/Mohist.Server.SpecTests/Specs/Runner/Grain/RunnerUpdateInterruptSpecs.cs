using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public sealed class RunnerUpdateInterruptSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public RunnerUpdateInterruptSpecs(Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture)
        : base(fixture) { }

    [Fact]
    public async Task UpdateInterrupt_FencesActiveWorkflowAgentAndDisconnectDoesNotUnfenceIt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks);
        var operationId = $"runner-update:{Guid.NewGuid():N}";

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(
            new AgentExecutionBinding(
                task.Id,
                work.WorkId,
                runnerId,
                "session-update",
                "turn-update",
                "opencode",
                "runtime-update")));

        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            task.Id,
            work.WorkId,
            runnerId,
            operationId));
        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            task.Id,
            work.WorkId,
            runnerId,
            operationId));

        var marked = await LoadRunAsync(_workflowId!);
        var settlement = Assert.Single(marked.CurrentStage().Tasks).AgentResultSettlement;
        Assert.NotNull(settlement);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted, settlement!.State);
        Assert.Equal(operationId, settlement.UpdateOperationId);
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentRunnerDisconnectedAsync(runnerId));
        Assert.Equal(
            AgentResultSettlementState.RecoverablyInterrupted,
            (await LoadRunAsync(_workflowId!)).CurrentStage().Tasks.Single().AgentResultSettlement!.State);
        Assert.Contains(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.AgentTaskUpdateInterrupted);
    }

    [Fact]
    public async Task UpdateInterruptedWorkWithoutReceipt_BlocksAtTheSettlementDeadline()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks);

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(
            new AgentExecutionBinding(
                task.Id,
                work.WorkId,
                runnerId,
                "session-deadline",
                "turn-deadline",
                "opencode",
                "runtime-deadline")));
        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            task.Id,
            work.WorkId,
            runnerId,
            $"runner-update:{Guid.NewGuid():N}"));

        var marked = await LoadRunAsync(_workflowId!);
        var settlement = Assert.Single(marked.CurrentStage().Tasks).AgentResultSettlement!;
        var deadline = Assert.IsType<DateTimeOffset>(settlement.DeadlineAt);
        Assert.True(marked.HasUnresolvedAgentResult());

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        Assert.True(blocked.HasBlockedAgentResult());
        Assert.Equal(TaskRunStatus.Running, blockedTask.Status);
    }

    [Fact]
    public async Task BeginUpdateInterrupt_PreservesActiveWorkAndClosesAdmissionIdempotently()
    {
        await ClearBacklogAsync();
        var projectId = "runner-update-interrupt-project-567";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            "runner-update-interrupt-567",
            maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var activeJobId = "runner-update-interrupt-active-567";
        var pendingJobId = "runner-update-interrupt-pending-567";
        var activeJob = Grains.GetGrain<IAgentJobGrain>(activeJobId);
        var pendingJob = Grains.GetGrain<IAgentJobGrain>(pendingJobId);

        try
        {
            await activeJob.SubmitAsync(new AgentJobInput(
                "run until interrupted",
                WorkspacePath: "spec-workspace",
                ProjectId: projectId,
                AgentId: "agent-test",
                PinnedRunnerId: runnerId));
            var claim = await runner.TryClaimAgentJobAsync(activeJobId, projectId);
            Assert.NotNull(claim);
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());

            await pendingJob.SubmitAsync(new AgentJobInput(
                "wait for admission",
                WorkspacePath: "spec-workspace",
                ProjectId: projectId,
                AgentId: "agent-test",
                PinnedRunnerId: runnerId));
            Assert.Equal(AgentJobStatus.Pending, await pendingJob.GetStatusAsync());

            var first = await runner.BeginUpdateInterruptAsync();

            Assert.NotNull(first);
            Assert.Equal(RunnerStatus.Online, first!.Status);
            Assert.True(first.Draining);
            var active = Assert.Single(first.ActiveWorks);
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, active.OwnerKind);
            Assert.Equal(activeJobId, active.OwnerId);
            Assert.Equal(claim!.WorkId, active.WorkId);
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());

            var repeated = await runner.BeginUpdateInterruptAsync();

            Assert.NotNull(repeated);
            Assert.True(repeated!.Draining);
            Assert.Equal(first.Status, repeated.Status);
            Assert.Equal(first.LastHeartbeatAt, repeated.LastHeartbeatAt);
            Assert.Equal(
                first.ActiveWorks.Select(work => (work.OwnerKind, work.OwnerId, work.WorkId)),
                repeated.ActiveWorks.Select(work => (work.OwnerKind, work.OwnerId, work.WorkId)));

            Assert.False((await runner.TryBeginPollAsync()).Admitted);
            Assert.Null(await runner.TryClaimAgentJobAsync(pendingJobId, projectId));
            Assert.Equal(AgentJobStatus.Pending, await pendingJob.GetStatusAsync());
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());
        }
        finally
        {
            await pendingJob.CancelAsync();
            await activeJob.CancelAsync();
            await runner.UnregisterAsync();
        }
    }

    [Fact]
    public async Task UpdateInterruptCancel_ReleasesOnlyTheMatchingPersistedFence()
    {
        await ClearBacklogAsync();
        var projectId = "runner-update-interrupt-rollback-project-567";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            "runner-update-interrupt-rollback-567");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        const string firstId = "00000000-0000-0000-0000-000000000001";
        const string successorId = "00000000-0000-0000-0000-000000000002";
        var registeredInfo = await runner.GetInfoAsync();
        Assert.NotNull(registeredInfo);

        try
        {
            var first = await runner.BeginUpdateInterruptAsync(firstId);
            var repeated = await runner.BeginUpdateInterruptAsync(firstId);

            Assert.NotNull(first);
            Assert.NotNull(repeated);
            Assert.True(first!.Draining);
            Assert.Equal("00000000000000000000000000000001", first.UpdateInterruptId);
            Assert.Equal(first.UpdateInterruptId, repeated!.UpdateInterruptId);
            Assert.False((await runner.TryBeginPollAsync()).Admitted);

            // A generic drain caller cannot accidentally release an update
            // fence that it did not acquire.
            await runner.CancelDrainAsync();
            Assert.True((await runner.GetRuntimeStateAsync()).Draining);

            var cancelled = await runner.CancelUpdateInterruptAsync(firstId);
            var afterCancellation = await runner.GetRuntimeStateAsync();
            var repeatedCancellation = await runner.CancelUpdateInterruptAsync(firstId);

            Assert.Equal(RunnerUpdateInterruptCancelStatus.Cancelled, cancelled.Status);
            Assert.False(afterCancellation.Draining);
            Assert.Null(afterCancellation.UpdateInterruptId);
            Assert.Equal(RunnerUpdateInterruptCancelStatus.AlreadyCancelled, repeatedCancellation.Status);

            var delayedBegin = await runner.BeginUpdateInterruptAsync(firstId);
            Assert.NotNull(delayedBegin);
            Assert.False(delayedBegin!.Draining);
            Assert.Null(delayedBegin.UpdateInterruptId);

            var successor = await runner.BeginUpdateInterruptAsync(successorId);
            var staleCancellation = await runner.CancelUpdateInterruptAsync(firstId);
            var afterStaleCancellation = await runner.GetRuntimeStateAsync();

            Assert.NotNull(successor);
            Assert.True(successor!.Draining);
            Assert.Equal("00000000000000000000000000000002", successor.UpdateInterruptId);
            Assert.Equal(RunnerUpdateInterruptCancelStatus.Superseded, staleCancellation.Status);
            Assert.True(afterStaleCancellation.Draining);
            Assert.Equal(successor.UpdateInterruptId, afterStaleCancellation.UpdateInterruptId);

            await TestLifecycle.Deactivate(runner);
            await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
            runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var afterReactivation = await runner.GetRuntimeStateAsync();
            Assert.True(afterReactivation.Draining);
            Assert.Equal(successor.UpdateInterruptId, afterReactivation.UpdateInterruptId);

            await runner.RegisterAsync(registeredInfo!);

            var afterRegistration = await runner.GetRuntimeStateAsync();
            Assert.False(afterRegistration.Draining);
            Assert.Null(afterRegistration.UpdateInterruptId);
        }
        finally
        {
            await runner.UnregisterAsync();
        }
    }
}
