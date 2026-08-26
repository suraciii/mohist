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

        Assert.Equal(WorkReportVerdict.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ObserveAgentExecutionAsync(observation));
        Assert.Null(await snapshots.LoadJsonAsync(_workflowId!, work.WorkId));

        var unresolved = await LoadRunAsync(_workflowId!);
        Assert.True(unresolved.HasUnresolvedAgentResult());
        Assert.Null(unresolved.NextWork());
        Assert.Null(unresolved.CurrentPendingWork());
        Assert.Null(await workflow.ClaimNextAsync(runnerId, "test-generation"));
        var recovery = Assert.Single((await Services.GetRequiredService<DispatchService>()
            .PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
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
            new RunnerPollRequest([workKey], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Empty((await dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [workKey], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

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

        Assert.Equal(WorkReportVerdict.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

        // Pre-deadline Unknown lease: the run is in the runner's activeWorks,
        // counts against capacity, and is part of the desired redelivery set.
        var preDeadlineRuntime = await runner.GetRuntimeStateAsync();
        var preDeadlineOwner = Assert.Single(preDeadlineRuntime.ActiveWorks, item =>
            string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));
        Assert.Equal(work.WorkId, preDeadlineOwner.WorkId);
        var preDeadlineDesired = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
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

            var desired = await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
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
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ReceiveTaskReportAsync(
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
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
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

        Assert.Equal(WorkReportVerdict.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

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
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

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

        Assert.Equal("refused", report.Ack);
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
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        // A subsequent matching late authoritative report still settles the
        // attempt, proving only an authoritative success/failure can clear
        // the Blocked state through ReceiveTaskReportAsync.
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ReceiveTaskReportAsync(
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

    [Fact]
    public async Task UnboundAgentFailure_IsObservedAsUnknownAndSettlesToBlocked()
    {
        // A runner-side failure before any runtime turn started (e.g. the
        // session-binding fail-closed) carries no execution binding. The
        // report service must route it into the unknown observation so the
        // settlement arbiter owns it — an acknowledged report also lets the
        // Runner retire its journal entry instead of retrying a rejection
        // forever while the workflow silently waits for a result.
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks);
        var reportService = Services.GetRequiredService<WorkflowReportService>();

        var succeededAck = await reportService.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            task.Id,
            new WorkResult("succeeded", "must never be accepted without a binding"),
            CancellationToken.None);
        Assert.Equal("refused", succeededAck.Ack);

        var failure = await reportService.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            task.Id,
            new WorkResult(
                "failed",
                "Workflow AgentSession is active; the previous Runtime Session has not reached a terminal state, so retry is fail-closed",
                Error: new ExecutionError("session-binding-failed", "session binding failed")),
            CancellationToken.None);
        Assert.Equal("accepted", failure.Ack);

        var observed = await LoadRunAsync(_workflowId!);
        var observedTask = Assert.Single(observed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, observedTask.Status);
        Assert.Equal(
            AgentResultSettlementState.Unknown,
            observedTask.AgentResultSettlement!.State);
        Assert.Equal("session-binding-failed", observedTask.AgentResultSettlement.ReasonCode);

        var deadline = observedTask.AgentResultSettlement.DeadlineAt!.Value;
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
    }


}
