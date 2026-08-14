using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerUnregistersWithInFlightWork_FailsActiveWorkflowWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetAssignedWorkerIdAsync());
        Assert.Null(await workflow.GetCurrentWorkIdAsync());

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerUnregistersWithoutOutstandingWork_DoesNotFailAlreadyCompletedWork()
    {
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

    [Fact]
    public async Task RunnerUnregisters_IsIdempotentForRunnerLoss()
    {
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

    [Fact]
    public async Task Heartbeat_RefreshesPresenceWhilePollIsGated_AndPreventsRunnerCloseout()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.BeginDrainAsync();
        var before = await runner.GetRuntimeStateAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await runner.HeartbeatAsync();

        var afterHeartbeat = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, afterHeartbeat.Status);
        Assert.Equal(before.LastHeartbeatAt.AddMinutes(1), afterHeartbeat.LastHeartbeatAt);

        // This is beyond the original presence interval, but within the
        // interval renewed by the control-plane heartbeat.
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1.5));
        var afterInterval = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, afterInterval.Status);
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
    }

    [Fact]
    public async Task Reactivation_WithPersistedRunningTask_RecoversAssignmentButLeavesWorkDrainToRunnerSide()
    {
        var workflowId = $"wf-orphan-{Guid.NewGuid():N}";
        var runnerId = $"offline-runner-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(SingleStage(checks: []), workflowId);
        var (dispatched, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(workflowId);
        var task = run.Stages.Single().Tasks.Single();
        task.Status = TaskRunStatus.Running;
        task.StartedAt = _fixture.TimeProvider.GetUtcNow();
        task.WorkId = dispatched.WorkId;
        task.WorkerId = runnerId;
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

        run = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Running, run.Stages.Single().Tasks.Single().Status);
    }

    [Fact]
    public async Task HeartbeatRepair_OfflineGrain_RefreshesInfoAndPresence()
    {
        var runnerId = $"repair-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

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

    [Fact]
    public async Task StoppedWorkflow_KeepsAssignment_AndRunnerDropsPendingWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        Assert.NotNull(await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(Services));

        await workflow.StopAsync("test-stop");

        Assert.Equal("Stopped", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetAssignedWorkerIdAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync(Services));

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("test-stop", run.Failure?.Message);
    }
}
