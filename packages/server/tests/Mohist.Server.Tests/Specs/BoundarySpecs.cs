using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class BoundarySpecs : WorkflowGrainSpecs
{
    public BoundarySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task EmptyStage_NoTasksOrChecks_WorkflowCompletes()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [])
        ]));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task RunningCheck_ReportsPending_CheckRunsAgain()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pending", "not ready");

        var (pendingCheck, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", pendingCheck.WorkId);
        Assert.NotEqual(check.WorkId, pendingCheck.WorkId);

        await ReportAsync(r3, pendingCheck.WorkId, "pass");
        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task UnknownWorkReport_Ignored_CurrentWorkStillCompletes()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, runnerId) = await PollWorkAnyAsync();
        await workflow.ReportResultAsync("unknown-work", new WorkDispatchResult("failed", "wrong work"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        await ReportAsync(runnerId, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        Assert.True(await Grains.GetGrain<IRunnerGrain>(r2).IsAvailableAsync());
    }
}
