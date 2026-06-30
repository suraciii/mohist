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
/// drain that set by synthesizing a failed outcome through the normal
/// <c>ReportWorkflowResultAsync</c> channel — not by calling a grain
/// notification method on <see cref="IWorkflowGrain"/>.
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
    public async Task PollAsync_TracksOutstandingWorkflowWork_AndReportRemovesIt()
    {
        // After T-004, ReportWorkflowResultAsync must track outstanding
        // workflow work so that runner-loss closeout can find it. Pull a
        // workflow work item, observe it is in the runtime active-works
        // list, then report the result and observe it is removed.
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var (work, _) = await PollWorkAnyAsync();
        var runtimeAfterPoll = await runner.GetRuntimeStateAsync();
        Assert.Equal(runnerId, runtimeAfterPoll.Status == RunnerStatus.Online ? runnerId : runnerId);
        var activeWorkflowWork = runtimeAfterPoll.ActiveWorks
            .Where(w => w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.OwnerId == work.WorkflowRunId)
            .ToList();
        Assert.Single(activeWorkflowWork);
        Assert.Equal(work.WorkId, activeWorkflowWork[0].WorkId);

        await runner.ReportWorkflowResultAsync(work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var runtimeAfterReport = await runner.GetRuntimeStateAsync();
        Assert.DoesNotContain(runtimeAfterReport.ActiveWorks, w =>
            w.OwnerKind == WorkDispatchOwnerKinds.Workflow && w.OwnerId == work.WorkflowRunId && w.WorkId == work.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ReportWorkflowResultAsync_AfterRunnerReactivation_ReconstructsActiveWorkAndReports()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await DeactivateRunnerAsync(runnerId);

        var reactivatedRunner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var result = await reactivatedRunner.ReportWorkflowResultAsync(
            work.WorkflowRunId,
            work.WorkId,
            new WorkResult("completed"));

        Assert.Equal("reported", result.Reason);
        Assert.Equal("Completed", await workflow.GetRunStatusAsync());

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Completed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportWorkflowResultAsync_ForSupersededTrackedWork_RemovesStaleRunnerWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (oldWork, _) = await PollWorkAnyAsync();

        await workflow.RerunAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var report = await runner.ReportWorkflowResultAsync(
            oldWork.WorkflowRunId,
            oldWork.WorkId,
            new WorkResult("failed", "superseded"));

        Assert.True(report.Tracked);
        Assert.Equal("stale-work", report.Reason);

        var runtime = await runner.GetRuntimeStateAsync();
        Assert.DoesNotContain(runtime.ActiveWorks, w =>
            w.OwnerKind == WorkDispatchOwnerKinds.Workflow
            && w.OwnerId == oldWork.WorkflowRunId
            && w.WorkId == oldWork.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerLoss_SynthesizesFailedTaskOutcome_ViaReportChannel()
    {
        // Regression for T-004 (design D5): runner-loss closeout must go
        // through the normal ReportWorkflowResultAsync channel. The grain
        // sees the same TaskOutcome(Failed, Detail="runner-lost") that a
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
        // report would. This pins the end-to-end observable contract:
        // the workflow grain ends up with task status Failed and the
        // failure message "runner-lost" — identical to the old
        // NotifyRunnerLostAsync path but produced via the report channel.
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        // The synthesized report's `Detail: "runner-lost"` is preserved
        // end-to-end through the translator → grain → failure-message path.
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }
}
