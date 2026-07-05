using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Tests.Support;
using System.Data;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Grain;

/// <summary>
/// Fake-based specs covering T-003: batch link / unlink with per-issue
/// outcomes, partial-failure semantics, de-duplication, idempotency,
/// the cross-epic active-membership invariant, and the post-commit
/// event persistence inherited from T-001.
/// </summary>
public class EpicBatchMembershipSpecs
{
    private const string ProjectId = "project_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_NewIssues_AllLinked()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);
        await SeedIssueAsync(database, issueId: "issue_c", issueNumber: 3);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
            new BatchMembershipRequestItem("3", "issue_c", 3),
        ], ProjectId);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("linked", o.Status));
        Assert.Equal(new[] { "1", "2", "3" }, outcomes.Select(o => o.Identifier).ToArray());

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        Assert.Equal(3, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_SameInternalIdRequestedTwice_AreDeduplicatedToOneLink()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        // The HTTP layer resolves the two distinct identifier strings
        // ("1" and "issue_a") to the same issue. After dedup they
        // collapse to a single entry pointing to one link attempt — the
        // grain never sees the duplicate. Even if a duplicate did
        // slip through here, the grain's own internal-id dedup means
        // the issue would still be linked at most once.
        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("issue_a", "issue_a", 1),
        ], ProjectId);

        Assert.Single(outcomes);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_IssueInOtherNonTerminalEpic_ReportedAsConflict()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_first", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_second", status: "running", number: 2);
        await SeedIssueAsync(database, issueId: "issue_conflict", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_clean", issueNumber: 2);

        var firstGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_first");
        await firstGrain.LinkIssueAsync("issue_conflict", 1, ProjectId);

        var secondGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_second");
        var outcomes = await secondGrain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_conflict", 1),
            new BatchMembershipRequestItem("2", "issue_clean", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        var conflict = outcomes.Single(o => o.Identifier == "1");
        Assert.Equal("conflict", conflict.Status);
        Assert.Equal("epic_first", conflict.OwningEpicId);
        var clean = outcomes.Single(o => o.Identifier == "2");
        Assert.Equal("linked", clean.Status);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_second")
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal("issue_clean", links[0].IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_AlreadyLinkedIssue_ReportedAsAlreadyLinked_NoDuplicate()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_a", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("already-linked", outcome.Status);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_AllTerminalMemberships_ClaimedWithoutConflict()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_terminal", status: "closed", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_terminal_only", issueNumber: 1);

        var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_terminal");
        await terminalGrain.LinkIssueAsync("issue_terminal_only", 1, ProjectId);

        var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active");
        var outcomes = await activeGrain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_terminal_only", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("linked", outcome.Status);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.IssueId == "issue_terminal_only")
            .ToListAsync();
        Assert.Equal(2, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssuesAsync_RemovesOnlyRequestedMembers_RemainingIntact()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);
        await SeedIssueAsync(database, issueId: "issue_c", issueNumber: 3);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);
        await grain.LinkIssueAsync("issue_b", 2, ProjectId);
        await grain.LinkIssueAsync("issue_c", 3, ProjectId);

        var outcomes = await grain.UnlinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("unlinked", o.Status));

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        var remainingIds = remaining.Select(r => r.IssueId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "issue_c" }, remainingIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssuesAsync_NotMember_ReportedAsWasNotAMember()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);

        var outcomes = await grain.UnlinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal("unlinked", outcomes.First(o => o.Identifier == "1").Status);
        Assert.Equal("was-not-a-member", outcomes.First(o => o.Identifier == "2").Status);

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a")
            .ToListAsync();
        Assert.Empty(remaining);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_EmptyInput_ReturnsEmptyOutcomes()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var outcomes = await grain.LinkIssuesAsync(Array.Empty<BatchMembershipRequestItem>(), ProjectId);

        Assert.Empty(outcomes);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_OnTerminalEpic_RecordsIssueLinkedEvent()
    {
        var store = new RecordingEventStore();
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_t", status: "closed", number: 1);
        await SeedIssueAsync(database, issueId: "issue_t", issueNumber: 1);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var grain = new EpicGrain(
            database.Factory,
            new NullGrainFactory(),
            time,
            store,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:epic_t",
        };

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_t", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("linked", outcome.Status);

        var stored = await store.ListEpicEventsAsync("epic_t");
        var evt = Assert.Single(stored);
        Assert.Equal("com.mohist.epic.issue-linked", evt.Envelope.Type);
        Assert.Contains("issue_t", evt.Envelope.Data.ToString());
        Assert.Equal(time.GetUtcNow(), evt.Envelope.Time);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_WhenActiveMembershipInsertFails_DoesNotPersistIssueLinkedEvent()
    {
        var store = new RecordingEventStore();
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_owner", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_race", issueNumber: 1);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var grain = new EpicGrain(
            database.CreateFactory(new InsertConflictingActiveIssueBeforeSaveInterceptor(ProjectId, "issue_race", "epic_owner", 1)),
            new NullGrainFactory(),
            time,
            store,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:epic_target",
        };

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_race", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("conflict", outcome.Status);
        Assert.Equal("epic_owner", outcome.OwningEpicId);

        var stored = await store.ListEpicEventsAsync("epic_target");
        Assert.Empty(stored);

        await using var verify = database.CreateDbContext();
        var targetLinks = await verify.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == ProjectId && link.EpicId == "epic_target" && link.IssueId == "issue_race")
            .ToListAsync();
        Assert.Empty(targetLinks);
    }

    private static EpicGrain CreateGrain(TestDbContextFactory factory, string grainKey) =>
        new(
            factory,
            new NullGrainFactory(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            new NoopEventStore(),
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = grainKey,
        };

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string epicId = "epic_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {epicId}",
            Description = "",
            Priority = "p2",
            Status = status,
            PauseReason = pauseReason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string issueId = "issue_1",
        int issueNumber = 1,
        IssueStatus status = IssueStatus.Backlog)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            Priority = "p2",
            IsDraft = false,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
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
            GrainTestConfig.MigrateWithSchemaFix(db);
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

        public TestDbContextFactory CreateFactory(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_connection);
            if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
            return new TestDbContextFactory(builder.Options);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class InsertConflictingActiveIssueBeforeSaveInterceptor(
        string projectId,
        string issueId,
        string ownerEpicId,
        int issueNumber) : SaveChangesInterceptor
    {
        private bool _inserted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_inserted || eventData.Context is not MohistDbContext db)
                return result;

            var claimsTargetIssue = db.ChangeTracker.Entries<EpicActiveIssueRow>()
                .Any(entry => entry.State == EntityState.Added
                    && entry.Entity.ProjectId == projectId
                    && entry.Entity.IssueId == issueId
                    && entry.Entity.EpicId != ownerEpicId);
            if (!claimsTargetIssue)
                return result;

            _inserted = true;
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                    VALUES ($projectId, $issueId, $epicId, $issueNumber, $createdAt)
                    """;
                AddParameter(command, "$projectId", projectId);
                AddParameter(command, "$issueId", issueId);
                AddParameter(command, "$epicId", ownerEpicId);
                AddParameter(command, "$issueNumber", issueNumber);
                AddParameter(command, "$createdAt", new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync();
            }

            return result;
        }

        private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class NullGrainFactory : IGrainFactory
    {
        public IEpicGrain GetEpicGrain(string grainKey) => throw new NotSupportedException();
        public IIssueGrain GetIssueGrain(string issueId) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
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
}
