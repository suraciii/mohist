using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Tests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Services;

/// <summary>
/// Covers the reconciliation sweep's candidate selection against
/// <see cref="IssueWorkflowReconciliationService"/>. The sweep must
/// exclude archived issues at the SQL layer (a Done/archived issue
/// with a preserved <c>workflowRunId</c> is an execution fact, not a
/// stuck-run signal) and must still reach <c>InProgress</c> issues.
/// Spec: <c>openspec/changes/issue-264/specs/issue-workflow-run-reference/spec.md#background-reconciliation-skips-non-in-progress-issues</c>.
/// </summary>
public class IssueWorkflowReconciliationServiceSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReconcileOnceAsync_ArchivedDoneIssue_IsNotSelectedAsCandidate()
    {
        // Archived Done issues preserve their workflowRunId (execution
        // fact), but the sweep must not pull them as stuck-run candidates.
        // Without the status SQL filter, this issue would be reached
        // because workflowRunId != null.
        await using var database = CreateDatabase();
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_archived",
            issueNumber: 1,
            status: IssueStatus.Done,
            workflowRunId: "wr_archived",
            archivedAt: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc));

        var grains = new RecordingIssueGrainFactory(database.Factory);
        var service = new IssueWorkflowReconciliationService(
            database.Factory, grains, NullLogger<IssueWorkflowReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReconcileOnceAsync_InProgressIssueWithReference_IsStillSwept()
    {
        // The InProgress issue with a non-null workflowRunId is exactly
        // what the sweep is meant to catch. It must still be selected by
        // the candidate query after tightening it to InProgress rows.
        await using var database = CreateDatabase();
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_active",
            issueNumber: 1,
            status: IssueStatus.InProgress,
            workflowRunId: "wr_active");

        var grains = new RecordingIssueGrainFactory(database.Factory);
        var service = new IssueWorkflowReconciliationService(
            database.Factory, grains, NullLogger<IssueWorkflowReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Single(grains.Calls);
        Assert.Equal("issue_active", grains.Calls[0].GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReconcileOnceAsync_MixedCandidates_OnlyInProgressReachesGrain()
    {
        // Mix of rows: only InProgress is a stuck-run candidate. Done rows
        // keep workflowRunId as historical data and must not be swept.
        await using var database = CreateDatabase();
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_active",
            issueNumber: 1,
            status: IssueStatus.InProgress,
            workflowRunId: "wr_active");
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_done",
            issueNumber: 2,
            status: IssueStatus.Done,
            workflowRunId: "wr_done");
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_archived",
            issueNumber: 3,
            status: IssueStatus.Done,
            workflowRunId: "wr_archived",
            archivedAt: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc));

        var grains = new RecordingIssueGrainFactory(database.Factory);
        var service = new IssueWorkflowReconciliationService(
            database.Factory, grains, NullLogger<IssueWorkflowReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Single(grains.Calls);
        Assert.Contains(grains.Calls, c => c.GrainKey == "issue_active");
        Assert.DoesNotContain(grains.Calls, c => c.GrainKey == "issue_done");
        Assert.DoesNotContain(grains.Calls, c => c.GrainKey == "issue_archived");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReconcileOnceAsync_NoCandidates_DoesNotInvokeGrain()
    {
        await using var database = CreateDatabase();
        // Seed only an archived Done issue, which the SQL filter must
        // exclude. The sweep then finds nothing.
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_archived",
            issueNumber: 1,
            status: IssueStatus.Done,
            workflowRunId: "wr_archived",
            archivedAt: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc));

        var grains = new RecordingIssueGrainFactory(database.Factory);
        var service = new IssueWorkflowReconciliationService(
            database.Factory, grains, NullLogger<IssueWorkflowReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ReconcileOnceAsync_RepeatedRuns_AreIdempotent()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(
            database,
            projectId: "project_1",
            issueId: "issue_active",
            issueNumber: 1,
            status: IssueStatus.InProgress,
            workflowRunId: "wr_active");

        var grains = new RecordingIssueGrainFactory(database.Factory);
        var service = new IssueWorkflowReconciliationService(
            database.Factory, grains, NullLogger<IssueWorkflowReconciliationService>.Instance);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        // Each sweep walks the same candidate set; the per-grain
        // GetWorkflowStatusAsync is idempotent. We only assert the
        // candidate selection is stable (one reach per sweep).
        Assert.Equal(3, grains.Calls.Count);
        Assert.All(grains.Calls, c => Assert.Equal("issue_active", c.GrainKey));
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        IssueStatus status,
        string workflowRunId,
        DateTime? archivedAt = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            WorkflowRunId = workflowRunId,
            ArchivedAt = archivedAt,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            WorkflowRunId = workflowRunId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.Migrate();
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
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

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class RecordingIssueGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<RecordedGrainCall> Calls { get; } = [];

        public RecordingIssueGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IIssueGrain GetIssueGrain(string grainKey)
        {
            Calls.Add(new RecordedGrainCall(grainKey));
            return new NoopIssueGrain();
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
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

    public sealed record RecordedGrainCall(string GrainKey);

    private sealed class NoopIssueGrain : IIssueGrain
    {
        public Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null) => throw new NotSupportedException();
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => Task.FromResult<IssueWorkflowStatus?>(null);
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
    }
}
