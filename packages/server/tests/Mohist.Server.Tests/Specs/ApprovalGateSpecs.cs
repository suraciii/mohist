using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ApprovalGateSpecs : WorkflowGrainSpecs
{
    public ApprovalGateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ApprovalStage_TasksAndChecksPass_WorkflowAwaitsApproval()
    {
        await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        var poll = await runner.PollAsync();
        Assert.Null(poll);
    }

    [Fact]
    public async Task AwaitingApproval_UserApproves_WorkflowContinuesToNextStage()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync();

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check2.WorkId);
        await ReportChecksPassAsync(r4, check2, "build-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task AwaitingApproval_UserApproves_AssignedRunnerContinuesWorkflow()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync();

        var nextRunnerId = await RegisterRunnerAsync();
        var nextRunner = Grains.GetGrain<IRunnerGrain>(nextRunnerId);
        Assert.Null(await nextRunner.PollAsync());

        var assignedRunner = Grains.GetGrain<IRunnerGrain>(r2);
        var buildWork = await assignedRunner.PollAsync();
        Assert.NotNull(buildWork);
        Assert.StartsWith("compile.", buildWork.WorkId);
    }

    [Fact]
    public async Task AwaitingApproval_UserRejects_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RejectAsync("not good enough");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.True(await runner.IsAvailableAsync());
    }
}
