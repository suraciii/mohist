using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs;

public class BoundarySpecs : WorkflowGrainSpecs
{
    public BoundarySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task EmptyStage_NoTasksOrChecks_WorkflowCompletes()
    {
        await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build", [], [])
        ]));

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        Assert.Null(await runner.PollAsync());
        Assert.True(await runner.IsAvailableAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckReportsPending_CheckRunsAgain()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksAsync(r2, check, ("check-1", "pending", "not ready"));

        var (pendingCheck, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-build:", pendingCheck.WorkId);
        Assert.NotEqual(check.WorkId, pendingCheck.WorkId);

        await ReportChecksPassAsync(r3, pendingCheck, "check-1");
        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task UnknownWorkReport_Ignored_CurrentWorkContinues()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, runnerId) = await PollWorkAnyAsync();
        await workflow.ReportResultAsync(runnerId, "unknown-work", new WorkResult("failed", "wrong work"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        await ReportAsync(runnerId, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "check-1");

        Assert.True(await Grains.GetGrain<IRunnerGrain>(r2).IsAvailableAsync());
    }
}
