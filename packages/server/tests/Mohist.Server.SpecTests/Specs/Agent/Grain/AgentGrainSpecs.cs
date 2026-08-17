using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

public class AgentGrainSpecs
{
    [Fact]
    public async Task Create_show_update_and_archive_persist_agent_lifecycle()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var grain = CreateGrain(factory, timeProvider, "project_1", "agent_1");

        var instructions = "Review literally {{issue.title}} and ${MODEL}\nDo not render.";
        var config = JsonDocument.Parse("{\"type\":\"opencode\",\"model\":\"openai/gpt-5.5\"}").RootElement.Clone();
        var created = await grain.CreateAsync(new AgentCreateData(
            "project_1",
            "reviewer",
            "Senior reviewer",
            instructions,
            config,
            ["debugging-code", "software-design"],
            2,
            Avatar: "https://example.test/avatar.svg"));

        Assert.Equal("agent_1", created.Id);
        Assert.Equal("project_1", created.ProjectId);
        Assert.Equal("active", created.Status);
        Assert.Equal(instructions, created.Instructions);
        Assert.Equal("https://example.test/avatar.svg", created.Avatar);
        Assert.Equal("openai/gpt-5.5", created.AgentConfig!.Value.GetProperty("model").GetString());
        Assert.Equal(["debugging-code", "software-design"], created.Skills);
        Assert.Equal(2, created.MaxConcurrentRuns);

        var shown = await grain.ShowAsync();
        Assert.NotNull(shown);
        Assert.Equal(created, shown);

        var createdAt = DateTimeOffset.Parse(created.UpdatedAt);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var updatedInstructions = "Keep ${literal} unchanged";
        var updated = await grain.UpdateAsync(new AgentUpdateData(
            "principal-reviewer",
            "Principal reviewer",
            updatedInstructions,
            JsonDocument.Parse("{\"type\":\"opencode\",\"temperature\":0}").RootElement.Clone(),
            ["fsd"],
            3,
            UpdateFields,
            Avatar: "https://example.test/avatar-new.svg"));

        Assert.NotNull(updated);
        Assert.Equal("principal-reviewer", updated.Name);
        Assert.Equal(updatedInstructions, updated.Instructions);
        Assert.Equal("https://example.test/avatar-new.svg", updated.Avatar);
        Assert.Equal(0, updated.AgentConfig!.Value.GetProperty("temperature").GetInt32());
        Assert.Equal(["fsd"], updated.Skills);
        Assert.Equal(3, updated.MaxConcurrentRuns);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        var updatedAt = DateTimeOffset.Parse(updated.UpdatedAt);
        Assert.True(updatedAt > createdAt);
        var persisted = await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_1"));
        Assert.NotNull(persisted);
        Assert.Equal(updatedAt, AgentStore.Deserialize(persisted.State)!.UpdatedAt);

        var archived = await grain.ArchiveAsync();
        Assert.NotNull(archived);
        Assert.Equal(AgentStatus.Archived, archived.Status);
        Assert.NotNull(await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_1")));
    }

    [Fact]
    public async Task Create_rejects_duplicate_names_including_archived_agents()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;

        var first = CreateGrain(factory, "project_1", "agent_1");
        await first.CreateAsync(NewCreate("project_1", "reviewer"));
        await first.ArchiveAsync();

        var second = CreateGrain(factory, "project_1", "agent_2");
        await Assert.ThrowsAsync<AgentNameConflictException>(() => second.CreateAsync(NewCreate("project_1", "reviewer")));

        var archived = await first.ShowAsync();
        Assert.Equal(AgentStatus.Archived, archived!.Status);
    }

    [Fact]
    public async Task TaskFirstCreate_is_first_writer_wins_and_adopts_only_matching_fingerprint()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;
        var grain = CreateGrain(factory, "project_1", "agent_task_first");

        var first = await grain.CreateAsync(NewCreate(
            "project_1",
            "first",
            taskFirstIdempotencyKey: "same-key",
            taskFirstRequestFingerprint: "first-fingerprint"));
        var replay = await grain.CreateAsync(NewCreate(
            "project_1",
            "different-name",
            taskFirstIdempotencyKey: "same-key",
            taskFirstRequestFingerprint: "first-fingerprint"));

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Name, replay.Name);
        Assert.Equal(first.Instructions, replay.Instructions);
        await Assert.ThrowsAsync<AgentTaskIdempotencyConflictException>(() => grain.CreateAsync(NewCreate(
            "project_1",
            "different-name",
            taskFirstIdempotencyKey: "same-key",
            taskFirstRequestFingerprint: "changed-fingerprint")));
    }

    [Fact]
    public async Task Create_establishes_agent_principal_that_outlives_archive()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;
        var grain = CreateGrain(factory, "project_1", "agent_principal_1");

        await grain.CreateAsync(NewCreate("project_1", "principal-agent"));
        var principal = await factory.CreateDbContext().Principals.SingleAsync(row => row.Id == "agent_principal_1");
        Assert.Equal("Agent", principal.Kind);
        Assert.Equal("principal-agent", principal.Name);

        // Archiving the agent must not remove the attribution anchor:
        // historical activity keeps pointing at the principal.
        await grain.ArchiveAsync();
        var afterArchive = await factory.CreateDbContext().Principals.SingleAsync(row => row.Id == "agent_principal_1");
        Assert.Equal("principal-agent", afterArchive.Name);
    }

    [Fact]
    public async Task EnsureAgentPrincipal_is_idempotent_and_keeps_first_name()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;
        var store = new PrincipalStore(factory, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)));

        await store.EnsureAgentPrincipalAsync("agent_x", "first-name");
        await store.EnsureAgentPrincipalAsync("agent_x", "second-name");

        await using var db = factory.CreateDbContext();
        var principal = Assert.Single(db.Principals.Where(row => row.Id == "agent_x"));
        Assert.Equal("first-name", principal.Name);
    }

    [Fact]
    public async Task Rename_rejects_project_name_conflict_without_changing_existing_name()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;

        var first = CreateGrain(factory, "project_1", "agent_1");
        var second = CreateGrain(factory, "project_1", "agent_2");
        await first.CreateAsync(NewCreate("project_1", "reviewer"));
        await second.CreateAsync(NewCreate("project_1", "coder"));

        await Assert.ThrowsAsync<AgentNameConflictException>(() =>
            second.UpdateAsync(new AgentUpdateData("reviewer", null, null, null, null, null, new HashSet<string> { nameof(AgentUpdateData.Name) })));

        var unchanged = await second.ShowAsync();
        Assert.Equal("coder", unchanged!.Name);
    }

    [Fact]
    public async Task Same_agent_id_in_different_projects_uses_distinct_grain_keys()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;

        var first = CreateGrain(factory, "project_1", "agent_same");
        var second = CreateGrain(factory, "project_2", "agent_same");

        await first.CreateAsync(NewCreate("project_1", "reviewer"));
        await second.CreateAsync(NewCreate("project_2", "reviewer"));

        Assert.Equal("project_1", (await first.ShowAsync())!.ProjectId);
        Assert.Equal("project_2", (await second.ShowAsync())!.ProjectId);
        Assert.Equal(2, await context.Agents.CountAsync());
    }

    [Fact]
    public async Task Archive_then_unarchive_round_trips_agent_to_active_and_advances_updated_at()
    {
        var database = CreateModelSchemaDatabase();
        await using (database)
        {
            await using var context = database.CreateDbContext();
            var factory = database.Factory;
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var grain = CreateGrain(factory, timeProvider, "project_1", "agent_1");

            await grain.CreateAsync(NewCreate("project_1", "reviewer"));
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            var archived = await grain.ArchiveAsync();
            Assert.NotNull(archived);
            Assert.Equal(AgentStatus.Archived, archived.Status);
            var archivedAt = DateTimeOffset.Parse(archived.UpdatedAt);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            var unarchived = await grain.UnarchiveAsync();
            Assert.NotNull(unarchived);
            Assert.Equal(AgentStatus.Active, unarchived.Status);
            var unarchivedAt = DateTimeOffset.Parse(unarchived.UpdatedAt);
            Assert.True(unarchivedAt > archivedAt, "Unarchive should advance UpdatedAt past the archive time");

            var stored = await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_1"));
            Assert.NotNull(stored);
            var deserialized = AgentStore.Deserialize(stored.State);
            Assert.NotNull(deserialized);
            Assert.Equal(AgentStatus.Active, deserialized!.Status);
            Assert.Equal(unarchivedAt, deserialized.UpdatedAt);
        }
    }

    [Fact]
    public async Task Unarchive_of_already_active_agent_is_a_no_op_without_advancing_updated_at_or_persisting()
    {
        var database = CreateModelSchemaDatabase();
        await using (database)
        {
            await using var context = database.CreateDbContext();
            var factory = database.Factory;
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var grain = CreateGrain(factory, timeProvider, "project_1", "agent_1");

            var created = await grain.CreateAsync(NewCreate("project_1", "reviewer"));
            var originalUpdatedAt = DateTimeOffset.Parse(created.UpdatedAt);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            var result = await grain.UnarchiveAsync();
            Assert.NotNull(result);
            Assert.Equal(AgentStatus.Active, result.Status);
            Assert.Equal(created.Id, result.Id);
            Assert.Equal(originalUpdatedAt, DateTimeOffset.Parse(result.UpdatedAt));

            var stored = await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_1"));
            Assert.NotNull(stored);
            var deserialized = AgentStore.Deserialize(stored.State);
            Assert.NotNull(deserialized);
            Assert.Equal(originalUpdatedAt, deserialized!.UpdatedAt);
        }
    }

    [Fact]
    public async Task Unarchive_of_unknown_agent_returns_null_and_creates_no_row()
    {
        var database = CreateModelSchemaDatabase();
        await using (database)
        {
            await using var context = database.CreateDbContext();
            var factory = database.Factory;
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var grain = CreateGrain(factory, timeProvider, "project_1", "agent_missing");

            var result = await grain.UnarchiveAsync();

            Assert.Null(result);
            Assert.Null(await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_missing")));
        }
    }

    private static AgentGrain CreateGrain(TestDbContextFactory factory, string projectId, string agentId)
    {
        return CreateGrain(factory, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)), projectId, agentId);
    }

    private static AgentGrain CreateGrain(TestDbContextFactory factory, FakeTimeProvider timeProvider, string projectId, string agentId)
    {
        var identity = GrainTestContext.Create(GrainKey.Agent(projectId, agentId));
        return new AgentGrain(
            identity.Context,
            identity.Runtime,
            new AgentStore(factory),
            new AgentQuerier(factory),
            timeProvider,
            new PrincipalStore(factory, timeProvider));
    }

    private static AgentCreateData NewCreate(
        string projectId,
        string name,
        string? taskFirstIdempotencyKey = null,
        string? taskFirstRequestFingerprint = null) => new(
        projectId,
        name,
        null,
        "instructions",
        JsonDocument.Parse("{\"type\":\"opencode\"}").RootElement.Clone(),
        [],
        null,
        TaskFirstIdempotencyKey: taskFirstIdempotencyKey,
        TaskFirstRequestFingerprint: taskFirstRequestFingerprint);

    private static readonly HashSet<string> UpdateFields =
    [
        nameof(AgentUpdateData.Name),
        nameof(AgentUpdateData.Description),
        nameof(AgentUpdateData.Instructions),
        nameof(AgentUpdateData.AgentConfig),
        nameof(AgentUpdateData.Skills),
        nameof(AgentUpdateData.MaxConcurrentRuns),
        nameof(AgentUpdateData.Avatar),
    ];

    private static TestDatabase CreateModelSchemaDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyModelSchemaTo(connection);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, new TestDbContextFactory(options));
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
}
