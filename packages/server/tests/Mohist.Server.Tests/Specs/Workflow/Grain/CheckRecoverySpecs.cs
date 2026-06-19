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
    public async Task Reactivation_WithDispatchedCheckAndOnlineRunner_RedispachesSameCheckWork()
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
            TestProjectId(checkWork.WorkflowRunId),
            MaxWorkflowSlots: RunnerCapacity.DefaultMaxWorkflowSlots));

        workflow = Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(checkWork.WorkflowRunId);

        Assert.Equal(checkWork.WorkId, await workflow.GetCurrentWorkIdAsync());
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
        Assert.Equal(checkWork.WorkId, check.DispatchWorkId);
        Assert.Equal(runnerId, check.DispatchRunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DispatchedCheckRunnerIdDerivesFromWorkflowClaim()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, _) = await PollWorkAnyAsync();
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();

        Assert.Equal(runnerId, run.Claim!.RunnerId);
        Assert.Equal(checkWork.WorkId, check.DispatchWorkId);
        Assert.Equal(run.Claim.RunnerId, check.DispatchRunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckResultFromRunnerOutsideWorkflowClaimIsIgnored()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        await workflow.ReportResultAsync(otherRunnerId, checkWork.WorkId, new WorkResult("pass", Output: "[]"));

        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
        Assert.Equal(runnerId, run.Claim!.RunnerId);
        Assert.Equal(checkWork.WorkId, check.DispatchWorkId);
        Assert.Equal(run.Claim.RunnerId, check.DispatchRunnerId);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Reactivation_WithDispatchedCheckAndOfflineRunner_ClearsAndRequeuesCheck()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (taskWork, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, taskWork.WorkId, "completed");
        var (checkWork, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        await DeactivateWorkflowAsync(checkWork.WorkflowRunId);

        workflow = Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(checkWork.WorkflowRunId);

        var recoveredWorkId = await workflow.GetCurrentWorkIdAsync();
        Assert.NotNull(recoveredWorkId);
        Assert.NotEqual(checkWork.WorkId, recoveredWorkId);
        var run = await LoadRunAsync(checkWork.WorkflowRunId);
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Pending, check.Status);
        Assert.Equal(recoveredWorkId, check.DispatchWorkId);
        Assert.NotNull(check.DispatchRunnerId);
        Assert.NotNull(check.DispatchedAt);
    }
}
