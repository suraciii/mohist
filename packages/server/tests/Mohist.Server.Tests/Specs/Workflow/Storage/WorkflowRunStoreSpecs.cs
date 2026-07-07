using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Storage;

/// <summary>
/// Unit specs for <see cref="WorkflowRunStore"/>. issue-391 T-003: workflow
/// CloudEvents produced by <see cref="WorkflowRunStore.SaveAsync(WorkflowRun, IReadOnlyList{WorkflowEvent}, CancellationToken)"/>
/// must carry the run's <c>projectId</c> annotation on the envelope so the
/// Agent subscription dispatch handler can resolve the project without
/// reverse-querying the Workflow domain.
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithProjectAnnotation_StampsProjectIdOnCloudEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var captured = new List<CloudEvent>();
        var publisher = new CapturingEventPublisher(captured);
        var store = new WorkflowRunStore(factory, new NoopEventStore(), publisher);

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

        var envelope = Assert.Single(captured);
        Assert.Equal("com.mohist.workflow.run.failed", envelope.Type);
        Assert.True(envelope.Extensions.TryGetValue("projectid", out var projectId));
        Assert.Equal(ProjectId, projectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithoutProjectAnnotation_DoesNotStampProjectIdExtension()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var captured = new List<CloudEvent>();
        var publisher = new CapturingEventPublisher(captured);
        var store = new WorkflowRunStore(factory, new NoopEventStore(), publisher);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var envelope = Assert.Single(captured);
        Assert.False(envelope.Extensions.ContainsKey("projectid"));
    }

    private sealed class CapturingEventPublisher : IEventPublisher
    {
        private readonly List<CloudEvent> _events;
        public CapturingEventPublisher(List<CloudEvent> events) => _events = events;

        public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
        {
            _events.Add(envelope);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TData>(TData data, string type, string source, string? subject = null, IReadOnlyDictionary<string, string>? extensions = null, CancellationToken ct = default)
        {
            _events.Add(new CloudEvent(
                Guid.NewGuid().ToString(),
                new Uri(source, UriKind.RelativeOrAbsolute),
                type,
                DateTimeOffset.UtcNow,
                null,
                subject: subject,
                extensions: extensions));
            return Task.CompletedTask;
        }
    }
}
