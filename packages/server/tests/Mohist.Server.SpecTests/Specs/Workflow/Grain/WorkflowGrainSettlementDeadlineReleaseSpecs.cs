using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// The unknown-result deadline is a liveness boundary: it releases the attempt's
/// active-work lease without inferring an outcome. These specs drive fake time
/// exactly onto the persisted deadline and inject cleanup failures around the
/// durable release save.
/// </summary>
public sealed partial class WorkflowGrainStateSaveFailureSpecs
{
    [Fact]
    public async Task SettlementDeadline_ReleasesTheAssignmentOnceAndKeepsTheAttemptAddressable()
    {
        const string workflowRunId = "wr-settlement-deadline-release";
        const string projectId = "proj-settlement-deadline-release";
        const string workerId = "worker-settlement-deadline-release";
        var calls = new ReminderCalls();

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var grain = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await grain.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(grain, store, workflowRunId, projectId, workerId);
        await grain.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed", "no stop receipt", "stop-op"));
        await snapshots.SaveFirstJsonAsync(workflowRunId, binding.WorkId, "{}");

        var unknown = Assert.IsType<WorkflowRun>(await store.LoadAsync(workflowRunId));
        var unknownSettlement = SettlementOf(unknown);
        var deadline = Assert.IsType<DateTimeOffset>(unknownSettlement.DeadlineAt);
        var identity = Identity(unknownSettlement);
        Assert.Equal(workerId, unknown.AssignedTo);

        // One tick strictly before the deadline must not release anything.
        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow() - TimeSpan.FromTicks(1));
        await grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        Assert.Equal(AgentResultSettlementState.Unknown, SettlementOf(await store.LoadAsync(workflowRunId)).State);
        Assert.Equal(workerId, (await store.LoadAsync(workflowRunId))!.AssignedTo);

        TimeProvider.Advance(TimeSpan.FromTicks(1));
        await grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        await AssertBlockedAndReleasedAsync(store, workflowRunId, workerId, identity, deadline);
        Assert.Null(await grain.GetAssignedWorkerIdAsync());
        Assert.Null(await snapshots.LoadJsonAsync(workflowRunId, binding.WorkId));
        Assert.Equal(1, calls.RemoveAttempts);
        Assert.Equal(1, calls.LockReleaseAttempts);
        var blockedEvents = await BlockedEventTypesAsync(events, workflowRunId);
        Assert.Equal(
            [
                EventCatalog.ReverseDns.TaskBlocked,
                EventCatalog.ReverseDns.StageBlocked,
                EventCatalog.ReverseDns.WorkflowRunBlocked,
            ],
            blockedEvents);

        // Replayed reminder delivery and a fresh activation converge without a
        // second transition, replacement work, or renewed ownership.
        await grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        TimeProvider.Advance(TimeSpan.FromHours(1));
        await grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        var reactivated = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await reactivated.OnActivateAsync(CancellationToken.None);

        await AssertBlockedAndReleasedAsync(store, workflowRunId, workerId, identity, deadline);
        Assert.Null(await reactivated.GetAssignedWorkerIdAsync());
        Assert.Equal(blockedEvents, await BlockedEventTypesAsync(events, workflowRunId));
        Assert.Equal(4, calls.RemoveAttempts);
        Assert.Equal(4, calls.LockReleaseAttempts);
    }

    [Theory]
    [InlineData("reminder")]
    [InlineData("stage-lock")]
    public async Task SettlementDeadline_CleanupFailureKeepsTheAttemptReleasedAndConverges(string boundary)
    {
        var workflowRunId = $"wr-settlement-deadline-cleanup-{boundary}";
        var projectId = $"proj-settlement-deadline-cleanup-{boundary}";
        var workerId = $"worker-settlement-deadline-cleanup-{boundary}";
        var calls = new ReminderCalls();

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var grain = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await grain.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(grain, store, workflowRunId, projectId, workerId);
        await grain.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected"));

        var unknown = Assert.IsType<WorkflowRun>(await store.LoadAsync(workflowRunId));
        var identity = Identity(SettlementOf(unknown));
        var deadline = Assert.IsType<DateTimeOffset>(SettlementOf(unknown).DeadlineAt);
        FailNextCleanupBoundary(calls, boundary);

        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow());
        await grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        // The durable boundary survives a failing cleanup step: ownership stays
        // released and the reminder is only dropped once cleanup succeeded.
        await AssertBlockedAndReleasedAsync(store, workflowRunId, workerId, identity, deadline);
        var blockedEvents = await BlockedEventTypesAsync(events, workflowRunId);
        Assert.Equal(3, blockedEvents.Length);
        if (boundary == "stage-lock")
            Assert.Equal(0, calls.RemoveAttempts);

        var recovered = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await recovered.OnActivateAsync(CancellationToken.None);

        await AssertBlockedAndReleasedAsync(store, workflowRunId, workerId, identity, deadline);
        Assert.Null(await recovered.GetAssignedWorkerIdAsync());
        Assert.Equal(blockedEvents, await BlockedEventTypesAsync(events, workflowRunId));
        Assert.True(calls.RemoveAttempts >= 1);
        Assert.True(calls.LockReleaseAttempts >= 2);
    }

    [Fact]
    public async Task PreExistingBlockedSettlement_WithStaleAssignmentIsRepairedOnActivation()
    {
        const string workflowRunId = "wr-settlement-blocked-repair";
        const string projectId = "proj-settlement-blocked-repair";
        const string workerId = "worker-settlement-blocked-repair";
        var calls = new ReminderCalls();

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var grain = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await grain.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(grain, store, workflowRunId, projectId, workerId);
        await grain.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed"));

        // A run blocked by an older binary keeps its assignment on disk.
        var legacy = Assert.IsType<WorkflowRun>(await store.LoadAsync(workflowRunId));
        var legacySettlement = SettlementOf(legacy);
        legacySettlement.State = AgentResultSettlementState.Blocked;
        legacy.Assignment = new WorkflowAssignment(workerId, TimeProvider.GetUtcNow());
        await store.SaveAsync(legacy);
        var identity = Identity(legacySettlement);
        var deadline = Assert.IsType<DateTimeOffset>(legacySettlement.DeadlineAt);
        var before = (await events.ListAsync(workflowRunId)).Count;

        var repaired = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await repaired.OnActivateAsync(CancellationToken.None);

        await AssertBlockedAndReleasedAsync(store, workflowRunId, workerId, identity, deadline);
        Assert.Null(await repaired.GetAssignedWorkerIdAsync());
        Assert.Equal(before, (await events.ListAsync(workflowRunId)).Count);
        Assert.Equal(binding.TaskRunId, SettlementOf(await store.LoadAsync(workflowRunId)).TaskRunId);
    }

    [Fact]
    public async Task TwoConcurrentUnknownAttempts_ReleaseBothLeasesAtTheirOwnBoundaries()
    {
        const string projectId = "proj-settlement-two-attempts";
        const string workerId = "worker-settlement-two-attempts";
        var calls = new ReminderCalls();

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var attempts = new List<(string RunId, WorkflowGrain Grain, AgentExecutionBinding Binding, string Identity, DateTimeOffset Deadline)>();
        foreach (var index in new[] { 1, 2 })
        {
            var workflowRunId = $"wr-settlement-two-attempts-{index}";
            var grain = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
            await grain.OnActivateAsync(CancellationToken.None);
            var binding = await StartAgentWorkAsync(grain, store, workflowRunId, projectId, workerId);
            await grain.ObserveAgentExecutionAsync(new AgentExecutionObservation(
                binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected"));
            var settlement = SettlementOf(await store.LoadAsync(workflowRunId));
            attempts.Add((
                workflowRunId,
                grain,
                binding,
                Identity(settlement),
                Assert.IsType<DateTimeOffset>(settlement.DeadlineAt)));
        }

        Assert.NotEqual(attempts[0].RunId, attempts[1].RunId);
        TimeProvider.Advance(attempts.Max(attempt => attempt.Deadline) - TimeProvider.GetUtcNow());

        // Cleanup fails for the first attempt; neither lease may survive it.
        calls.FailNextLockRelease = true;
        foreach (var attempt in attempts)
            await attempt.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        foreach (var attempt in attempts)
        {
            await AssertBlockedAndReleasedAsync(store, attempt.RunId, workerId, attempt.Identity, attempt.Deadline);
            Assert.Null(await attempt.Grain.GetAssignedWorkerIdAsync());
            Assert.Equal(3, (await BlockedEventTypesAsync(events, attempt.RunId)).Length);
            Assert.Equal(attempt.Binding.WorkId, SettlementOf(await store.LoadAsync(attempt.RunId)).WorkId);
        }

        foreach (var attempt in attempts)
        {
            var recovered = CreateReminderGrain(scope.ServiceProvider, store, attempt.RunId, calls);
            await recovered.OnActivateAsync(CancellationToken.None);
            await AssertBlockedAndReleasedAsync(store, attempt.RunId, workerId, attempt.Identity, attempt.Deadline);
            Assert.Equal(3, (await BlockedEventTypesAsync(events, attempt.RunId)).Length);
        }
    }

    private static async Task AssertBlockedAndReleasedAsync(
        IWorkflowRunStore store,
        string workflowRunId,
        string workerId,
        string identity,
        DateTimeOffset deadline)
    {
        var run = Assert.IsType<WorkflowRun>(await store.LoadAsync(workflowRunId));
        var task = Assert.Single(run.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(task.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Blocked, settlement.State);
        Assert.Null(run.Assignment);
        Assert.Null(run.AssignedTo);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Null(run.Failure);
        Assert.Null(run.CurrentStage().Failure);
        Assert.Equal(workerId, task.WorkerId);
        Assert.Equal(settlement.WorkId, task.WorkId);
        Assert.Equal(identity, Identity(settlement));
        Assert.Equal(deadline, settlement.DeadlineAt);
        Assert.True(run.HasBlockedAgentResult());
        Assert.False(run.HasDispatchableWork());
        Assert.Null(run.NextWork());
        Assert.Single(run.CurrentStage().Tasks);
    }

    private static async Task<string[]> BlockedEventTypesAsync(IEventStore events, string workflowRunId)
    {
        var types = (await events.ListAsync(workflowRunId)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskCompleted, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskCancelled, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunStopped, types);
        return types.Where(type => type is EventCatalog.ReverseDns.TaskBlocked
            or EventCatalog.ReverseDns.StageBlocked
            or EventCatalog.ReverseDns.WorkflowRunBlocked).ToArray();
    }

    private static AgentResultSettlement SettlementOf(WorkflowRun? run) =>
        Assert.IsType<AgentResultSettlement>(
            Assert.Single(Assert.IsType<WorkflowRun>(run).CurrentStage().Tasks).AgentResultSettlement);

    private static string Identity(AgentResultSettlement settlement) => string.Join(
        '|',
        settlement.TaskRunId,
        settlement.WorkId,
        settlement.RunnerId,
        settlement.AgentSessionId,
        settlement.AgentTurnId,
        settlement.Runtime,
        settlement.RuntimeSessionId,
        settlement.StopOperationId,
        settlement.LastObservation,
        settlement.ReasonCode,
        settlement.Message,
        settlement.FirstUnknownAt,
        settlement.DeadlineAt);
}
