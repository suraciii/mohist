using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class CheckRecoverySpecs : WorkflowGrainSpecs
{
    public CheckRecoverySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Reactivation_WithDispatchedCheckAndOnlineRunner_RedispatchesCheckWork()
    {
        await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();

        await DeactivateWorkflowAsync(checkWork.WorkflowRunId);
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(checkWork.WorkflowRunId)));

        var workflow = Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(checkWork.WorkflowRunId);

        var recoveredWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.NotNull(recoveredWorkId);
        Assert.StartsWith("checks-", recoveredWorkId);
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Running, check.Status);
    }

    [Fact]
    public async Task DispatchedCheckWorkerIdDerivesFromWorkflowAssignment()
    {
        await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();

        Assert.Equal(runnerId, run.Assignment!.WorkerId);
        Assert.Equal(StageCheckStatus.Running, check.Status);
    }

    [Fact]
    public async Task CheckResultFromRunnerOutsideWorkflowAssignmentIsIgnored()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        var beforeReport = await LoadRunAsync(checkWork.WorkflowRunId);
        var beforeCheck = beforeReport.Stages.Single().Checks.Single();

        await ReportAsync(otherRunnerId, checkWork.WorkflowRunId, checkWork.WorkId,
            new WorkResult("pass", Output: "[]"));

        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(beforeCheck.Status, check.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }
}
