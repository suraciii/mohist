using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

[Collection("WorkflowRecovery")]
public class WorkflowRunQuerierSpecs : WorkflowGrainSpecs
{
    public WorkflowRunQuerierSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task StatusCacheRebuildsAfterWorkflowRunStoreSave()
    {
        await StartWorkflowWithoutRunnerAsync(SingleStage());
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = GetQuerier(cache, deserializer);

        await querier.GetStatusAsync(_workflowId!);
        var store = Services.GetRequiredService<IWorkflowRunStore>();
        var run = await store.LoadAsync(_workflowId!);
        Assert.NotNull(run);
        run!.Status = WorkflowRunStatus.Paused;
        await store.SaveAsync(run);

        var changed = await querier.GetStatusAsync(_workflowId!);
        await querier.GetStatusAsync(_workflowId!);

        Assert.Equal("paused", changed?.Status);
        Assert.Equal(2, deserializer.Count);
    }

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

    private sealed class CountingDeserializer : IWorkflowRunDeserializer
    {
        public int Count { get; private set; }

        public WorkflowRun? Deserialize(string state)
        {
            Count++;
            return Mohist.Server.Infrastructure.JSON.Deserialize<WorkflowRun>(state);
        }
    }
}
