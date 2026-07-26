using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class AdvanceSpecs : WorkflowGrainSpecs
{
    public AdvanceSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ApprovalStage_CompletesWork_WaitsForApproval()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("review", "Review", "spec/check")],
                RequiresApproval: true),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "review");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync(Services));
    }

    [Fact]
    public async Task NonApprovalStage_CompletesWork_AutoAdvancesToNextStage()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("review", "Review", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "review");

        var (nextTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(r3, nextTask.WorkId, "completed");

        Assert.Equal(RunnerStatus.Online, (await Grains.GetGrain<IRunnerGrain>(r3).GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task EmptyStage_SkipsToNextStage()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan", [], []),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (nextTask, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(runnerId, nextTask.WorkId, "completed");

        Assert.Equal(RunnerStatus.Online, (await Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task EmptyApprovalStage_UserApproves_AdvancesToNextStage()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan", [], [], RequiresApproval: true),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync(Services));

        await workflow.ApproveAsync("operator-1");

        var (nextTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);
        await ReportAsync(r2, nextTask.WorkId, "completed");

        Assert.Equal(RunnerStatus.Online, (await Grains.GetGrain<IRunnerGrain>(r2).GetRuntimeStateAsync()).Status);
    }
}
