using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class EventStoreDeliveryProgressSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly SqliteConnection _keeper;
    private EventStore _store = null!;

    public EventStoreDeliveryProgressSpecs()
    {
        var connectionString = $"Data Source=event-store-delivery-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();

        _store = new EventStore(new Factory(_options), NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppendAsync_LeavesDispatchedAtNull()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));

        await using var db = new MohistDbContext(_options);
        var row = await db.WorkflowRunEvents.SingleAsync();
        Assert.Null(row.DispatchedAt);

        var listed = await _store.ListAsync("wr_1");
        Assert.Single(listed);
    }

    [Fact]
    public async Task MarkDispatchedAsync_SetsOnlyMatchedRow()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_2", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/issues/issue_1", "com.mohist.issue.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/epics/epic_1", "com.mohist.epic.completed"));

        var markAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/workflow-runs/wr_1", 1, markAt);

        await using var db = new MohistDbContext(_options);
        var wr1 = await db.WorkflowRunEvents.Where(e => e.Source == "/mohist/workflow-runs/wr_1").OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, wr1.Count);
        Assert.Equal(markAt, wr1[0].DispatchedAt);
        Assert.Null(wr1[1].DispatchedAt);

        var wr2 = await db.WorkflowRunEvents.Where(e => e.Source == "/mohist/workflow-runs/wr_2").SingleAsync();
        Assert.Null(wr2.DispatchedAt);

        var issueRow = await db.IssueEvents.SingleAsync();
        Assert.Null(issueRow.DispatchedAt);

        var epicRow = await db.EpicEvents.SingleAsync();
        Assert.Null(epicRow.DispatchedAt);
    }

    [Fact]
    public async Task MarkDispatchedAsync_IsIdempotent()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));

        var firstMark = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var secondMark = new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/workflow-runs/wr_1", 1, firstMark);
        await _store.MarkDispatchedAsync("/mohist/workflow-runs/wr_1", 1, secondMark);

        await using var db = new MohistDbContext(_options);
        var row = await db.WorkflowRunEvents.SingleAsync();
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(secondMark, row.DispatchedAt);
    }

    [Fact]
    public async Task ListUndeliveredAsync_ReturnsRowsFromAllThreeTables()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/issues/issue_1", "com.mohist.issue.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/epics/epic_1", "com.mohist.epic.completed"));

        var rows = await _store.ListUndeliveredAsync();

        Assert.Equal(3, rows.Count);
        var origins = rows.Select(r => r.Origin).ToList();
        Assert.Equal(new[] { EventOrigin.Epic, EventOrigin.Issue, EventOrigin.WorkflowRun }, origins);
    }

    [Fact]
    public async Task ListUndeliveredAsync_ExcludesDeliveredRows()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));

        var markAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/workflow-runs/wr_1", 1, markAt);

        var rows = await _store.ListUndeliveredAsync();

        var workflowRows = rows.Where(r => r.Origin == EventOrigin.WorkflowRun).ToList();
        Assert.Single(workflowRows);
        Assert.Equal(2, workflowRows[0].Id);
    }

    [Fact]
    public async Task ListUndeliveredAsync_OrdersBySourceThenIdForPerStreamFifo()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_2", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));

        var rows = await _store.ListUndeliveredAsync();

        var wr1Rows = rows.Where(r => r.Source == "/mohist/workflow-runs/wr_1").ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, wr1Rows.Select(r => r.Id));

        Assert.Equal(new[] { "/mohist/workflow-runs/wr_1", "/mohist/workflow-runs/wr_2" },
            rows.Select(r => r.Source).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ListAsync_ReturnsDeliveredAndUndeliveredIdentically()
    {
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/workflow-runs/wr_1", "com.mohist.workflow.task.completed"));

        var markAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/workflow-runs/wr_1", 1, markAt);

        var listed = await _store.ListAsync("wr_1");
        Assert.Equal(2, listed.Count);
        Assert.Equal(new long[] { 1, 2 }, listed.Select(e => e.Id));
    }

    [Fact]
    public async Task ListIssueEventsAsync_ReturnsDeliveredAndUndeliveredIdentically()
    {
        await _store.AppendAsync(BuildEvent("/mohist/issues/issue_1", "com.mohist.issue.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/issues/issue_1", "com.mohist.issue.completed"));

        var markAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/issues/issue_1", 1, markAt);

        var listed = await _store.ListIssueEventsAsync("issue_1");
        Assert.Equal(2, listed.Count);
        Assert.Equal(new long[] { 1, 2 }, listed.Select(e => e.Id));
    }

    [Fact]
    public async Task ListEpicEventsAsync_ReturnsDeliveredAndUndeliveredIdentically()
    {
        await _store.AppendAsync(BuildEvent("/mohist/epics/epic_1", "com.mohist.epic.completed"));
        await _store.AppendAsync(BuildEvent("/mohist/epics/epic_1", "com.mohist.epic.completed"));

        var markAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        await _store.MarkDispatchedAsync("/mohist/epics/epic_1", 1, markAt);

        var listed = await _store.ListEpicEventsAsync("epic_1");
        Assert.Equal(2, listed.Count);
        Assert.Equal(new long[] { 1, 2 }, listed.Select(e => e.Id));
    }

    private static CloudEvent BuildEvent(string source, string type) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: FixedTime,
            data: JsonDocument.Parse("{}").RootElement);

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
