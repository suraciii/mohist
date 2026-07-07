using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

/// <summary>
/// Coverage for T-004 (design D5): <see cref="RunnerGrain"/> must own the
/// outstanding-work set for workflow work items, and on runner loss must
/// drain that set by synthesizing a failed report through the normal
/// workflow report channel.
/// </summary>
[Collection("WorkflowGrain")]
public class RunnerOutstandingWorkSpecs : WorkflowGrainSpecs
{
    public RunnerOutstandingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private async Task DeactivateRunnerAsync(string runnerId)
    {
        await Grains.GetGrain<IRunnerGrain>(runnerId).DeactivateForTestAsync();
        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        await TestWait.ForAsync(
            async () => await management.GetDetailedGrainStatistics(),
            activations => !activations.Any(stat => stat.GrainType.Contains(nameof(RunnerGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(runnerId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"Runner grain '{runnerId}' to deactivate");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerLoss_SynthesizesFailedTaskReport_ViaReportChannel()
    {
        // Regression for T-004 (design D5): runner-loss closeout must go
        // through the normal workflow report channel. The grain
        // sees the same TaskReport(Failed, Detail="runner-lost") that a
        // runner process would have sent if it had finished and reported
        // `failed` itself — there is no separate "runner lost" path on
        // the grain.
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerLoss_WithoutOutstandingWorkflowWork_IsNoOp()
    {
        // The runner may have no polled workflow work (only registered).
        // UnregisterAsync must not throw, must not synthesize a report,
        // and must not touch any unrelated workflow's state. We test this
        // by registering a runner that has no assigned workflow, then
        // unregistering — the closeout path must no-op cleanly.
        var runnerId = $"lonely-runner-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            "test-project-no-workflow"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        // No poll — outstanding-work set is empty.
        var runtimeBefore = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, runtimeBefore.Status);
        Assert.Empty(runtimeBefore.ActiveWorks);

        await runner.UnregisterAsync();

        var runtimeAfter = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Offline, runtimeAfter.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerLoss_FailedReportHasDetailRunnerLost_OnWorkflowGrainSide()
    {
        // After T-004, the runner-side synthesized failed report flows
        // through the same translator that any runner-process "failed"
        // report would. This pins the observable product contract:
        // the workflow grain ends up with task status Failed and the
        // failure message "runner-lost" — identical to the old
        // NotifyRunnerLostAsync path but produced via the report channel.
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        // The synthesized report's `Detail: "runner-lost"` is preserved
        // through the translator -> grain -> failure-message path.
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
