using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerOutstandingWorkSpecs : WorkflowGrainSpecs
{
    public RunnerOutstandingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerLoss_FailsActiveWorkflowTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
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
    public async Task RunnerLoss_FailedTaskKeepsRunnerLostMessage()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerLoss_WithRunningChecks_FailsEachRunningCheck()
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

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(checks.WorkflowRunId);
        var stage = run.Stages.Single();
        var typecheck = stage.Checks.Single(c => c.Name == "typecheck");
        var lint = stage.Checks.Single(c => c.Name == "lint");

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(StageCheckStatus.Failed, typecheck.Status);
        Assert.Equal("runner-lost", typecheck.Message);
        Assert.Equal(StageCheckStatus.Failed, lint.Status);
        Assert.Equal("runner-lost", lint.Message);
        Assert.Equal("typecheck", run.Failure?.CheckName);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }
}
