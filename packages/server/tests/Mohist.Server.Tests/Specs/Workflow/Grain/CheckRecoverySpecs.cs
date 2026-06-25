using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class CheckRecoverySpecs : WorkflowGrainSpecs
{
    public CheckRecoverySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Reactivation_WithDispatchedCheckAndOnlineRunner_RedispatchesCheckWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();

        await DeactivateWorkflowAsync(checkWork.WorkflowRunId);
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(checkWork.WorkflowRunId)));

        workflow = Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(checkWork.WorkflowRunId);

        var recoveredWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.NotNull(recoveredWorkId);
        Assert.StartsWith("checks-", recoveredWorkId);
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DispatchedCheckRunnerIdDerivesFromWorkflowAssignment()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();

        Assert.Equal(runnerId, run.Assignment!.RunnerId);
        Assert.Equal(StageCheckStatus.Pending, check.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckResultFromRunnerOutsideWorkflowAssignmentIsIgnored()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        // The runner-grain identity is the runner itself, so a runner that
        // did not pull this work item has no entry in its outstanding set
        // and rejects the report as "untracked". The workflow grain never
        // sees it, mirroring the previous "ignored" behavior.
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        var report = await otherRunner.ReportWorkflowResultAsync(
            checkWork.WorkflowRunId, checkWork.WorkId,
            new WorkResult("pass", Output: "[]"));
        Assert.False(report.Tracked);
        Assert.Equal("untracked", report.Reason);

        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
        Assert.Equal(runnerId, run.Assignment!.RunnerId);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Reactivation_WithDispatchedCheckAndOfflineRunner_SynthesizesEmptyCheckReport_WithoutFailingPendingChecks()
    {
        // After T-004 (design D5), the grain no longer auto-clears
        // dispatched check work on runner loss. RunnerGrain drains its
        // outstanding-work set and synthesizes a failed report through
        // the normal ReportWorkflowResultAsync channel. For a checks
        // work item the synthesized CheckOutcome carries an empty results
        // list, which is a no-op against ProcessCheckOutcomeAsync — the
        // check stays Pending. The work delivery is completed (so
        // GetCurrentWorkIdAsync returns null on reactivation) but no
        // domain failure events are emitted. This pins the "checks
        // closeout is a no-op" property explicitly.
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        await DeactivateWorkflowAsync(checkWork.WorkflowRunId);

        workflow = Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(checkWork.WorkflowRunId);

        // The synthesized check report completed the work delivery, so
        // there is no current work id on reactivation.
        var recoveredWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.Null(recoveredWorkId);

        // The check itself was never failed — synthesized empty check
        // results don't generate any failure events.
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }
}
