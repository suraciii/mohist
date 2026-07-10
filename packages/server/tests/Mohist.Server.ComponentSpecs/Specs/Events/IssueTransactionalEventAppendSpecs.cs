using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.ComponentSpecs.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.ComponentSpecs.Specs.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// Issue producer. Covers issue-361 T-004 scenarios: issue state and
/// emitted IssueEvents commit atomically; an event-row write failure
/// rolls back the state transaction and propagates (no
/// log-and-swallow catch remains); durable event rows survive a
/// crash-after-commit and remain readable on a fresh
/// <c>DbContext</c>; events stamped by <c>IssueStore.SaveAsync</c>
/// carry the issue's identity (<c>projectid</c>, <c>issueid</c>,
/// <c>issueno</c>) in <c>extensions</c> (D5 identity stamping).
/// </summary>
public class IssueTransactionalEventAppendSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_issue_txn";
    private const string IssueId = "issue_txn_1";
    private const int IssueNumber = 7;

    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private EventStore _eventStore = null!;

    public IssueTransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=issue-transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbFactory = new Factory(_options);

        using (var db = new MohistDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndIssueEventRowsTogether()
    {
        var store = new IssueStore(_dbFactory, _eventStore);
        var issue = BuildIssue("issue_txn_ok");

        await store.SaveAsync(issue.Id, issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var stored = await _eventStore.ListIssueEventsAsync("issue_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.issue.created");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.issue.archived");

        var loaded = await store.LoadAsync("issue_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("issue_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        var store = new IssueStore(_dbFactory, new ThrowingEventStore());
        var issue = BuildIssue("issue_txn_fail");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(issue.Id, issue, [
                new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
                new IssueArchived(),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.Issues.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.IssueEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_IssueEventRowsRemainDurableOnFreshDbContext()
    {
        var store = new IssueStore(_dbFactory, _eventStore);
        var issue = BuildIssue("issue_txn_crash");

        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.IssueEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/issues/issue_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("com.mohist.issue.archived", row.Type);
        Assert.Null(row.DispatchedAt);

        var issueRow = Assert.Single(await freshDb.Issues.AsNoTracking()
            .Where(r => r.IssueId == "issue_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(issueRow.State));
    }

    [Fact]
    public async Task SaveAsync_StampsProjectIdIssueIdAndIssueNoOnExtensions()
    {
        var store = new IssueStore(_dbFactory, _eventStore);
        var issue = BuildIssue(IssueId);

        await store.SaveAsync(issue.Id, issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var stored = await _eventStore.ListIssueEventsAsync(IssueId);
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
            Assert.Equal(ProjectId, stampedProjectId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
            Assert.Equal(IssueId, stampedIssueId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issueno", out var stampedIssueNo));
            Assert.Equal(IssueNumber.ToString(), stampedIssueNo);
            Assert.Equal(IssueNumber.ToString(), entry.Envelope.Subject);
        }
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        var store = new IssueStore(_dbFactory, _eventStore);
        var issue = BuildIssue("issue_txn_state_only");

        await store.SaveAsync(issue.Id, issue, []);

        var loaded = await store.LoadAsync("issue_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListIssueEventsAsync("issue_txn_state_only"));
    }

    [Fact]
    public async Task SaveAsync_NoEventsOverload_RemainsForCallerWithNoPendingEvents()
    {
        // The grain takes the events-aware overload when there are
        // pending events, but the no-events overload of SaveAsync must
        // still exist for callers that haven't recorded any domain
        // events (e.g. an issue that has been touched but did not
        // transition). It continues to commit the state row cleanly.
        var store = new IssueStore(_dbFactory, _eventStore);
        var issue = BuildIssue("issue_txn_no_events_path");

        await store.SaveAsync(issue.Id, issue);

        var loaded = await store.LoadAsync("issue_txn_no_events_path");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListIssueEventsAsync("issue_txn_no_events_path"));
    }

    private static DomainIssue BuildIssue(string id)
    {
        return new DomainIssue
        {
            Id = id,
            ProjectId = ProjectId,
            Number = IssueNumber,
            Title = "Transaction probe",
            Priority = "p2",
        };
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// <see cref="IEventStore"/> that throws on the second append in a
    /// save transaction, simulating an event-row write failure (e.g.
    /// constraint violation). Used to verify that the store does NOT
    /// swallow the exception and that the issue state transaction is
    /// rolled back. Mirrors the WorkflowRunStore equivalent from T-003.
    /// </summary>
    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2)
            {
                throw new InvalidOperationException("simulated event write failed");
            }
            await db.IssueEvents.AddAsync(new IssueEventRow
            {
                Id = _callCount,
                Source = envelope.Source.ToString(),
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson = "{}",
            }, ct);
        }

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
