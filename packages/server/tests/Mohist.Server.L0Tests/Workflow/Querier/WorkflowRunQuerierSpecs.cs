using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Querier;

public sealed class WorkflowRunQuerierSpecs : WorkflowDefinitionResolverTestFactory
{
    [Fact]
    public async Task StatusCacheRebuildsAfterWorkflowRunStoreSave()
    {
        const string projectId = "project-status-cache";
        const string runId = "workflow-status-cache";
        await SeedAsync(
            projectId,
            issueNumber: 1,
            runId,
            issueTemplateJson: null,
            issueWorkflowProfileId: "mohist/local");
        await ReplaceRunStateAsync(runId, projectId, 1, "mohist/local");

        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        await querier.GetStatusAsync(runId);

        var dbFactory = new TestDbContextFactory(Database.Options);
        var store = new WorkflowRunStore(
            dbFactory,
            new EventStore(dbFactory, NullLogger<EventStore>.Instance),
            NullLogger<WorkflowRunStore>.Instance,
            new EventDispatchSignal(),
            new DispatchSnapshotStore(dbFactory, NullLogger<DispatchSnapshotStore>.Instance));
        var run = await store.LoadAsync(runId);
        Assert.NotNull(run);
        run!.Status = WorkflowRunStatus.Paused;
        await store.SaveAsync(run);

        var changed = await querier.GetStatusAsync(runId);
        await querier.GetStatusAsync(runId);

        Assert.Equal("paused", changed?.Status);
        Assert.Equal(2, deserializer.Count);
    }

    [Fact]
    public async Task AssignableCandidates_SkipNonRunnableRowsBeforeRunnableCandidate()
    {
        const string projectId = "project-with-many-paused-runs";
        const int candidatePageSize = 20;
        for (var i = 0; i < candidatePageSize; i++)
            await InsertRunRowAsync($"paused-{i:000}", projectId, WorkflowRunStatus.Paused);

        var runnableWorkflowId = "runnable-after-paused-page";
        await InsertRunRowAsync(runnableWorkflowId, projectId, WorkflowRunStatus.Pending);

        var querier = new WorkflowRunQuerier(new TestDbContextFactory(Database.Options));
        var candidates = await querier.FindAssignableCandidatesAsync(projectId, candidatePageSize);

        var candidate = Assert.Single(candidates);
        Assert.Equal(runnableWorkflowId, candidate.WorkflowRunId);
    }

    private WorkflowQuerier CreateQuerier(
        WorkflowRunStatusCache cache,
        IWorkflowRunDeserializer deserializer)
    {
        var factory = new TestDbContextFactory(Database.Options);
        var variableResolver = new WorkflowVariableResolver(
            factory,
            new ProjectVariableStore(factory),
            new IssueVariableStore(factory),
            new WorkflowRunVariablesStore(factory));
        return new WorkflowQuerier(
            factory,
            DefinitionResolver,
            variableResolver,
            new WorkflowArtifactQuerier(factory),
            cache,
            deserializer);
    }

    private async Task InsertRunRowAsync(
        string runId,
        string projectId,
        WorkflowRunStatus status)
    {
        await using var db = Database.CreateContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = JSON.Serialize(new
            {
                status = status.ToString(),
                metadata = new
                {
                    createdAt = TestTime.UtcNow,
                    projectId,
                },
            }),
        });
        await db.SaveChangesAsync();
    }

    private sealed class CountingDeserializer : IWorkflowRunDeserializer
    {
        public int Count { get; private set; }

        public WorkflowRun? Deserialize(string state)
        {
            Count++;
            return JSON.Deserialize<WorkflowRun>(state);
        }
    }
}
