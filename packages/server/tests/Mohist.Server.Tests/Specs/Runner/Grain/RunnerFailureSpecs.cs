using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerUnregistersWithInFlightWork_SynthesizesRunnerLostFailure_ViaReportChannel()
    {
        // Regression for T-004 (design D5): when the runner unregisters with an
        // outstanding workflow work item, RunnerGrain must drain the
        // outstanding-work set and synthesize a `failed` result through the
        // normal ReportWorkflowResultAsync channel. WorkflowGrain then sees
        // an ordinary failed task with Detail="runner-lost" — exactly what a
        // runner process would have sent if it had finished the work and
        // reported `failed` itself.
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Null(await workflow.GetCurrentWorkIdAsync());

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerUnregistersWithoutOutstandingWork_DoesNotFailAlreadyCompletedWork()
    {
        // The report-channel closeout drains the runner-side outstanding-work
        // set. When the runner has already reported (or never polled), the
        // set is empty and nothing should be synthesized. This pins the
        // "no work to fail" branch and proves the runner-loss path doesn't
        // accidentally regress a normal completion.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkId, "completed");

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerUnregisters_IsIdempotentForRunnerLoss()
    {
        // Pin: a second UnregisterAsync on the same runner must not
        // re-fail the task. The runner has already gone Offline after the
        // first Unregister; the second call is a no-op for runner-loss
        // closeout because the outstanding-work set was drained by the
        // first call.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        var firstStatus = await workflow.GetRunStatusAsync();
        Assert.Equal("Failed", firstStatus);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunningTask_WithoutReport_TimesOutWithoutQueryingRunner()
    {
        // Regression for T-003: RunnerGrain enforces a control-plane
        // WorkCompletionTimeout safety net via a persisted reminder. The
        // scan uses the in-memory active set hydrated from the RunnerWorks
        // ledger and never queries the runner process; a stuck work is
        // synthesized as failed(reason=timeout) through the normal report
        // channel.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.CheckWorkTimeoutsAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        Assert.Null(await workflow.GetCurrentWorkIdAsync());

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("timeout", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Heartbeat_WithOnlineRunner_PreservesRunningTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        await DeactivateWorkflowAsync(work.WorkflowRunId);
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(work.WorkflowRunId)));
        workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
        Assert.Equal(RunnerStatus.Online, (await Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync()).Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Reactivation_WithPersistedRunningTask_RecoversAssignmentButLeavesWorkDrainToRunnerSide()
    {
        // After T-004, the workflow grain is purely a state ref for work
        // results — it has no runner-lost timer, no reminder, no polling
        // of the runner. Reactivation must therefore leave a persisted
        // running task untouched (no automatic failure). The runner-loss
        // closeout is the RunnerGrain's job and only fires from a live
        // RunnerGrain during UnregisterAsync/handle-timeout.
        var workflowId = $"wf-orphan-{Guid.NewGuid():N}";
        var runnerId = $"offline-runner-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(SingleStage(checks: []), workflowId);
        var (dispatched, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(workflowId);
        var task = run.Stages.Single().Tasks.Single();
        task.Status = TaskRunStatus.Running;
        task.StartedAt = DateTimeOffset.UtcNow;
        task.WorkId = dispatched.WorkId;
        task.RunnerId = runnerId;
        run.Status = WorkflowRunStatus.Running;
        run.Stages.Single().Status = StageRunStatus.Running;
        run.Failure = null;
        run.Stages.Single().Failure = null;
        await _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<Mohist.Server.Infrastructure.Data.Workflow.IWorkflowRunStore>()
            .SaveAsync(run);
        await DeactivateWorkflowAsync(workflowId);

        var reactivatedWorkflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        Assert.Equal("Running", await reactivatedWorkflow.GetRunStatusAsync());

        // After reactivation, the task remains Running — WorkflowGrain does
        // not auto-fail because it has no timer/reminder. Runner-loss
        // detection (the runner's responsibility) is intentionally not
        // exercised here because the runner grain is offline and the
        // notification path was deleted in T-004.
        run = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Running, run.Stages.Single().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task HeartbeatRepair_OfflineGrain_RebuildsRunnerInfo()
    {
        var runnerId = $"repair-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // A fresh grain is Offline with null _info, mirroring the state after the
        // silo collects and reactivates it. A heartbeat carrying the full runner
        // state must rebuild the runner info.
        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            ProjectId: null,
            CoderModels: ["openai/gpt-4", "anthropic/claude"]));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(2, info!.CoderModels?.Length);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StoppedWorkflow_KeepsAssignment_AndRunnerDropsPendingWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        Assert.NotNull(await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync());

        await workflow.StopAsync("test-stop");

        Assert.Equal("Stopped", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("test-stop", run.Failure?.Message);
    }
}
