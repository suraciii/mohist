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
    public async Task RunnerUnregistersWithInFlightWork_FailsRunningTaskAsRunnerLost()
    {
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
    public async Task NotifyRunnerLost_NonRunningTasksAreUnaffected()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkId, "completed");

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
        await workflow.NotifyRunnerLostAsync(runnerId);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task NotifyRunnerLost_IsIdempotent_AndIgnoresOtherRunners()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await workflow.NotifyRunnerLostAsync("other-runner");
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());

        await workflow.NotifyRunnerLostAsync(runnerId);
        await workflow.NotifyRunnerLostAsync(runnerId);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact(Skip = "Tech debt: work-timeout supervision is deleted in T-004 (design D5); this test is replaced by RunnerGrain-driven timeout coverage")]
    public async Task RunningTask_WithoutReport_TimesOutWithoutQueryingRunner()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, _) = await PollWorkAnyAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        task.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<Mohist.Server.Infrastructure.Data.Workflow.IWorkflowRunStore>()
            .SaveAsync(run);
        await DeactivateWorkflowAsync(work.WorkflowRunId);
        workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        run = await LoadRunAsync(work.WorkflowRunId);
        task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal("work-timeout", run.Failure?.Message);
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
    public async Task Reactivation_WithOfflineRunner_DoesNotQueryRunner_AndRunnerLostNotificationFailsRunningTask()
    {
        var workflowId = $"wf-orphan-{Guid.NewGuid():N}";
        var runnerId = $"offline-runner-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(SingleStage(checks: []), workflowId);
        var (dispatched, _) = await PollWorkAnyAsync();
        await workflow.NotifyRunnerLostAsync(_runnerId!);
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

        await reactivatedWorkflow.NotifyRunnerLostAsync(runnerId);
        Assert.Equal("Failed", await reactivatedWorkflow.GetRunStatusAsync());

        run = await LoadRunAsync(workflowId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task HeartbeatRepair_OfflineGrain_PreservesCapacityFromHeartbeatInfo()
    {
        var runnerId = $"repair-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // A fresh grain is Offline with null _info, mirroring the state after the
        // silo collects and reactivates it. A heartbeat carrying the full runner
        // state must rebuild capacity instead of collapsing to the default
        // (regression: runner heartbeats that omitted MaxWorkflowSlots reset a
        // capacity-4 runner to 1).
        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            ProjectId: null,
            CoderModels: ["openai/gpt-4", "anthropic/claude"],
            MaxWorkflowSlots: 4));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal(4, info!.MaxWorkflowSlots);
        Assert.Equal(2, info.CoderModels?.Length);
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
