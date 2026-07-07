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
        Assert.Equal(StageCheckStatus.Running, check.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DispatchedCheckWorkerIdDerivesFromWorkflowAssignment()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();

        Assert.Equal(runnerId, run.Assignment!.WorkerId);
        Assert.Equal(StageCheckStatus.Running, check.Status);
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
        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        // Under the reconciliation model a report goes direct to the owning
        // grain; a report from a runner not assigned to this work is acked as
        // Stale and discarded (the runner grain no longer relays/tracks). The
        // check status is unchanged and the assignment stays with the original
        // runner's workflow run.
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        var beforeReport = await LoadRunAsync(checkWork.WorkflowRunId);
        var beforeCheck = beforeReport.Stages.Single().Checks.Single();

        await ReportAsync(otherRunnerId, checkWork.WorkflowRunId, checkWork.WorkId,
            new WorkResult("pass", Output: "[]"));

        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(beforeCheck.Status, check.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    // Reactivation_WithDispatchedCheckAndOfflineRunner_SynthesizesEmptyCheckReport
    // was removed: it pinned the old closeout semantics where runner loss
    // synthesized an EMPTY check report (a no-op that left the check Pending
    // and completed the work delivery). Under the reconciliation model the
    // runner grain no longer tracks dispatched check work, and closeout
    // reports in-flight workflow work FAILED direct to the owning grain
    // (see RunnerGrain.CloseoutLostAsync). The "checks closeout is a no-op"
    // property no longer holds; runner-loss closeout for workflow work is
    // covered by RunnerWorkLedgerSpecs.RunnerLoss_SynthesizesFailedRunnerLost_ForWorkflowWork.
}
