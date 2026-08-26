using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Agent result settlement arbitration on the real grain without a cluster:
/// binding and observation replay fencing, unknown-result observation through
/// the report service, deadline blocking, status projection, explicit stop,
/// and pending-upload gating (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainAgentResultSettlementSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainAgentResultSettlementSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BoundObservationReplayPreservesTaskOutcomeUntilAnAuthoritativeReport()
    {
        var a = await ArrangeAsync("wr-settle-replay");
        var before = (await a.Events.ListAsync(a.RunId)).Count;
        var binding = Binding(a, "session-1", "turn-1");
        var observation = new AgentExecutionObservation(
            binding,
            AgentExecutionObservationKind.StopUnconfirmed,
            "stop-unconfirmed",
            "transport did not confirm stop",
            "stop-1");

        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Stale, await a.Grain.BindAgentExecutionAsync(binding with { AgentSessionId = "other-session" }));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(observation));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(observation));
        Assert.Equal(ReportAck.Stale, await a.Grain.ObserveAgentExecutionAsync(
            observation with { Binding = binding with { AgentTurnId = "other-turn" } }));

        var unresolved = await a.LoadRunAsync();
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(unresolved.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.StopUnconfirmed, settlement.LastObservation);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(unresolved.CurrentStage().Tasks).Status);
        Assert.Equal(WorkflowRunStatus.Running, unresolved.Status);
        Assert.Null(unresolved.Failure);
        Assert.True(unresolved.HasUnresolvedAgentResult());
        Assert.Equal(before + 1, (await a.Events.ListAsync(a.RunId)).Count);

        Assert.Equal(ReportAck.Accepted, await a.Grain.ReceiveTaskReportAsync(
            a.WorkerId,
            a.Work.Id!,
            new TaskReport(a.Work.Id!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, TaskRunId: a.TaskRunId)));
        Assert.Equal(ReportAck.Stale, await a.Grain.ObserveAgentExecutionAsync(observation));

        var completed = await a.LoadRunAsync();
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.False(completed.HasUnresolvedAgentResult());
        Assert.Contains(await a.Events.ListAsync(a.RunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
    }

    [Fact]
    public async Task UnboundUnknownRunnerResult_IsObservedWithTheUnboundAgentResultReason()
    {
        var a = await ArrangeAsync("wr-settle-unknown-unbound");
        var service = a.CreateReportService();
        var result = new WorkResult(
            "unknown",
            "Agent cleanup was not confirmed",
            Output: JsonSerializer.SerializeToElement(new[] { "invalid output must not fail the task" }),
            ArtifactUploadIds: ["missing-upload"],
            AddTasks: [new RuntimeTaskInput("follow-up", "Must not be projected", "spec/task")]);

        // A binding-less runner observation is acknowledged into settlement
        // arbitration instead of being rejected as stale, so the Runner can
        // retire its journal entry and the workflow surfaces a visible
        // unresolved state.
        var (ack, status) = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, result);
        Assert.Equal("accepted", ack);
        Assert.Equal("Running", status);

        var unresolved = await a.LoadRunAsync();
        var unsettledTask = Assert.Single(unresolved.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(unsettledTask.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal("unbound-agent-result", settlement.ReasonCode);
        Assert.Equal(TaskRunStatus.Running, unsettledTask.Status);
        Assert.Single(unresolved.CurrentStage().Tasks);
        Assert.Null(unsettledTask.Output);
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);
    }

    [Fact]
    public async Task UnknownRunnerResultUsesTheBoundObservationWithoutOutputArtifactOrFollowUpSideEffects()
    {
        var a = await ArrangeAsync("wr-settle-unknown-bound");
        var binding = Binding(a, "session-result", "turn-result");
        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        var service = a.CreateReportService();
        var result = new WorkResult(
            "unknown",
            "Agent cleanup was not confirmed",
            Output: JsonSerializer.SerializeToElement(new[] { "invalid output must not fail the task" }),
            ArtifactUploadIds: ["missing-upload"],
            AddTasks: [new RuntimeTaskInput("follow-up", "Must not be projected", "spec/task")]);

        var mismatched = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, result,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            "other-runtime-session");
        Assert.Equal("stale", mismatched.Ack);
        Assert.Equal("Running", mismatched.WorkflowStatus);

        var (ack, status) = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, result,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId);

        Assert.Equal("accepted", ack);
        Assert.Equal("Running", status);
        var unresolved = await a.LoadRunAsync();
        var unsettledTask = Assert.Single(unresolved.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(unsettledTask.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.Unknown, settlement.LastObservation);
        Assert.Equal("agent-result-unconfirmed", settlement.ReasonCode);
        Assert.Equal(TaskRunStatus.Running, unsettledTask.Status);
        Assert.Single(unresolved.CurrentStage().Tasks);
        Assert.Null(unsettledTask.Output);
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);
    }

    [Fact]
    public async Task RecoveredStartedFenceObservation_IsObservedOnTheOriginalAttemptWithoutATerminalResult()
    {
        var a = await ArrangeAsync("wr-settle-started-fence");
        var service = a.CreateReportService();
        var observation = new WorkResult(
            "unknown",
            "Runner restarted after a durable started fence without a completed result receipt.");

        var accepted = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, observation);
        Assert.Equal("accepted", accepted.Ack);
        Assert.Equal("Running", accepted.WorkflowStatus);

        var stale = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, "other-task-attempt", observation);
        Assert.Equal("stale", stale.Ack);

        var unresolved = await a.LoadRunAsync();
        var task = Assert.Single(unresolved.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(AgentResultSettlementState.Unknown, task.AgentResultSettlement!.State);
        Assert.Equal(a.TaskRunId, task.AgentResultSettlement.TaskRunId);
        Assert.Equal(a.Work.Id, task.AgentResultSettlement.WorkId);
        Assert.Equal(a.WorkerId, task.AgentResultSettlement.RunnerId);
        Assert.Null(task.Output);
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
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
        var a = await ArrangeAsync($"wr-settle-physical-{kind}");
        var original = Assert.Single((await a.LoadRunAsync()).CurrentStage().Tasks);
        var binding = Binding(a, "session-physical-observation", "turn-physical-observation");

        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, kind, $"physical-{kind.ToString().ToLowerInvariant()}")));

        var unresolved = await a.LoadRunAsync();
        var task = Assert.Single(unresolved.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(task.AgentResultSettlement);
        Assert.Equal(original.Id, task.Id);
        Assert.Equal(a.Work.Id, task.WorkId);
        Assert.Equal(a.WorkerId, task.WorkerId);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(kind, settlement.LastObservation);
        Assert.Null(task.Output);
        Assert.Null(unresolved.NextWork());
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
            entry.Envelope.Type is EventCatalog.ReverseDns.TaskCompleted
                or EventCatalog.ReverseDns.TaskFailed);
    }

    [Fact]
    public async Task RecoveredCompletedResultReport_SettlesBlockedAttemptWithOriginalIdentity()
    {
        var a = await ArrangeAsync("wr-settle-recovered-completed");
        var binding = Binding(a, "session-recovered", "turn-recovered");
        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        var service = a.CreateReportService();

        Assert.Equal(("accepted", "Running"), await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId,
            new WorkResult("unknown", "Runner restarted before a result was durably recorded"),
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));

        var unknown = await a.LoadRunAsync();
        var unknownTask = Assert.Single(unknown.CurrentStage().Tasks);
        var deadline = Assert.IsType<DateTimeOffset>(unknownTask.AgentResultSettlement!.DeadlineAt);
        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow());
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await a.LoadRunAsync();
        Assert.Equal(AgentResultSettlementState.Blocked,
            Assert.Single(blocked.CurrentStage().Tasks).AgentResultSettlement!.State);

        // A completed journal entry is replayed as the original WorkResult.
        var receipt = new WorkResult(
            "completed",
            Output: JsonSerializer.SerializeToElement(new { answer = "recovered" }),
            ExitCode: 0);
        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, receipt,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));

        var completed = await a.LoadRunAsync();
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.True(completedTask.Output.HasValue);
        Assert.Equal("recovered", completedTask.Output.Value.GetProperty("answer").GetString());
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskFailed);

        var eventCount = (await a.Events.ListAsync(a.RunId)).Count;
        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId, receipt,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId));
        Assert.Equal(eventCount, (await a.Events.ListAsync(a.RunId)).Count);

        TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        Assert.Equal(eventCount, (await a.Events.ListAsync(a.RunId)).Count);
    }

    [Fact]
    public async Task UnknownRunnerResultTargetsUniqueAttemptWhenDefinitionIdRepeatsAcrossStages()
    {
        var runId = "wr-settle-repeat-def";
        var definition = new WorkflowDefinition([
            new StageDefinition(
                "plan",
                [new TaskDefinition("repeat", "Plan repeat", "mohist/opencode")],
                []),
            new StageDefinition(
                "build",
                [new TaskDefinition("repeat", "Build repeat", "mohist/opencode")],
                [])
        ]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        await arrangement.Grain.AssignWorkerAsync(arrangement.WorkerId);
        var plan = await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation");
        Assert.NotNull(plan);
        var planTaskRunId = await arrangement.RunningTaskRunIdAsync();

        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.ReceiveTaskReportAsync(
            arrangement.WorkerId,
            plan!.Id!,
            new TaskReport(plan.Id!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, TaskRunId: planTaskRunId)));

        var build = await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation");
        Assert.NotNull(build);
        var buildTask = Assert.Single((await arrangement.Store.LoadAsync(runId))!.CurrentStage().Tasks);
        Assert.NotEqual(planTaskRunId, buildTask.Id);
        Assert.NotEqual(plan.Id, build.Id);
        var buildBinding = new AgentExecutionBinding(
            buildTask.Id,
            build.Id!,
            arrangement.WorkerId,
            "session-repeat",
            "turn-repeat",
            "opencode",
            "runtime-repeat");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.BindAgentExecutionAsync(buildBinding));

        var a = new SettlementArrangement(arrangement, build!, buildTask.Id, _fixture.Services);
        var service = a.CreateReportService();
        var (ack, status) = await service.ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.TaskRunId,
            new WorkResult("unknown", "Agent cleanup was not confirmed"),
            CancellationToken.None,
            buildBinding.AgentSessionId,
            buildBinding.AgentTurnId,
            buildBinding.Runtime,
            buildBinding.RuntimeSessionId);

        Assert.Equal("accepted", ack);
        Assert.Equal("Running", status);
        var run = await a.LoadRunAsync();
        Assert.Equal("build", run.CurrentStageId);
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(run.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(build.Id, settlement.WorkId);
    }

    [Fact]
    public async Task ReminderTick_UsesTheFixedDeadlineAndBlocksWithoutFailure()
    {
        var a = await ArrangeAsync("wr-settle-reminder-deadline");
        var binding = Binding(a, "session-2", "turn-2");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");

        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(observation));

        var unknown = await a.LoadRunAsync();
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement);
        var deadline = Assert.IsType<DateTimeOffset>(settlement.DeadlineAt);
        Assert.Equal(settlement.FirstUnknownAt!.Value.AddMinutes(5), deadline);

        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        Assert.Equal(AgentResultSettlementState.Unknown,
            Assert.Single((await a.LoadRunAsync()).CurrentStage().Tasks).AgentResultSettlement!.State);

        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow());
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await a.LoadRunAsync();
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        Assert.Equal(TaskRunStatus.Running, blockedTask.Status);
        Assert.Equal(WorkflowRunStatus.Running, blocked.Status);
        Assert.Null(blocked.Failure);
        Assert.Null(blocked.CurrentStage().Failure);

        var workflowRuns = a.Services.GetRequiredService<WorkflowRunQuerier>();
        Assert.Equal(0, await workflowRuns.CountRunningAssignedToAsync(a.WorkerId));
        Assert.Empty(await workflowRuns.FindRunningAssignedToAsync(a.WorkerId));

        var eventTypes = (await a.Events.ListAsync(a.RunId)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.StageBlocked, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunBlocked, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);

        var lateObservation = observation with { ReasonCode = "late-old-generation-observation", Message = "must not rewrite blocked settlement" };
        Assert.Equal(ReportAck.Stale, await a.Grain.ObserveAgentExecutionAsync(lateObservation));
        var afterLateSettlement = Assert.Single((await a.LoadRunAsync()).CurrentStage().Tasks).AgentResultSettlement;
        Assert.Equal(AgentResultSettlementState.Blocked, afterLateSettlement!.State);
        Assert.Equal("stop-unconfirmed", afterLateSettlement.ReasonCode);

        var report = new TaskReport(
            a.Work.Id!,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            TaskRunId: blockedTask.Id);
        Assert.Equal(ReportAck.Accepted, await a.Grain.ReceiveTaskReportAsync(a.WorkerId, a.Work.Id!, report));
        Assert.Equal(ReportAck.Stale, await a.Grain.ReceiveTaskReportAsync(a.WorkerId, a.Work.Id!, report));

        var completed = await a.LoadRunAsync();
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task BlockedProjection_ExposesStableCategoryWithPersistedFactsAndReplayIsConsistent()
    {
        var a = await ArrangeAsync("wr-settle-projection");
        var binding = Binding(a, "session-proj-1", "turn-proj-1");
        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed", "transport did not confirm stop", "stop-op-proj")));

        var unknown = await a.LoadRunAsync();
        var unknownSettlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement);
        var deadline = Assert.IsType<DateTimeOffset>(unknownSettlement.DeadlineAt);

        // Before the deadline the status surface exposes Unknown with the
        // persisted reason, message, execution identity, and deadline, and the
        // attempt still owns its active Runner reservation.
        var beforeView = WorkflowStatusMapper.BuildStatusView(unknown, definition: null)!;
        Assert.Equal("running", beforeView.Status);
        Assert.Equal("running", beforeView.Stages[0].Status);
        Assert.Equal("running", beforeView.Stages[0].Tasks[0].Status);
        Assert.Null(beforeView.Failure);
        Assert.Equal(a.WorkerId, beforeView.AssignedTo);
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

        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow());
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await a.LoadRunAsync();
        var blockedView = WorkflowStatusMapper.BuildStatusView(blocked, definition: null)!;
        Assert.Equal("blocked", blockedView.Status);
        Assert.Equal("blocked", blockedView.Stages[0].Status);
        Assert.Equal("blocked", blockedView.Stages[0].Tasks[0].Status);
        Assert.Null(blockedView.Failure);
        Assert.Null(blockedView.AssignedTo);
        Assert.Contains(blockedView.AvailableActions, action => action.Name == "stop");
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
        var blockedEvents = (await a.Events.ListAsync(a.RunId))
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
        Assert.DoesNotContain(await a.Events.ListAsync(a.RunId), entry =>
            entry.Envelope.Type is EventCatalog.ReverseDns.TaskFailed
                or EventCatalog.ReverseDns.StageFailed
                or EventCatalog.ReverseDns.WorkflowRunFailed
                or EventCatalog.ReverseDns.TaskCompleted
                or EventCatalog.ReverseDns.WorkflowRunCompleted);

        // Replaying the reminder and re-reading across activation produces one
        // consistent projection: same blocked attention, same facts, no
        // duplicate blocked events or failure/completion notifications.
        await a.Grain.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);
        var replayEvents = await a.Events.ListAsync(a.RunId);
        Assert.Equal(blockedEvents.Length, replayEvents.Count(entry => entry.Envelope.Type is EventCatalog.ReverseDns.TaskBlocked
            or EventCatalog.ReverseDns.StageBlocked
            or EventCatalog.ReverseDns.WorkflowRunBlocked));
        var replayedView = WorkflowStatusMapper.BuildStatusView(await a.LoadRunAsync(), definition: null)!;
        Assert.Equal("blocked", replayedView.Status);
        Assert.Equal(attention.ReasonCode, replayedView.AgentResultAttention!.ReasonCode);
        Assert.Equal(attention.DeadlineAt, replayedView.AgentResultAttention.DeadlineAt);
        Assert.Equal(attention.Message, replayedView.AgentResultAttention.Message);
    }

    [Fact]
    public async Task ExplicitStop_CancelsTheUnresolvedAttemptThenMakesLaterReportsAndObservationsStale()
    {
        var a = await ArrangeAsync("wr-settle-explicit-stop");
        var binding = Binding(a, "session-4", "turn-4");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.TargetMissing, "target-missing");

        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(observation));
        await a.Grain.StopAsync("operator confirmed stop");
        await a.Grain.StopAsync("cleanup replay");

        var stopped = await a.LoadRunAsync();
        var cancelled = Assert.Single(stopped.CurrentStage().Tasks);
        Assert.Equal(WorkflowRunStatus.Stopped, stopped.Status);
        Assert.Equal(TaskRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(a.Work.Id, cancelled.WorkId);
        Assert.Equal(a.WorkerId, cancelled.WorkerId);
        Assert.NotNull(cancelled.AgentResultSettlement);
        Assert.Null(stopped.Failure);
        Assert.Null(stopped.CurrentStage().Failure);
        Assert.Null(await a.Snapshots.LoadJsonAsync(a.RunId, a.Work.Id!));
        Assert.Equal(ReportAck.Stale, await a.Grain.ReceiveTaskReportAsync(
            a.WorkerId,
            a.Work.Id!,
            new TaskReport(
                a.Work.Id!,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: cancelled.Id)));
        Assert.Equal(ReportAck.Stale, await a.Grain.ObserveAgentExecutionAsync(observation));
        var eventTypes = (await a.Events.ListAsync(a.RunId)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskCancelled, eventTypes);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunStopped, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
    }

    [Fact]
    public async Task ExplicitStop_LateReportDoesNotConsumePendingArtifactUpload()
    {
        var a = await ArrangeAsync("wr-settle-stop-artifact");
        const string uploadId = "artup_late_after_explicit_stop";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
            {
                UploadId = uploadId,
                WorkflowRunId = a.RunId,
                WorkId = a.Work.Id!,
                TaskRunId = a.TaskRunId,
                Path = "late.txt",
                ContentType = "text/plain",
                ContentHash = "sha256:late-after-stop",
                Size = 4,
                StoragePath = "workflows/test/late.txt",
                CreatedAt = TimeProvider.GetUtcNow(),
                ExpiresAt = TimeProvider.GetUtcNow().AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var binding = Binding(a, "session-stop-artifact", "turn-stop-artifact");
        Assert.Equal(ReportAck.Accepted, await a.Grain.BindAgentExecutionAsync(binding));
        Assert.Equal(ReportAck.Accepted, await a.Grain.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected")));
        await a.Grain.StopAsync("operator stop");

        var report = await a.CreateReportService().ReportAsync(
            a.WorkerId,
            a.RunId,
            a.Work.Id!,
            a.TaskRunId,
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("stale", report.Ack);
        await using var assertionScope = _fixture.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.NotNull(await assertionDb.WorkflowArtifactPendingUploads.FindAsync(uploadId));
        Assert.Empty(await assertionDb.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == a.RunId)
            .ToListAsync());
    }

    private AgentExecutionBinding Binding(SettlementArrangement a, string sessionId, string turnId) =>
        new(
            a.TaskRunId,
            a.Work.Id!,
            a.WorkerId,
            sessionId,
            turnId,
            "opencode",
            $"runtime-{sessionId}");

    private async Task<SettlementArrangement> ArrangeAsync(string runId)
    {
        var definition = SingleStage([new TaskDefinition("agent", "Agent", "mohist/opencode")]);
        // A per-run worker id keeps runner-scoped projections isolated in the
        // persistent shared database, mirroring the cluster fixture's
        // per-test runner.
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        await arrangement.Grain.AssignWorkerAsync(arrangement.WorkerId);
        var work = await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation");
        Assert.NotNull(work);
        var taskRunId = await arrangement.RunningTaskRunIdAsync();
        return new SettlementArrangement(arrangement, work!, taskRunId, _fixture.Services);
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks) => new(
    [
        new StageDefinition("build", tasks, []),
    ]);

    private sealed record SettlementArrangement(
        WorkflowGrainArrangement Arrangement,
        WorkItem Work,
        string TaskRunId,
        IServiceProvider Services)
    {
        public WorkflowGrain Grain => Arrangement.Grain;
        public IEventStore Events => Arrangement.Events;
        public IDispatchSnapshotStore Snapshots => Arrangement.Snapshots;
        public IWorkflowRunStore Store => Arrangement.Store;
        public RunnerUpdateOperationGrainRegistry? Operations => Arrangement.Operations;
        public string RunId => Arrangement.RunId;
        public string WorkerId => Arrangement.WorkerId;

        public WorkflowReportService CreateReportService() =>
            WorkflowGrainContractSupport.CreateReportService(
                Services,
                Grain,
                Operations is null ? null : runnerId => Operations.For(runnerId));

        public async Task<WorkflowRun> LoadRunAsync() =>
            await Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
    }
}
