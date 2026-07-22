using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class BoundarySpecs : WorkflowGrainSpecs
{
    public BoundarySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task EmptyStage_NoTasksOrChecks_WorkflowCompletes()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build", [], [])
        ]));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync(Services));
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task CheckReportsPending_CheckRunsAgain()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksAsync(r2, check, ("check-1", "pending", "not ready"));

        var (pendingCheck, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", pendingCheck.WorkId);
        Assert.NotNull(pendingCheck);

        await ReportChecksPassAsync(r3, pendingCheck, "check-1");
        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task UnknownWorkReport_Ignored_CurrentWorkContinues()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await ReportAsync(runnerId, task.WorkflowRunId, "unknown-work", new WorkResult("failed", "wrong work"));

        var repoll = await runner.PollAsync(Services);
        if (repoll is not null)
        {
            Assert.Equal(task.WorkflowRunId, repoll.WorkflowRunId);
            Assert.Equal(task.WorkId, repoll.WorkId);
        }

        await ReportAsync(runnerId, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "check-1");

        Assert.Equal(RunnerStatus.Online, (await Grains.GetGrain<IRunnerGrain>(r2).GetRuntimeStateAsync()).Status);
    }
}
