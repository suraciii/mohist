using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

[Collection("WorkflowRecovery")]
public class WorkflowRunQuerierSpecs : WorkflowGrainSpecs
{
    public WorkflowRunQuerierSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerPoll_SkipsNonRunnableRowsBeyondFirstPage()
    {
        const int candidatePageSize = 20;
        await ClearBacklogAsync();
        var projectId = "project-with-many-paused-runs";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        for (var i = 0; i < candidatePageSize; i++)
        {
            var workflowId = $"paused-{i:000}";
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, SingleStage(), projectId);
            await workflow.StartAsync(TestInput(projectId));
            await workflow.PauseAsync("hold");
        }

        var runnableWorkflowId = "runnable-after-paused-page";
        var runnable = Grains.GetGrain<IWorkflowGrain>(runnableWorkflowId);
        await SeedWorkflowTemplateAsync(runnableWorkflowId, SingleStage(), projectId);
        await runnable.StartAsync(TestInput(projectId));

        var work = await runner.PollAsync(Services);

        Assert.NotNull(work);
        Assert.Equal(runnableWorkflowId, work.WorkflowRunId);
    }
}
