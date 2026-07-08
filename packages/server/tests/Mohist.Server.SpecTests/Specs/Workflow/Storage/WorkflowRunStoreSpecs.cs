using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

/// <summary>
/// Unit specs for <see cref="WorkflowRunStore"/> covering issue-361 T-003:
/// the store now stamps both <c>projectid</c> and <c>issueid</c> onto the
/// emitted WorkflowRun CloudEvent (read from
/// <see cref="WorkflowRunMetadata.Annotations"/>), appends the event row in
/// the same EF Core transaction as the run state, and lets an event-row
/// write failure roll back the state transaction instead of swallowing it.
/// </summary>
public sealed class FakeWorkflowRunStoreDbContextFactory : IDbContextFactory<MohistDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeWorkflowRunStoreDbContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class WorkflowRunStoreSpecs
{
    private const string ProjectId = "proj_workflow_store";
    private const string IssueId = "issue_ws_1";
    private const string WorkflowRunId = "wr_ws_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithProjectAnnotation_StampsProjectIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        var envelope = stored.Envelope;
        Assert.Equal("com.mohist.workflow.run.failed", envelope.Type);
        Assert.True(envelope.Extensions.TryGetValue("projectid", out var projectId));
        Assert.Equal(ProjectId, projectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithIssueAnnotation_StampsIssueIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.True(stored.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
        Assert.Equal(IssueId, stampedIssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithoutProjectAnnotation_DoesNotStampProjectIdExtension()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.False(stored.Envelope.Extensions.ContainsKey("projectid"));
        Assert.False(stored.Envelope.Extensions.ContainsKey("issueid"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithEvents_PersistsStateAndEventRowsInSameTransaction()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunFailed("boom"),
        ]);

        var stored = await eventStore.ListAsync(WorkflowRunId);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.started");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.failed");

        var loaded = await store.LoadAsync(WorkflowRunId);
        Assert.NotNull(loaded);
        Assert.Equal(WorkflowRunId, loaded!.Id);
    }
}