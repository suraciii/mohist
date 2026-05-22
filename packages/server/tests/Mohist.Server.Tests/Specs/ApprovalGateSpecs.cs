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
        Assert.Equal("draft", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.Equal("check", check.WorkType);
        await ReportAsync(r2, check.WorkId, "pass");

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
        await ReportAsync(r2, check.WorkId, "pass");

        await workflow.ApproveAsync();

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.Equal("compile", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.Equal("check", check2.WorkType);
        await ReportAsync(r4, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task AwaitingApproval_UserRejects_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        await workflow.RejectAsync("not good enough");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.True(await runner.IsAvailableAsync());
    }
}
