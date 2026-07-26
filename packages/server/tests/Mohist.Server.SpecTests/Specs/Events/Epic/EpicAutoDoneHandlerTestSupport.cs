using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Epic.Subscriptions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using System.Text.Json;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Events;

public abstract class EpicAutoDoneHandlerTestSupport
{
    protected static readonly DateTimeOffset EventTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    protected static CloudEvent<IssueDraftChanged> BuildDraftChangedEvent(
        string projectId, string issueId, bool oldIsDraft, bool newIsDraft) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueDraftChanged,
            time: EventTime,
            data: new IssueDraftChanged(oldIsDraft, newIsDraft),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                [EventCatalog.Lineage.Issue] = "1",
            });

    protected static CloudEvent<IssuePrerequisiteRemoved> BuildPrerequisiteRemovedEvent(
        string projectId, string issueId, int prereqNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssuePrerequisiteRemoved,
            time: EventTime,
            data: new IssuePrerequisiteRemoved(prereqNumber),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                [EventCatalog.Lineage.Issue] = "1",
            });

    protected static CloudEvent<EpicStartAttemptFailed> BuildStartAttemptFailedEvent(
        string projectId, string epicId, string issueId, int issueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri(EpicEventPersistence.EpicSource(projectId, EpicNumber(epicId)), UriKind.Relative),
            type: EventCatalog.ReverseDns.EpicStartAttemptFailed,
            time: EventTime,
            data: new EpicStartAttemptFailed(issueNumber, "transient failure"),
            subject: EpicNumber(epicId).ToString(),
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                [EventCatalog.Lineage.Epic] = EpicNumber(epicId).ToString(),
            });

    protected static async Task SeedIssueWithPrereqsAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        int[] prereqNumbers)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        foreach (var prereq in prereqNumbers)
            issue.AddPrerequisite(prereq);
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    protected static CloudEvent<IssueCompleted> BuildCompletedEvent(string projectId, string issueId) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: new IssueCompleted("wr_1"),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                [EventCatalog.Lineage.Issue] = "1",
            });

    protected static CloudEvent<IssueCancelled> BuildCancelledEvent(string projectId, string issueId) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: EventTime,
            data: new IssueCancelled("cancel reason"),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                [EventCatalog.Lineage.Issue] = "1",
            });

    protected static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = "project_1",
        string epicId = "epic_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {number}",
            Description = "",
            Priority = "p2",
            Status = status,
            PauseReason = pauseReason,
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    protected static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        Mohist.Server.Issue.Domain.IssueStatus status)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    protected static async Task SeedLinkAsync(TestDatabase database, string epicId, string issueId, int issueNumber)
    {
        await using var db = database.CreateDbContext();
        var projectId = "project_1";
        var epicNumber = EpicNumber(epicId);
        var issueRow = await db.Issues.SingleAsync(row => row.ProjectId == projectId && row.Number == issueNumber);
        var issue = IssueStore.Deserialize(issueRow.State)!;
        issue.AssignEpic(epicNumber);
        issue.ClearPendingEvents();
        issueRow.State = IssueStore.Serialize(issue);
        issueRow.EpicNumber = epicNumber;
        await db.SaveChangesAsync();
    }

    protected static int EpicNumber(string epicKey) => epicKey switch
    {
        "epic_closed" or "epic_done" => 1,
        "epic_running" => 2,
        _ => int.Parse(epicKey.AsSpan("epic_".Length), System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    protected sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    protected sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    protected sealed class TestEpicGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<RecordedGrainCall> Calls { get; } = [];
        public List<string> IssueStartCalls { get; } = [];
        public bool ThrowOnIssueStart { get; init; }

        public TestEpicGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IEpicGrain GetEpicGrain(string grainKey)
        {
            Calls.Add(new RecordedGrainCall(grainKey));
            return new EpicGrain(
                _dbFactory,
                this,
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                new NoopEventStore(),
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };
        }

        private IIssueGrain GetIssueGrain(string issueId) => new TestIssueGrain(this, issueId);

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IEpicGrain))
                return GetEpicGrain(grainPrimaryKey);
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    protected sealed class TestIssueGrain : IIssueGrain
    {
        private readonly TestEpicGrainFactory _owner;
        private readonly string _issueId;

        public TestIssueGrain(TestEpicGrainFactory owner, string issueId)
        {
            _owner = owner;
            _issueId = issueId;
        }

        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null) => throw new NotSupportedException();

        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null)
        {
            _owner.IssueStartCalls.Add(_issueId);
            return _owner.ThrowOnIssueStart
                ? Task.FromException<string>(new InvalidOperationException("selected issue start failure"))
                : Task.FromResult("wr_test");
        }

        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task MarkDoneAsync() => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task CloseCompositeAsync() => throw new NotSupportedException();
        public Task ReopenCompositeAsync() => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task ArchiveForParentCascadeAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task RecomputeCompositeStatusAsync() => throw new NotSupportedException();
        public Task StartCompositeAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string author, string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task<bool> AssignEpicAsync(int epicNumber) => Task.FromResult(true);
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => Task.FromResult(true);
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber)
        {
            _owner.IssueStartCalls.Add(_issueId);
            return _owner.ThrowOnIssueStart
                ? Task.FromException<bool>(new InvalidOperationException("selected issue start failure"))
                : Task.FromResult(true);
        }

        public Task<string?> GetActiveWorkflowRunIdAsync() => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<long> GetRepositoryBindingRevisionAsync() => throw new NotImplementedException();
    }

    public sealed record RecordedGrainCall(string GrainKey);
}
