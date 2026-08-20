using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Querier;

public sealed class WorkflowRunStatusCacheSpecs : WorkflowDefinitionResolverTestFactory
{
    [Fact]
    public async Task RepeatedStatusReadsReuseTheAggregateWithoutDeserializingAgain()
    {
        var runId = await SeedRunAsync("repeat");
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        var first = await querier.GetStatusAsync(runId);
        var second = await querier.GetStatusAsync(runId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(JSON.Serialize(first), JSON.Serialize(second));
        Assert.Equal(1, deserializer.Count);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task StateWriteChangesTheEtagAndRebuildsTheCachedAggregateOnce()
    {
        var runId = await SeedRunAsync("etag");
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        await querier.GetStatusAsync(runId);
        await UpdateStateAsync(runId, run => run.Status = WorkflowRunStatus.Paused);

        var changed = await querier.GetStatusAsync(runId);
        var repeated = await querier.GetStatusAsync(runId);

        Assert.NotNull(changed);
        Assert.Equal("paused", changed!.Status);
        Assert.Equal(JSON.Serialize(changed), JSON.Serialize(repeated));
        Assert.Equal(2, deserializer.Count);
    }

    [Fact]
    public async Task CacheHitAndForcedRebuildProduceEquivalentViews()
    {
        var runId = await SeedRunAsync("equivalent");
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        var cached = await querier.GetStatusAsync(runId);
        cache.Clear();
        var rebuilt = await querier.GetStatusAsync(runId);

        Assert.NotNull(cached);
        Assert.NotNull(rebuilt);
        Assert.Equal(JSON.Serialize(cached), JSON.Serialize(rebuilt));
        Assert.Equal(2, deserializer.Count);
    }

    [Fact]
    public async Task ArtifactAddedWithoutStateWriteStaysFreshWithoutRebuild()
    {
        var runId = await SeedRunAsync("artifact");
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);
        var first = await querier.GetStatusAsync(runId);
        var taskId = first!.Stages.SelectMany(stage => stage.Tasks).First().Id;

        await using (var db = Database.CreateContext())
        {
            db.WorkflowArtifacts.Add(new WorkflowArtifactRow
            {
                ArtifactId = "artifact-cache-spec",
                WorkflowRunId = runId,
                TaskRunId = taskId,
                Path = "review.md",
                Kind = "file",
                DisplayName = "review.md",
                RecordedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Size = 42,
                ArtifactStoragePath = "virtual/review.md",
            });
            await db.SaveChangesAsync();
        }

        var second = await querier.GetStatusAsync(runId);
        var third = await querier.GetStatusAsync(runId);

        var secondArtifacts = second!.Stages
            .SelectMany(stage => stage.Tasks)
            .Where(t => t.Id == taskId)
            .SelectMany(t => t.ArtifactSummaries ?? [])
            .ToList();
        var thirdArtifacts = third!.Stages
            .SelectMany(stage => stage.Tasks)
            .Where(t => t.Id == taskId)
            .SelectMany(t => t.ArtifactSummaries ?? [])
            .ToList();
        Assert.NotEmpty(secondArtifacts);
        Assert.NotEmpty(thirdArtifacts);
        Assert.All(secondArtifacts, artifact => Assert.Equal("artifact-cache-spec", artifact.ArtifactId));
        Assert.All(thirdArtifacts, artifact => Assert.Equal("artifact-cache-spec", artifact.ArtifactId));
        Assert.Equal(1, deserializer.Count);
    }

    [Fact]
    public async Task UnknownRunReturnsNullWithoutCreatingAnEntry()
    {
        var cache = new WorkflowRunStatusCache();
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        var status = await querier.GetStatusAsync("unknown-status-cache-run");

        Assert.Null(status);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, deserializer.Count);
    }

    [Fact]
    public async Task EvictionRebuildsAnEquivalentView()
    {
        var firstRunId = await SeedRunAsync("eviction-first");
        var secondRunId = await SeedRunAsync("eviction-second");
        var cache = new WorkflowRunStatusCache(capacity: 1);
        var deserializer = new CountingDeserializer();
        var querier = CreateQuerier(cache, deserializer);

        var first = await querier.GetStatusAsync(firstRunId);
        await querier.GetStatusAsync(secondRunId);
        var rebuilt = await querier.GetStatusAsync(firstRunId);

        Assert.NotNull(first);
        Assert.NotNull(rebuilt);
        Assert.Equal(JSON.Serialize(first), JSON.Serialize(rebuilt));
        Assert.Equal(3, deserializer.Count);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task PerCallStatusAssemblyDoesNotMutateTheCachedAggregate()
    {
        var runId = await SeedRunAsync("readonly");
        var cache = new WorkflowRunStatusCache();
        var querier = CreateQuerier(cache, new CountingDeserializer());

        var first = await querier.GetStatusAsync(runId);
        var taskId = first!.Stages.SelectMany(stage => stage.Tasks).First().Id;
        await using (var db = Database.CreateContext())
        {
            db.WorkflowArtifacts.Add(new WorkflowArtifactRow
            {
                ArtifactId = "artifact-readonly-spec",
                WorkflowRunId = runId,
                TaskRunId = taskId,
                Path = "readonly.md",
                Kind = "file",
                DisplayName = "readonly.md",
                RecordedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Size = 7,
                ArtifactStoragePath = "virtual/readonly.md",
            });
            await db.SaveChangesAsync();
        }

        var etag = await ReadEtagAsync(runId);
        Assert.True(cache.TryGet(runId, etag, out var cachedRun));
        var before = JSON.Serialize(cachedRun);

        var second = await querier.GetStatusAsync(runId);

        Assert.True(cache.TryGet(runId, etag, out cachedRun));
        Assert.Equal(before, JSON.Serialize(cachedRun));
        Assert.Contains(
            second!.Stages.SelectMany(stage => stage.Tasks)
                .Where(task => task.Id == taskId)
                .SelectMany(task => task.ArtifactSummaries ?? []),
            artifact => artifact.ArtifactId == "artifact-readonly-spec");
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

    private async Task<string> SeedRunAsync(string label)
    {
        var runId = $"wr_status_cache_{label}";
        var projectId = $"project_status_cache_{label}";
        await SeedAsync(
            projectId,
            1,
            runId,
            issueTemplateJson: null,
            issueWorkflowProfileId: "mohist/local");
        await ReplaceRunStateAsync(runId, projectId, 1, "mohist/local");
        return runId;
    }

    private async Task UpdateStateAsync(string runId, Action<WorkflowRun> update)
    {
        await using var db = Database.CreateContext();
        var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == runId);
        var run = JSON.Deserialize<WorkflowRun>(row.State)!;
        update(run);
        row.State = JSON.Serialize(run);
        var etag = db.Entry(row).Property<long>("ETag");
        etag.CurrentValue = etag.OriginalValue + 1;
        await db.SaveChangesAsync();
    }

    private async Task<long> ReadEtagAsync(string runId)
    {
        await using var db = Database.CreateContext();
        return await db.WorkflowRuns
            .Where(r => r.WorkflowRunId == runId)
            .Select(r => EF.Property<long>(r, "ETag"))
            .SingleAsync();
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
