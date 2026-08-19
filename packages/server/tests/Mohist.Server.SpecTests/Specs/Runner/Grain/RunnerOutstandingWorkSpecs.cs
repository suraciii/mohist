using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerOutstandingWorkSpecs : WorkflowGrainSpecs
{
    public RunnerOutstandingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerLoss_RecordsRecoverableInterruptionForActiveWorkflowTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        var recordedAt = _fixture.TimeProvider.GetUtcNow();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        var interruption = Assert.IsType<WorkInterruption>(task.Interruption);
        Assert.Equal("runner-lost", interruption.ReasonCode);
        Assert.Equal(work.WorkId, interruption.WorkId);
        Assert.Equal(work.WorkflowRunId, interruption.OwnerId);
        Assert.Equal(recordedAt, interruption.RecordedAt);
        Assert.Equal(
            interruption.RecordedAt.Add(WorkflowOptionsDefault.RunnerLossRecoveryTimeout),
            interruption.RecoveryDeadlineAt);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Null(run.Failure);
        Assert.True(WorkflowOptionsDefault.RunnerLossRecoveryTimeout > TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task RunnerLoss_WithoutOutstandingWorkflowWork_IsNoOp()
    {
        var runnerId = $"lonely-runner-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            "test-project-no-workflow"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var runtimeBefore = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, runtimeBefore.Status);
        Assert.Empty(runtimeBefore.ActiveWorks);

        await runner.UnregisterAsync();

        var runtimeAfter = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Offline, runtimeAfter.Status);
    }

    [Fact]
    public async Task RunnerLoss_DeadlineFailsTaskWithRecordedReasonAndInterruptionEvent()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var interrupted = await LoadRunAsync(work.WorkflowRunId);
        var deadline = interrupted.Stages.Single().Tasks.Single().Interruption!.RecoveryDeadlineAt;

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.RunnerLossRecoveryReminderName, default);

        var failed = await LoadRunAsync(work.WorkflowRunId);
        var task = failed.Stages.Single().Tasks.Single();
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Null(task.Interruption);
        Assert.Equal(FailureReason.TaskFailed, failed.Failure?.Reason);
        Assert.Equal("runner-lost", failed.Failure?.Message);
        Assert.Contains(
            await EventStore.ListAsync(work.WorkflowRunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.TaskInterrupted);
    }

    [Theory]
    [InlineData("mohist/opencode")]
    [InlineData("mohist/pi")]
    public async Task RunnerLoss_PreservesAgentResultAndReconnectSettlesTheOriginalAttempt(string uses)
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("agent", "Agent", uses)],
            checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(work.WorkflowRunId);
        var originalTask = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            originalTask.Id,
            work.WorkId,
            runnerId,
            $"session-{uses}",
            $"turn-{uses}",
            uses == "mohist/pi" ? "pi" : "opencode",
            $"runtime-{uses}");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var disconnected = await LoadRunAsync(work.WorkflowRunId);
        var unsettledTask = Assert.Single(disconnected.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(unsettledTask.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.Disconnected, settlement.LastObservation);
        Assert.Equal("runner-disconnected", settlement.ReasonCode);
        Assert.Equal(TaskRunStatus.Running, unsettledTask.Status);
        Assert.Equal(WorkflowRunStatus.Running, disconnected.Status);
        Assert.Null(disconnected.Failure);

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        var dispatch = Services.GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        var redelivery = Assert.Single((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(work.WorkId, redelivery.WorkId);
        Assert.Equal(work.TaskRunId, redelivery.TaskRunId);
        Assert.Equal(binding.Runtime, redelivery.AgentRecovery?.Runtime);
        Assert.Equal(binding.RuntimeSessionId, redelivery.AgentRecovery?.RuntimeSessionId);

        var report = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var (ack, _) = await report.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed"),
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId);
        Assert.Equal("accepted", ack);

        var completed = await LoadRunAsync(work.WorkflowRunId);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(originalTask.Id, completedTask.Id);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task RunnerLoss_WithRunningChecksRecordsRecoverableInterruption()
    {
        await StartWorkflowAsync(SingleStage(
            checks: [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint")
            ]));
        var runnerId = _runnerId!;
        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var run = await LoadRunAsync(checks.WorkflowRunId);
        var stage = run.Stages.Single();
        var interruption = Assert.IsType<WorkInterruption>(stage.Interruption);
        Assert.Equal("runner-lost", interruption.ReasonCode);
        Assert.Equal(checks.WorkId, interruption.WorkId);
        Assert.Equal(checks.WorkflowRunId, interruption.OwnerId);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.All(stage.Checks, check => Assert.Equal(StageCheckStatus.Running, check.Status));
        Assert.Null(run.Failure);

        _fixture.TimeProvider.Advance(interruption.RecoveryDeadlineAt - _fixture.TimeProvider.GetUtcNow());
        await Grains.GetGrain<IWorkflowGrain>(checks.WorkflowRunId)
            .ReceiveReminder(WorkflowGrain.RunnerLossRecoveryReminderName, default);

        var failed = await LoadRunAsync(checks.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.All(failed.Stages.Single().Checks, check => Assert.Equal(StageCheckStatus.Failed, check.Status));
        Assert.Equal("runner-lost", failed.Failure?.Message);
        Assert.Contains(
            await EventStore.ListAsync(checks.WorkflowRunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.ChecksInterrupted);
    }

    [Fact]
    public async Task RunnerLoss_RearmsRecoveryDeadlineFromPersistedStateOnActivation()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var interrupted = await LoadRunAsync(work.WorkflowRunId);
        var deadline = interrupted.Stages.Single().Tasks.Single().Interruption!.RecoveryDeadlineAt;

        await DeactivateWorkflowAsync(work.WorkflowRunId);
        var reactivated = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Running", await reactivated.GetRunStatusAsync());
        Assert.NotNull((await LoadRunAsync(work.WorkflowRunId)).Stages.Single().Tasks.Single().Interruption);

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await reactivated.ReceiveReminder(WorkflowGrain.RunnerLossRecoveryReminderName, default);

        var failed = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal("runner-lost", failed.Failure?.Message);
    }

    [Fact]
    public async Task RunnerLoss_AcceptedTerminalReportClearsInterruptionBeforeDeadlineWins()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var interrupted = await LoadRunAsync(work.WorkflowRunId);
        var taskRunId = interrupted.Stages.Single().Tasks.Single().Id;
        var deadline = interrupted.Stages.Single().Tasks.Single().Interruption!.RecoveryDeadlineAt;

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: taskRunId)));

        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.RunnerLossRecoveryReminderName, default);

        var completed = await LoadRunAsync(work.WorkflowRunId);
        var task = completed.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.Null(task.Interruption);
        Assert.Equal(WorkflowRunStatus.Ready, completed.Status);
        Assert.Null(completed.Failure);
    }

    [Fact]
    public async Task RunnerLoss_LateReportAfterDeadlineIsStaleAndDoesNotDuplicateTerminalEvents()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var interrupted = await LoadRunAsync(work.WorkflowRunId);
        var taskRunId = interrupted.Stages.Single().Tasks.Single().Id;
        var deadline = interrupted.Stages.Single().Tasks.Single().Interruption!.RecoveryDeadlineAt;
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());

        var reportService = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var late = await reportService.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            taskRunId,
            new WorkResult("completed", "late previous-generation result"));

        Assert.Equal("stale", late.Ack);
        var eventTypes = (await EventStore.ListAsync(work.WorkflowRunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Equal(1, eventTypes.Count(type => type == EventCatalog.ReverseDns.TaskInterrupted));
        Assert.Equal(1, eventTypes.Count(type => type == EventCatalog.ReverseDns.TaskFailed));

        var duplicate = await reportService.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            taskRunId,
            new WorkResult("completed", "duplicate late result"));
        Assert.Equal("stale", duplicate.Ack);

        var failed = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, failed.Stages.Single().Tasks.Single().Status);
        Assert.Equal("runner-lost", failed.Failure?.Message);
        var finalEventTypes = (await EventStore.ListAsync(work.WorkflowRunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Equal(eventTypes.Length, finalEventTypes.Length);
    }

    [Fact]
    public void RunnerLossRecoveryTimeout_IsConfigurableAndExceedsPresenceTimeout()
    {
        var configured = TimeSpan.FromMinutes(20);
        var options = new WorkflowOptions { RunnerLossRecoveryTimeout = configured };

        Assert.Equal(configured, options.RunnerLossRecoveryTimeout);
        Assert.True(new WorkflowOptions().RunnerLossRecoveryTimeout > TimeSpan.FromMinutes(2));
    }
}

internal static class WorkflowOptionsDefault
{
    public static TimeSpan RunnerLossRecoveryTimeout => new WorkflowOptions().RunnerLossRecoveryTimeout;
}
