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

[Collection("WorkflowGrain")]
public sealed partial class AgentResultSettlementSpecs : WorkflowGrainSpecs
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
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(observation));

        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.False(completed.HasUnresolvedAgentResult());
        Assert.Contains(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
    }

    [Fact]
    public async Task UnknownRunnerResultUsesTheBoundObservationWithoutOutputArtifactOrFollowUpSideEffects()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-result", "turn-result", "opencode", "runtime-result");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var service = Services.GetRequiredService<WorkflowReportService>();
        var result = new WorkResult(
            "unknown",
            "Agent cleanup was not confirmed",
            Output: JsonSerializer.SerializeToElement(new[] { "invalid output must not fail the task" }),
            ArtifactUploadIds: ["missing-upload"],
            AddTasks: [new RuntimeTaskInput("follow-up", "Must not be projected", "spec/task")]);

        var incomplete = await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result);
        Assert.Equal("stale", incomplete.Ack);
        Assert.Null(incomplete.WorkflowStatus);

        var mismatched = await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            "other-runtime-session");
        Assert.Equal("stale", mismatched.Ack);
        Assert.Equal("Running", mismatched.WorkflowStatus);

        var (ack, status) = await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId);

        Assert.Equal("accepted", ack);
        Assert.Equal("Running", status);
        var unresolved = await LoadRunAsync(_workflowId!);
        var unsettledTask = Assert.Single(unresolved.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(unsettledTask.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.Unknown, settlement.LastObservation);
        Assert.Equal("agent-result-unconfirmed", settlement.ReasonCode);
        Assert.Equal(TaskRunStatus.Running, unsettledTask.Status);
        Assert.Single(unresolved.CurrentStage().Tasks);
        Assert.Null(unsettledTask.Output);
        Assert.DoesNotContain(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);
    }

    [Fact]
    public async Task RecoveredStartedFenceObservation_RequiresTheOriginalAttemptAndDoesNotWriteATerminalResult()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var service = Services.GetRequiredService<WorkflowReportService>();
        var observation = new WorkResult(
            "unknown",
            "Runner restarted after a durable started fence without a completed result receipt.");

        var accepted = await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            observation);
        Assert.Equal("stale", accepted.Ack);
        Assert.Null(accepted.WorkflowStatus);

        var stale = await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            "other-task-attempt",
            observation);
        Assert.Equal("stale", stale.Ack);

        var unresolved = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(unresolved.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(AgentResultSettlementState.AwaitingResult, task.AgentResultSettlement!.State);
        Assert.Equal(work.TaskRunId, task.AgentResultSettlement.TaskRunId);
        Assert.Equal(work.WorkId, task.AgentResultSettlement.WorkId);
        Assert.Equal(runnerId, task.AgentResultSettlement.RunnerId);
        Assert.Null(task.Output);
        Assert.DoesNotContain(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type is EventCatalog.ReverseDns.TaskCompleted
                or EventCatalog.ReverseDns.TaskFailed);
    }

    [Theory]
    [InlineData(AgentExecutionObservationKind.Idle)]
    [InlineData(AgentExecutionObservationKind.Completed)]
    [InlineData(AgentExecutionObservationKind.TargetMissing)]
    public async Task PhysicalObservation_DoesNotSettleOrReplaceTheOriginalAttempt(
        AgentExecutionObservationKind kind)
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var original = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "session-physical-observation",
            "turn-physical-observation",
            "pi",
            "runtime-physical-observation");

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, kind, $"physical-{kind.ToString().ToLowerInvariant()}")));

        var unresolved = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(unresolved.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(task.AgentResultSettlement);
        Assert.Equal(original.Id, task.Id);
        Assert.Equal(work.WorkId, task.WorkId);
        Assert.Equal(runnerId, task.WorkerId);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(kind, settlement.LastObservation);
        Assert.Null(task.Output);
        Assert.Null(unresolved.NextWork());
        Assert.DoesNotContain(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type is EventCatalog.ReverseDns.TaskCompleted
                or EventCatalog.ReverseDns.TaskFailed);
    }

    [Fact]
    public async Task RecoveredCompletedResultReport_SettlesBlockedAttemptWithOriginalIdentity()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-recovered", "turn-recovered", "pi", "runtime-recovered");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        var service = Services.GetRequiredService<WorkflowReportService>();

        Assert.Equal(("accepted", "Running"), await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("unknown", "Runner restarted before a result was durably recorded"),
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));

        var unknown = await LoadRunAsync(_workflowId!);
        var unknownTask = Assert.Single(unknown.CurrentStage().Tasks);
        var deadline = Assert.IsType<DateTimeOffset>(unknownTask.AgentResultSettlement!.DeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        Assert.Equal(AgentResultSettlementState.Blocked,
            Assert.Single(blocked.CurrentStage().Tasks).AgentResultSettlement!.State);

        // A completed journal entry is replayed as the original WorkResult.
        var receipt = new WorkResult(
            "completed",
            Output: JsonSerializer.SerializeToElement(new { answer = "recovered" }),
            ExitCode: 0);
        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            receipt,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));

        var completed = await LoadRunAsync(_workflowId!);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.True(completedTask.Output.HasValue);
        Assert.Equal("recovered", completedTask.Output.Value.GetProperty("answer").GetString());
        Assert.DoesNotContain(await EventStore.ListAsync(_workflowId!), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);

        Assert.Equal(("stale", "Completed"), await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            receipt,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));
    }

    [Fact]
    public async Task UnknownRunnerResultTargetsUniqueAttemptWhenDefinitionIdRepeatsAcrossStages()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition([
            new StageDefinition(
                "plan",
                [new TaskDefinition("repeat", "Plan repeat", "mohist/opencode")],
                []),
            new StageDefinition(
                "build",
                [new TaskDefinition("repeat", "Build repeat", "mohist/opencode")],
                [])
        ]));
        var (plan, runnerId) = await PollWorkAnyAsync();
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            plan.WorkId,
            new TaskReport(
                plan.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: plan.TaskRunId)));
        var (build, buildRunnerId) = await PollWorkAnyAsync();
        Assert.Equal(runnerId, buildRunnerId);
        Assert.NotEqual(plan.TaskRunId, build.TaskRunId);
        Assert.NotEqual(plan.WorkId, build.WorkId);
        var buildTask = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var buildBinding = new AgentExecutionBinding(
            buildTask.Id,
            build.WorkId,
            runnerId,
            "session-repeat",
            "turn-repeat",
            "opencode",
            "runtime-repeat");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(buildBinding));

        var service = Services.GetRequiredService<WorkflowReportService>();
        var (ack, status) = await service.ReportAsync(
            runnerId,
            _workflowId!,
            build.WorkId,
            build.TaskRunId,
            new WorkResult("unknown", "Agent cleanup was not confirmed"),
            CancellationToken.None,
            buildBinding.AgentSessionId,
            buildBinding.AgentTurnId,
            buildBinding.Runtime,
            buildBinding.RuntimeSessionId);

        Assert.Equal("accepted", ack);
        Assert.Equal("Running", status);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal("build", run.CurrentStageId);
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(run.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(build.WorkId, settlement.WorkId);
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

        var workflowRuns = Services.GetRequiredService<WorkflowRunQuerier>();
        Assert.Equal(0, await workflowRuns.CountRunningAssignedToAsync(runnerId));
        Assert.Empty(await workflowRuns.FindRunningAssignedToAsync(runnerId));

        var eventTypes = (await EventStore.ListAsync(_workflowId!)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.StageBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunBlocked, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);

        var lateObservation = observation with { ReasonCode = "late-old-generation-observation", Message = "must not rewrite blocked settlement" };
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(lateObservation));
        var afterLateObservation = await LoadRunAsync(_workflowId!);
        var afterLateSettlement = Assert.Single(afterLateObservation.CurrentStage().Tasks).AgentResultSettlement;
        Assert.Equal(AgentResultSettlementState.Blocked, afterLateSettlement!.State);
        Assert.Equal("stop-unconfirmed", afterLateSettlement.ReasonCode);

        var report = new TaskReport(
            work.WorkId,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            TaskRunId: blockedTask.Id);
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(runnerId, work.WorkId, report));
        Assert.Equal(ReportAck.Stale, await workflow.ReceiveTaskReportAsync(runnerId, work.WorkId, report));

        var completed = await LoadRunAsync(_workflowId!);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task BlockedProjection_ExposesStableCategoryWithPersistedFactsAndReplayIsConsistent()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-proj-1", "turn-proj-1", "opencode", "runtime-proj-1");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed", "transport did not confirm stop", "stop-op-proj")));

        var unknown = await LoadRunAsync(_workflowId!);
        var unknownSettlement = Assert.IsType<AgentResultSettlement>(Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement);
        var deadline = Assert.IsType<DateTimeOffset>(unknownSettlement.DeadlineAt);

        // Before the deadline the status surface exposes Unknown with the
        // persisted reason, message, execution identity, and deadline, and the
        // attempt still owns its active Runner reservation.
        var beforeView = WorkflowStatusMapper.BuildStatusView(unknown, definition: null)!;
        Assert.Equal("running", beforeView.Status);
        Assert.Equal("running", beforeView.Stages[0].Status);
        Assert.Equal("running", beforeView.Stages[0].Tasks[0].Status);
        Assert.Null(beforeView.Failure);
        Assert.Equal(runnerId, beforeView.AssignedTo);
        Assert.Null(beforeView.AgentResultAttention);
        var beforeSettlement = beforeView.Stages[0].Tasks[0].AgentResultSettlement!;
        Assert.Equal("unknown", beforeSettlement.State);
        Assert.Equal("stop-unconfirmed", beforeSettlement.Reason);
        Assert.Equal("stop-unconfirmed", beforeSettlement.ReasonCode);
        Assert.Equal("transport did not confirm stop", beforeSettlement.Message);
        Assert.Equal("stop-op-proj", beforeSettlement.StopOperationId);
        Assert.Equal("session-proj-1", beforeSettlement.AgentSessionId);
        Assert.Equal("turn-proj-1", beforeSettlement.AgentTurnId);
        Assert.Equal(deadline, beforeSettlement.DeadlineAt);

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedView = WorkflowStatusMapper.BuildStatusView(blocked, definition: null)!;
        Assert.Equal("blocked", blockedView.Status);
        Assert.Equal("blocked", blockedView.Stages[0].Status);
        Assert.Equal("blocked", blockedView.Stages[0].Tasks[0].Status);
        Assert.Null(blockedView.Failure);
        Assert.Null(blockedView.AssignedTo);
        Assert.Contains(blockedView.AvailableActions, a => a.Name == "stop");
        var attention = blockedView.AgentResultAttention!;
        Assert.Equal("blocked", attention.State);
        Assert.Equal("agent-result-unconfirmed", attention.Reason);
        Assert.Equal("stop-unconfirmed", attention.ReasonCode);
        Assert.Equal("transport did not confirm stop", attention.Message);
        Assert.Equal(deadline, attention.DeadlineAt);
        Assert.Equal("session-proj-1", attention.AgentSessionId);
        Assert.Equal("turn-proj-1", attention.AgentTurnId);
        var blockedSettlement = blockedView.Stages[0].Tasks[0].AgentResultSettlement!;
        Assert.Equal("blocked", blockedSettlement.State);
        Assert.Equal("agent-result-unconfirmed", blockedSettlement.Reason);
        Assert.Equal("stop-unconfirmed", blockedSettlement.ReasonCode);
        Assert.Equal("transport did not confirm stop", blockedSettlement.Message);
        Assert.Equal(deadline, blockedSettlement.DeadlineAt);

        // The blocked events carry the stable category AND the persisted
        // reason so event consumers observe both without a separate lookup.
        var blockedEvents = (await EventStore.ListAsync(_workflowId!))
            .Where(entry => entry.Envelope.Type is EventCatalog.ReverseDns.TaskBlocked
                or EventCatalog.ReverseDns.StageBlocked
                or EventCatalog.ReverseDns.WorkflowRunBlocked)
            .ToArray();
        Assert.Equal(3, blockedEvents.Length);
        var taskBlockedData = blockedEvents.Single(entry => entry.Envelope.Type == EventCatalog.ReverseDns.TaskBlocked).Envelope.Data!.Value;
        Assert.Equal("agent-result-unconfirmed", taskBlockedData.GetProperty("reason").GetString());
        Assert.Equal("stop-unconfirmed", taskBlockedData.GetProperty("reasonCode").GetString());
        Assert.Equal(deadline, taskBlockedData.GetProperty("deadlineAt").GetDateTimeOffset());
        var runBlockedData = blockedEvents.Single(entry => entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunBlocked).Envelope.Data!.Value;
        Assert.Equal("stop-unconfirmed", runBlockedData.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain((await EventStore.ListAsync(_workflowId!)), entry =>
            entry.Envelope.Type is EventCatalog.ReverseDns.TaskFailed
                or EventCatalog.ReverseDns.StageFailed
                or EventCatalog.ReverseDns.WorkflowRunFailed
                or EventCatalog.ReverseDns.TaskCompleted
                or EventCatalog.ReverseDns.WorkflowRunCompleted);

        // Replaying the reminder and re-reading across activation produces one
        // consistent projection: same blocked attention, same facts, no
        // duplicate blocked events or failure/completion notifications.
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        var replayEvents = (await EventStore.ListAsync(_workflowId!));
        Assert.Equal(blockedEvents.Length, replayEvents.Count(entry => entry.Envelope.Type is EventCatalog.ReverseDns.TaskBlocked
            or EventCatalog.ReverseDns.StageBlocked
            or EventCatalog.ReverseDns.WorkflowRunBlocked));
        var replayed = await LoadRunAsync(_workflowId!);
        var replayedView = WorkflowStatusMapper.BuildStatusView(replayed, definition: null)!;
        Assert.Equal("blocked", replayedView.Status);
        Assert.Equal(attention.ReasonCode, replayedView.AgentResultAttention!.ReasonCode);
        Assert.Equal(attention.DeadlineAt, replayedView.AgentResultAttention.DeadlineAt);
        Assert.Equal(attention.Message, replayedView.AgentResultAttention.Message);
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
        var recovery = Assert.Single((await Services.GetRequiredService<DispatchService>()
            .PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(work.WorkId, recovery.WorkId);
        Assert.NotNull(recovery.AgentRecovery);

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
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: cancelled.Id)));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(observation));
        var eventTypes = (await EventStore.ListAsync(_workflowId!)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskCancelled, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunStopped, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
    }

    [Fact]
    public async Task ExplicitStop_LateReportDoesNotConsumePendingArtifactUpload()
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
            "session-stop-artifact",
            "turn-stop-artifact",
            "opencode",
            "runtime-stop-artifact");
        const string uploadId = "artup_late_after_explicit_stop";
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        await using (var db = new MohistDbContext(options))
        {
            db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
            {
                UploadId = uploadId,
                WorkflowRunId = _workflowId!,
                WorkId = work.WorkId,
                TaskRunId = task.Id,
                Path = "late.txt",
                ContentType = "text/plain",
                ContentHash = "sha256:late-after-stop",
                Size = 4,
                StoragePath = "workflows/test/late.txt",
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
                ExpiresAt = _fixture.TimeProvider.GetUtcNow().AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected")));
        await workflow.StopAsync("operator stop");

        var reportService = Services.GetRequiredService<WorkflowReportService>();
        var report = await reportService.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("stale", report.Ack);
        await using var assertionDb = new MohistDbContext(options);
        Assert.NotNull(await assertionDb.WorkflowArtifactPendingUploads.FindAsync(uploadId));
        Assert.Empty(await assertionDb.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == _workflowId)
            .ToListAsync());
    }

    [Fact]
    public async Task BlockedSettlement_ReleasesRunnerActiveWorksAndCapacityAtOneExactlyOnceBoundary()
    {
        // Issue-628 T-005: a durably Blocked Agent settlement must drop the
        // attempt from Runner activeWorks + capacity + AddMissingRedeliveriesAsync
        // desired set at the SAME post-commit boundary, and the release must
        // remain durable across repeated reminder/poll/status reads while the
        // configured slot totals stay unchanged. The pre-deadline Unknown
        // lease still occupies the runner and counts against used capacity.
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var (work, _) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-release", "turn-release", "opencode", "runtime-release");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");

        var configuredSlotsBefore = await runner.GetSlotsAsync();
        var dispatch = Services.GetRequiredService<DispatchService>();
        var querier = Services.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

        // Pre-deadline Unknown lease: the run is in the runner's activeWorks,
        // counts against capacity, and is part of the desired redelivery set.
        var preDeadlineRuntime = await runner.GetRuntimeStateAsync();
        var preDeadlineOwner = Assert.Single(preDeadlineRuntime.ActiveWorks, item =>
            string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));
        Assert.Equal(work.WorkId, preDeadlineOwner.WorkId);
        var preDeadlineDesired = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
        var preDeadlineRecovery = Assert.Single(preDeadlineDesired.Dispatches);
        Assert.Equal(work.WorkId, preDeadlineRecovery.WorkId);
        Assert.Equal((IReadOnlyList<string>)[_workflowId!], await querier.FindRunningAssignedToAsync(runnerId));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync(runnerId));

        var unknown = await LoadRunAsync(_workflowId!);
        var deadline = Assert.IsType<DateTimeOffset>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        Assert.Equal(AgentResultSettlementState.Blocked,
            Assert.Single(blocked.CurrentStage().Tasks).AgentResultSettlement!.State);
        Assert.Equal(task.Id, Assert.Single(blocked.CurrentStage().Tasks).Id);
        Assert.Equal(work.WorkId, Assert.Single(blocked.CurrentStage().Tasks).WorkId);
        Assert.Equal(runnerId, Assert.Single(blocked.CurrentStage().Tasks).WorkerId);

        // Post-commit boundary: same observation must hold across
        // repeated reminder/poll/status reads. The release is durable, not
        // a per-poll observation.
        for (var round = 0; round < 3; round++)
        {
            await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

            var runtime = await runner.GetRuntimeStateAsync();
            Assert.DoesNotContain(runtime.ActiveWorks, item =>
                string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));

            var desired = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
            Assert.Empty(desired.Dispatches);

            Assert.Empty(await querier.FindRunningAssignedToAsync(runnerId));
            Assert.Equal(0, await querier.CountRunningAssignedToAsync(runnerId));

            // The blocked task must remain in the persisted aggregate so a
            // matching late authoritative report can still settle it.
            var reloaded = await LoadRunAsync(_workflowId!);
            var reloadedTask = Assert.Single(reloaded.CurrentStage().Tasks);
            Assert.Equal(task.Id, reloadedTask.Id);
            Assert.Equal(work.WorkId, reloadedTask.WorkId);
            Assert.Equal(runnerId, reloadedTask.WorkerId);
            Assert.Equal(AgentResultSettlementState.Blocked, reloadedTask.AgentResultSettlement!.State);
        }

        // Configured slot totals must remain unchanged: blocked settlement
        // is a projection release, not a slot-policy change.
        Assert.Equal(configuredSlotsBefore, await runner.GetSlotsAsync());

        // A matching late authoritative report still settles the attempt
        // through the workflow report path without reintroducing it into
        // activeWorks or capacity.
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));

        var settled = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(settled.CurrentStage().Tasks).Status);
        Assert.Empty(await querier.FindRunningAssignedToAsync(runnerId));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(runnerId));
        Assert.Empty((await runner.GetRuntimeStateAsync()).ActiveWorks);
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
    }

    [Fact]
    public async Task BlockedSettlement_FencesMatchingUnknownReportWithoutTaskFailed()
    {
        // Issue-628 T-005: a matching late WorkResult with status unknown
        // is a non-authoritative observation only. When the workflow's
        // blocked domain rejects it as Stale, WorkflowReportService must
        // return stale WITHOUT forwarding the translator's failed fallback
        // to ReceiveTaskReportAsync. The blocked settlement, task/workflow
        // state, event stream, activeWorks, capacity, and the
        // missing-redelivery desired set must remain unchanged; no
        // TaskFailed event is emitted.
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var (work, _) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-unknown-obs", "turn-unknown-obs", "opencode", "runtime-unknown-obs");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");
        var reportService = Services.GetRequiredService<WorkflowReportService>();
        var dispatch = Services.GetRequiredService<DispatchService>();
        var querier = Services.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

        var unknown = await LoadRunAsync(_workflowId!);
        var deadline = Assert.IsType<DateTimeOffset>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt);
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        var beforeReasonCode = blockedTask.AgentResultSettlement!.ReasonCode;
        var beforeMessage = blockedTask.AgentResultSettlement.Message;
        var beforeEventCount = (await EventStore.ListAsync(_workflowId!)).Count;

        // Pre-condition: the durable Blocked projection has already released
        // the run from activeWorks / capacity / desired set.
        Assert.Empty(await querier.FindRunningAssignedToAsync(runnerId));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(runnerId));
        Assert.DoesNotContain((await runner.GetRuntimeStateAsync()).ActiveWorks, item =>
            string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        var report = await reportService.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            task.Id,
            new WorkResult(
                "unknown",
                "Runner restarted before the durably-Blocked attempt could be reclaimed",
                Output: JsonSerializer.SerializeToElement(new { leaked = "must-not-fail" }),
                ArtifactUploadIds: ["leaked-upload-id"],
                AddTasks: [new RuntimeTaskInput("leaked-follow-up", "Must not be projected", "spec/task")]),
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId);

        Assert.Equal("stale", report.Ack);
        Assert.Equal("Running", report.WorkflowStatus);

        var after = await LoadRunAsync(_workflowId!);
        var afterTask = Assert.Single(after.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, afterTask.Status);
        Assert.Equal(AgentResultSettlementState.Blocked, afterTask.AgentResultSettlement!.State);
        Assert.Equal(beforeReasonCode, afterTask.AgentResultSettlement.ReasonCode);
        Assert.Equal(beforeMessage, afterTask.AgentResultSettlement.Message);
        Assert.Equal(blockedTask.Id, afterTask.Id);
        Assert.Equal(work.WorkId, afterTask.WorkId);
        Assert.Equal(runnerId, afterTask.WorkerId);
        Assert.Null(afterTask.Output);

        // The event stream must not have grown: no TaskFailed /
        // TaskCompleted / TaskAddTasks entry is allowed. The Blocked events
        // emitted on the durable transition are unchanged.
        var afterEventCount = (await EventStore.ListAsync(_workflowId!)).Count;
        Assert.Equal(beforeEventCount, afterEventCount);
        var eventTypes = (await EventStore.ListAsync(_workflowId!))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskCompleted, eventTypes);

        // The runner control plane is unchanged: the matching unknown
        // observation cannot reintroduce the run.
        Assert.Empty(await querier.FindRunningAssignedToAsync(runnerId));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(runnerId));
        Assert.DoesNotContain((await runner.GetRuntimeStateAsync()).ActiveWorks, item =>
            string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        // A subsequent matching late authoritative report still settles the
        // attempt, proving only an authoritative success/failure can clear
        // the Blocked state through ReceiveTaskReportAsync.
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));
        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
    }


}
