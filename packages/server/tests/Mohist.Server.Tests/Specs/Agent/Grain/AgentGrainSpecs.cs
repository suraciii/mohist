using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Agent.Grain;

public class AgentGrainSpecs
{
    [Fact]
    public async Task Create_show_update_and_archive_persist_agent_lifecycle()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
        var factory = database.Factory;
        var grain = CreateGrain(factory, "project_1", "agent_1");

        var instructions = "Review literally {{issue.title}} and ${MODEL}\nDo not render.";
        var config = JsonDocument.Parse("{\"type\":\"opencode\",\"model\":\"openai/gpt-5.5\"}").RootElement.Clone();
        var created = await grain.CreateAsync(new AgentCreateData(
            "project_1",
            "reviewer",
            "Senior reviewer",
            instructions,
            config,
            ["debugging-code", "software-design"],
            2));

        Assert.Equal("agent_1", created.Id);
        Assert.Equal("project_1", created.ProjectId);
        Assert.Equal("active", created.Status);
        Assert.Equal(instructions, created.Instructions);
        Assert.Equal("openai/gpt-5.5", created.AgentConfig!.Value.GetProperty("model").GetString());
        Assert.Equal(["debugging-code", "software-design"], created.Skills);
        Assert.Equal(2, created.MaxConcurrentRuns);

        var shown = await grain.ShowAsync();
        Assert.NotNull(shown);
        Assert.Equal(created, shown);

        var updatedInstructions = "Keep ${literal} unchanged";
        var updated = await grain.UpdateAsync(new AgentUpdateData(
            "principal-reviewer",
            "Principal reviewer",
            updatedInstructions,
            JsonDocument.Parse("{\"type\":\"opencode\",\"temperature\":0}").RootElement.Clone(),
            ["fsd"],
            3,
            UpdateFields));

        Assert.NotNull(updated);
        Assert.Equal("principal-reviewer", updated.Name);
        Assert.Equal(updatedInstructions, updated.Instructions);
        Assert.Equal(0, updated.AgentConfig!.Value.GetProperty("temperature").GetInt32());
        Assert.Equal(["fsd"], updated.Skills);
        Assert.Equal(3, updated.MaxConcurrentRuns);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);

        var archived = await grain.ArchiveAsync();
        Assert.NotNull(archived);
        Assert.Equal(AgentStatus.Archived, archived.Status);
        Assert.NotNull(await factory.CreateDbContext().Agents.FindAsync(GrainKey.Agent("project_1", "agent_1")));
    }

    [Fact]
    public async Task Create_rejects_duplicate_names_including_archived_agents()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
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
    public async Task Rename_rejects_project_name_conflict_without_changing_existing_name()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
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
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
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
    public async Task AddAgentsTable_migration_applies_and_rolls_back_cleanly()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("20260616062153_AddIssueRiskColumn");
        Assert.False(await TableExistsAsync(context, "Agents"));
        Assert.True(await TableExistsAsync(context, "Issues"));

        await migrator.MigrateAsync("20260618100150_AddAgentsTable");
        Assert.True(await TableExistsAsync(context, "Agents"));
        Assert.Equal(0, await CountForeignKeysAsync(context, "Agents"));

        await migrator.MigrateAsync("20260616062153_AddIssueRiskColumn");
        Assert.False(await TableExistsAsync(context, "Agents"));
        Assert.True(await TableExistsAsync(context, "Issues"));
    }

    private static AgentGrain CreateGrain(TestDbContextFactory factory, string projectId, string agentId)
    {
        var grain = new AgentGrain(new AgentStore(factory), new AgentQuerier(factory))
        {
            GrainKeyForTest = GrainKey.Agent(projectId, agentId),
        };
        return grain;
    }

    private static AgentCreateData NewCreate(string projectId, string name) => new(
        projectId,
        name,
        null,
        "instructions",
        JsonDocument.Parse("{\"type\":\"opencode\"}").RootElement.Clone(),
        [],
        null);

    private static readonly HashSet<string> UpdateFields =
    [
        nameof(AgentUpdateData.Name),
        nameof(AgentUpdateData.Description),
        nameof(AgentUpdateData.Instructions),
        nameof(AgentUpdateData.AgentConfig),
        nameof(AgentUpdateData.Skills),
        nameof(AgentUpdateData.MaxConcurrentRuns),
    ];

    private static async Task<bool> TableExistsAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<int> CountForeignKeysAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({tableName})";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync()) count++;
        return count;
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
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
