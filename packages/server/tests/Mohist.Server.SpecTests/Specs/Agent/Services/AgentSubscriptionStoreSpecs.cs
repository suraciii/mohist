using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public class AgentSubscriptionStoreSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task CreateAsync_PersistsActiveSubscriptionWithProjectAgentAndTimestamps()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        var now = StartTime;

        var persisted = await store.CreateAsync(new AgentSubscription
        {
            Id = "subs_1",
            ProjectId = "proj_a",
            AgentId = "agent_review",
            Name = "plan-approvals",
            Filter = new SubscriptionFilter
            {
                Type = "com.mohist.workflow.stage.*",
                Source = "/mohist/workflow-runs/run_abc",
            },
            ResponsePrompt = "review and approve if plan looks clear",
            Priority = 5,
        });

        Assert.Equal(SubscriptionStatus.Active, persisted.Status);
        Assert.Equal(now, persisted.CreatedAt);
        Assert.Equal(now, persisted.UpdatedAt);
        var row = await ReadRowAsync(database, "subs_1");
        Assert.Equal("proj_a", row.ProjectId);
        Assert.Equal("agent_review", row.AgentId);
        Assert.Equal("plan-approvals", row.Name);
        Assert.Equal("com.mohist.workflow.stage.*", row.FilterType);
        Assert.Equal("/mohist/workflow-runs/run_abc", row.FilterSource);
        Assert.Null(row.FilterSubject);
        Assert.Equal(5, row.Priority);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task CreateAsync_PriorityNull_PersistsAsNull()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.CreateAsync(NewSubscription("subs_a", "default-priority", priority: null));

        var row = await ReadRowAsync(database, "subs_a");
        Assert.Null(row.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task CreateAsync_DuplicateNameOnSameAgent_ThrowsNameConflict()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "shared-name"));

        var ex = await Assert.ThrowsAsync<AgentSubscriptionNameConflictException>(() =>
            store.CreateAsync(NewSubscription("subs_2", "shared-name")));
        Assert.Equal("agent_review", ex.AgentId);
        Assert.Equal("shared-name", ex.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task CreateAsync_SameNameOnDifferentAgents_BothSucceed()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "shared", agentId: "agent_a"));
        await store.CreateAsync(NewSubscription("subs_2", "shared", agentId: "agent_b"));

        var rows = await ReadAllRowsAsync(database);
        Assert.Equal(2, rows.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task GetAsync_ReturnsPersistedSubscription()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_x", "watch"));

        var loaded = await store.GetAsync("subs_x");

        Assert.NotNull(loaded);
        Assert.Equal("subs_x", loaded!.Id);
        Assert.Equal("agent_review", loaded.AgentId);
        Assert.Equal("watch", loaded.Name);
        Assert.Equal("com.mohist.workflow.stage.*", loaded.Filter.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task GetAsync_MissingId_ReturnsNull()
    {
        await using var database = CreateDatabase();
        var store = NewStore(database);

        var loaded = await store.GetAsync("subs_missing");

        Assert.Null(loaded);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ListByAgentAsync_ReturnsOrderedByUpdatedAtDescending()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);
        await store.CreateAsync(NewSubscription("subs_old", "older"));
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await store.CreateAsync(NewSubscription("subs_newer", "newer"));

        var listed = await store.ListByAgentAsync("proj_a", "agent_review");

        Assert.Equal(new[] { "subs_newer", "subs_old" }, listed.Select(s => s.Id).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ListByAgentAsync_FiltersOutOtherAgents()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_a", "a-on-agent-x", agentId: "agent_x"));
        await store.CreateAsync(NewSubscription("subs_b", "b-on-agent-y", agentId: "agent_y"));

        var listed = await store.ListByAgentAsync("proj_a", "agent_x");

        var only = Assert.Single(listed);
        Assert.Equal("subs_a", only.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ListByProjectAsync_ReturnsAllSubscriptionsForTheProject()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        await SeedProjectAsync(database, "proj_b");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_p1", "p1-a", projectId: "proj_a", agentId: "agent_1"));
        await store.CreateAsync(NewSubscription("subs_p2", "p1-b", projectId: "proj_a", agentId: "agent_2"));
        await store.CreateAsync(NewSubscription("subs_p3", "p2-a", projectId: "proj_b", agentId: "agent_1"));

        var listed = await store.ListByProjectAsync("proj_a");

        Assert.Equal(2, listed.Count);
        Assert.All(listed, s => Assert.Equal("proj_a", s.ProjectId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task UpdateAsync_ChangesMutableFieldsAndAdvancesUpdatedAt()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);
        await store.CreateAsync(NewSubscription("subs_1", "before-name", priority: 1));
        var before = (await ReadRowAsync(database, "subs_1")).UpdatedAt;
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var updated = await store.UpdateAsync(
            "subs_1",
            name: "after-name",
            filter: new SubscriptionFilter
            {
                Type = "com.mohist.issue.completed",
                Source = "/mohist/issues/issue_99",
                Subject = "42",
            },
            responsePrompt: "after-prompt",
            priority: 9,
            priorityTouched: true);

        Assert.NotNull(updated);
        Assert.Equal("after-name", updated!.Name);
        Assert.Equal("com.mohist.issue.completed", updated.Filter.Type);
        Assert.Equal("/mohist/issues/issue_99", updated.Filter.Source);
        Assert.Equal("42", updated.Filter.Subject);
        Assert.Equal("after-prompt", updated.ResponsePrompt);
        Assert.Equal(9, updated.Priority);
        var row = await ReadRowAsync(database, "subs_1");
        Assert.True(row.UpdatedAt > before, "UpdatedAt must advance on update");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task UpdateAsync_OnlyTouchesRequestedFields()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "stable", priority: 7));

        var updated = await store.UpdateAsync(
            "subs_1",
            name: null,
            filter: null,
            responsePrompt: "new-prompt",
            priority: null,
            priorityTouched: false);

        Assert.Equal("stable", updated!.Name);
        Assert.Equal(7, updated.Priority);
        Assert.Equal("new-prompt", updated.ResponsePrompt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task UpdateAsync_PriorityReset_SetsNull()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "reset", priority: 3));

        var updated = await store.UpdateAsync(
            "subs_1",
            name: null,
            filter: null,
            responsePrompt: null,
            priority: null,
            priorityTouched: true);

        Assert.Null(updated!.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task UpdateAsync_DuplicateName_ThrowsNameConflict()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "first-name"));
        await store.CreateAsync(NewSubscription("subs_2", "second-name"));

        var ex = await Assert.ThrowsAsync<AgentSubscriptionNameConflictException>(() =>
            store.UpdateAsync("subs_2", name: "first-name", filter: null, responsePrompt: null, priority: null, priorityTouched: false));
        Assert.Equal("first-name", ex.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task UpdateAsync_MissingId_ReturnsNull()
    {
        await using var database = CreateDatabase();
        var store = NewStore(database);

        var updated = await store.UpdateAsync(
            "subs_missing",
            name: "anything",
            filter: null,
            responsePrompt: null,
            priority: null,
            priorityTouched: false);

        Assert.Null(updated);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ArchiveAsync_SetsArchivedAndAdvancesUpdatedAt()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);
        await store.CreateAsync(NewSubscription("subs_1", "archiveable"));
        var before = (await ReadRowAsync(database, "subs_1")).UpdatedAt;
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var archived = await store.ArchiveAsync("subs_1");

        Assert.Equal(SubscriptionStatus.Archived, archived!.Status);
        Assert.True(archived.UpdatedAt > before);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ArchiveAsync_AlreadyArchived_IsIdempotentAndDoesNotAdvanceUpdatedAt()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);
        await store.CreateAsync(NewSubscription("subs_1", "double-archive"));
        await store.ArchiveAsync("subs_1");
        var snapshot = (await ReadRowAsync(database, "subs_1")).UpdatedAt;
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var archived = await store.ArchiveAsync("subs_1");

        Assert.Equal(SubscriptionStatus.Archived, archived!.Status);
        var row = await ReadRowAsync(database, "subs_1");
        Assert.Equal(snapshot, row.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task RestoreAsync_SetsActiveAndAdvancesUpdatedAt()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);
        await store.CreateAsync(NewSubscription("subs_1", "restore-me"));
        await store.ArchiveAsync("subs_1");
        var before = (await ReadRowAsync(database, "subs_1")).UpdatedAt;
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        var restored = await store.RestoreAsync("subs_1");

        Assert.Equal(SubscriptionStatus.Active, restored!.Status);
        Assert.True(restored.UpdatedAt > before);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task DeleteAsync_RemovesRowAndReturnsTrue()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_1", "delete-me"));

        var removed = await store.DeleteAsync("subs_1");

        Assert.True(removed);
        Assert.Null(await ReadOptionalRowAsync(database, "subs_1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task DeleteAsync_MissingId_ReturnsFalse()
    {
        await using var database = CreateDatabase();
        var store = NewStore(database);

        var removed = await store.DeleteAsync("subs_missing");

        Assert.False(removed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ListByAgentAsync_IncludesArchivedRowsAlongsideActive()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);
        await store.CreateAsync(NewSubscription("subs_active", "still-active"));
        await store.CreateAsync(NewSubscription("subs_archived", "will-archive"));
        await store.ArchiveAsync("subs_archived");

        var listed = await store.ListByAgentAsync("proj_a", "agent_review");

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, s => s.Id == "subs_active" && s.Status == SubscriptionStatus.Active);
        Assert.Contains(listed, s => s.Id == "subs_archived" && s.Status == SubscriptionStatus.Archived);
    }

    private static AgentSubscription NewSubscription(
        string id,
        string name,
        string projectId = "proj_a",
        string agentId = "agent_review",
        int? priority = 0) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            Name = name,
            Filter = new SubscriptionFilter
            {
                Type = "com.mohist.workflow.stage.*",
                Source = "/mohist/workflow-runs/run_abc",
            },
            ResponsePrompt = $"prompt for {name}",
            Priority = priority,
        };

    private static AgentSubscriptionStore NewStore(
        TestDatabase database,
        FakeTimeProvider? timeProvider = null) =>
        new(database.Factory, timeProvider ?? new FakeTimeProvider(StartTime));

    private static async Task SeedProjectAsync(TestDatabase database, string projectId)
    {
        await using var db = database.CreateDbContext();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId.Replace('_', '-'),
            RepositoriesJson = """[{"name":"test-repo","gitUrl":"git@example.com:test-repo.git","baseBranch":"main","isDefault":true}]""",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<AgentSubscriptionRow> ReadRowAsync(TestDatabase database, string id)
    {
        var row = await ReadOptionalRowAsync(database, id);
        Assert.NotNull(row);
        return row!;
    }

    private static async Task<AgentSubscriptionRow?> ReadOptionalRowAsync(TestDatabase database, string id)
    {
        await using var db = database.CreateDbContext();
        return await db.AgentSubscriptions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    private static async Task<List<AgentSubscriptionRow>> ReadAllRowsAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.AgentSubscriptions.AsNoTracking().ToListAsync();
    }

    private static TestDatabase CreateDatabase()
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
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }
}
