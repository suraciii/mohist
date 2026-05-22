using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class AdvanceSpecs : WorkflowGrainSpecs
{
    public AdvanceSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task StageRequiringApproval_CompletesWork_WaitsWithoutDispatchingNextStage()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan",
                [new("draft", "Draft", "spec/task")],
                [new("review", "Review", "spec/check")],
                RequiresApproval: true),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("review:", check.WorkId);
        await ReportAsync(r2, check.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task StageWithoutApproval_CompletesWork_AdvancesToNextStage()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan",
                [new("draft", "Draft", "spec/task")],
                [new("review", "Review", "spec/check")]),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("review:", check.WorkId);
        await ReportAsync(r2, check.WorkId, "pass");

        var (nextTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(r3, nextTask.WorkId, "completed");

        Assert.True(await Grains.GetGrain<IRunnerGrain>(r3).IsAvailableAsync());
    }

    [Fact]
    public async Task EmptyStageWithoutApproval_StartsWorkflow_AdvancesToNextStage()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan", [], []),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (nextTask, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(runnerId, nextTask.WorkId, "completed");

        Assert.True(await Grains.GetGrain<IRunnerGrain>(runnerId).IsAvailableAsync());
    }

    [Fact]
    public async Task EmptyStageRequiringApproval_UserApproves_AdvancesToNextStage()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan", [], [], RequiresApproval: true),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());

        await workflow.ApproveAsync();

        var (nextTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(r2, nextTask.WorkId, "completed");

        Assert.True(await Grains.GetGrain<IRunnerGrain>(r2).IsAvailableAsync());
    }
}
