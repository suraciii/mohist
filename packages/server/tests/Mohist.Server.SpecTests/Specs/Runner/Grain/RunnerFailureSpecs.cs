using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerUnregistersWithInFlightWork_FailsItAsRunnerLost()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(run.Stages.Single().Tasks).Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerUnregistersWithoutOutstandingWork_DoesNotFailAlreadyCompletedWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkId, "completed");

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(run.Stages.Single().Tasks).Status);
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

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1.5));
        var afterOriginalPresenceInterval = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, afterOriginalPresenceInterval.Status);
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
    }

    [Fact]
    public async Task Heartbeat_WithOnlineRunner_PreservesRunningTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).HeartbeatAsync();

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
    }
}
