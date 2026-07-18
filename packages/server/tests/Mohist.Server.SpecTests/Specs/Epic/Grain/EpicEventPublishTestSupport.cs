using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

internal sealed record AffiliationCall(string IssueKey, int? EpicNumber, bool IsLink);

internal sealed class RecordingGrainFactory : IGrainFactory
{
    public List<AffiliationCall> AffiliationCalls { get; } = [];

    public IIssueGrain GetIssueGrain(string issueId) => new RecordingIssueGrain(this, issueId);

    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey
    {
        if (typeof(TGrainInterface) == typeof(IIssueGrain))
            return (TGrainInterface)(object)GetIssueGrain(primaryKey);
        throw new NotSupportedException(typeof(TGrainInterface).FullName);
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
    {
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

internal sealed class RecordingIssueGrain : IIssueGrain
{
    private readonly RecordingGrainFactory _owner;

    public RecordingIssueGrain(RecordingGrainFactory owner, string issueId)
    {
        _owner = owner;
        IssueId = issueId;
    }

    public string IssueId { get; }

    public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null) => throw new NotSupportedException();
    public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
    public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
    public Task CancelAsync() => throw new NotSupportedException();
    public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
    public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
    public Task ArchiveAsync() => throw new NotSupportedException();
    public Task UnarchiveAsync() => throw new NotSupportedException();
    public Task ReopenAsync() => throw new NotSupportedException();
    public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
    public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
    public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
    public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
    public Task RecomputeCompositeStatusAsync() => throw new NotSupportedException();
        public Task StartCompositeAsync() => throw new NotSupportedException();
    public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
    public Task DeactivateForTestAsync() => throw new NotSupportedException();

    public Task<bool> AssignEpicAsync(int epicNumber)
    {
        _owner.AffiliationCalls.Add(new AffiliationCall(IssueId, epicNumber, IsLink: true));
        return Task.FromResult(true);
    }

    public Task<bool> RemoveEpicAsync(int expectedEpicNumber)
    {
        _owner.AffiliationCalls.Add(new AffiliationCall(IssueId, null, IsLink: false));
        return Task.FromResult(true);
    }

    public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => Task.FromResult(true);

    public Task<string?> GetActiveWorkflowRunIdAsync() => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<long> GetRepositoryBindingRevisionAsync() => throw new NotImplementedException();
}

internal sealed class ThrowingAffiliationGrainFactory : IGrainFactory
{
    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey
    {
        if (typeof(TGrainInterface) == typeof(IIssueGrain))
            return (TGrainInterface)(object)new ThrowingIssueGrain();
        throw new NotSupportedException(typeof(TGrainInterface).FullName);
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
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

internal sealed class ThrowingIssueGrain : IIssueGrain
{
    public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null) => throw new NotSupportedException();
    public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
    public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
    public Task CancelAsync() => throw new NotSupportedException();
    public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
    public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
    public Task ArchiveAsync() => throw new NotSupportedException();
    public Task UnarchiveAsync() => throw new NotSupportedException();
    public Task ReopenAsync() => throw new NotSupportedException();
    public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
    public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
    public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
    public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
    public Task RecomputeCompositeStatusAsync() => throw new NotSupportedException();
        public Task StartCompositeAsync() => throw new NotSupportedException();
    public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
    public Task DeactivateForTestAsync() => throw new NotSupportedException();
    public Task<bool> AssignEpicAsync(int epicNumber) =>
        Task.FromException<bool>(new InvalidOperationException("simulated Issue command failure"));
    public Task<bool> RemoveEpicAsync(int expectedEpicNumber) =>
        Task.FromException<bool>(new InvalidOperationException("simulated Issue command failure"));
    public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) =>
        Task.FromException<bool>(new InvalidOperationException("simulated Issue command failure"));

    public Task<string?> GetActiveWorkflowRunIdAsync() => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
    public Task<long> GetRepositoryBindingRevisionAsync() => throw new NotImplementedException();
}

internal static class EpicEventPublishTestSupport
{
    public static (TestDatabase Database, RecordingEventStore EventStore) CreateDatabaseWithRecordingEventStore()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return (new TestDatabase(connection, factory), new RecordingEventStore());
    }

    public static TestDatabase CreateDatabase()
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

    public static EpicGrain CreateGrain(
        TestDbContextFactory factory,
        string grainKey,
        IEventStore eventStore,
        FakeTimeProvider timeProvider,
        IGrainFactory? grains = null) => new(
            factory,
            grains ?? new StubGrainFactory(),
            timeProvider,
            eventStore,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = grainKey,
        };

    public static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = "project_1",
        int epicNumber = 1,
        string status = "idle",
        string priority = "p1")
    {
        var time = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            ProjectId = projectId,
            Number = epicNumber,
            Title = $"Epic {epicNumber}",
            Description = "",
            Priority = priority,
            Status = status,
            PauseReason = null,
            CreatedAt = time,
            UpdatedAt = time,
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId = "project_1",
        int issueNumber = 1,
        int? epicNumber = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            Priority = "p2",
            IsDraft = false,
            EpicNumber = epicNumber,
        };
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            EpicNumber = epicNumber,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedLinkAsync(TestDatabase database, int issueNumber)
    {
        await using var db = database.CreateDbContext();
        var row = await db.Issues.SingleAsync(issue =>
            issue.ProjectId == "project_1" && issue.Number == issueNumber);
        var issue = IssueStore.Deserialize(row.State);
        if (issue is null) throw new InvalidOperationException("Seeded Issue state could not be deserialized.");
        issue!.AssignEpic(1);
        issue.ClearPendingEvents();
        row.EpicNumber = issue.EpicNumber;
        row.State = IssueStore.Serialize(issue);
        await db.SaveChangesAsync();
    }
}

internal sealed class TestDatabase : IAsyncDisposable
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

internal sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
{
    public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

    public DbContextOptions<MohistDbContext> Options { get; }

    public MohistDbContext CreateDbContext() => new(Options);
}

internal sealed class ThrowingEventStore : IEventStore
{
    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) =>
        throw new InvalidOperationException("simulated IEventStore failure");

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default) =>
        throw new InvalidOperationException("simulated IEventStore failure");

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
    public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
}

internal sealed class StubGrainFactory : IGrainFactory
{
    private int _counter;

    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey
    {
        if (typeof(TGrainInterface) == typeof(IEpicCounterGrain))
            return (TGrainInterface)(object)new StubEpicCounterGrain(() => Interlocked.Increment(ref _counter));
        throw new NotSupportedException(typeof(TGrainInterface).FullName);
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
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

internal sealed class StubEpicCounterGrain : IEpicCounterGrain
{
    private readonly Func<int> _next;

    public StubEpicCounterGrain(Func<int> next) => _next = next;

    public Task<int> NextAsync() => Task.FromResult(_next());
}
