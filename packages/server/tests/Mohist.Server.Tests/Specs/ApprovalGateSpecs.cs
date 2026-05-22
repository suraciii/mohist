using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ApprovalGateSpecs : WorkflowGrainSpecs
{
    public ApprovalGateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ApprovalStage_TasksAndChecksPass_WorkflowAwaitsApproval()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(ApprovalStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        Assert.Equal("plan", task.Stage);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        Assert.Equal("plan-ok", check.WorkType);
        await ReportAsync(runnerId, check.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var poll = await runner.PollAsync();
        Assert.Null(poll);
    }

    [Fact]
    public async Task AwaitingApproval_UserApproves_WorkflowContinuesToNextStage()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(ApprovalStage());

        // plan: init → task → check
        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "pass");

        await workflow.ApproveAsync();

        // build: init → task → check → complete
        var init2 = await PollWorkAsync(runnerId);
        Assert.Equal("build", init2.Stage);
        await ReportAsync(runnerId, init2.WorkId, "completed");

        var task2 = await PollWorkAsync(runnerId);
        Assert.Equal("compile", task2.WorkId);
        await ReportAsync(runnerId, task2.WorkId, "completed");

        var check2 = await PollWorkAsync(runnerId);
        Assert.Equal("build-ok", check2.WorkType);
        await ReportAsync(runnerId, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task AwaitingApproval_UserRejects_WorkflowFails()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(ApprovalStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "pass");

        await workflow.RejectAsync("not good enough");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }
}
