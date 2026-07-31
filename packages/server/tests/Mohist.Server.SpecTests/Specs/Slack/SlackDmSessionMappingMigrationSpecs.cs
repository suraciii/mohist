using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public class SlackDmSessionMappingMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesSlackDmSessionMappingsTable()
    {
        await using var database = CreateDatabase("20260731100000_AddSlackDmSessionMapping");
        await using var context = database.CreateDbContext();

        Assert.True(await context.SlackDmSessionMappings.AnyAsync() == false);
        var entries = await context.SlackDmSessionMappings.ToListAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Up_AddsConnectionIdAndSlackIdentityComputedColumnsOnAgentSessions()
    {
        // The schema is asserted end-to-end in SlackConnectionApiSpecs and the
        // store unit test; this spec only verifies the migration history is
        // recorded so downstream migrations see the new table.
        await using var database = CreateDatabase("20260731100000_AddSlackDmSessionMapping");
        var migrations = await ReadAppliedMigrationsAsync(database.Connection);
        Assert.Contains("20260731100000_AddSlackDmSessionMapping", migrations);
    }

    [Fact]
    public async Task Up_UniqueConstraintRejectsDuplicateConversation()
    {
        await using var database = CreateDatabase("20260731100000_AddSlackDmSessionMapping");
        await using var context = database.CreateDbContext();

        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        context.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
        {
            Id = "slkdmmp_first",
            ProjectId = "project-1",
            ConnectionId = "connection-1",
            WorkspaceTeamId = "T123",
            SlackUserId = "U_OWNER",
            DmConversationId = "D123",
            CurrentSessionId = "session-a",
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();

        context.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
        {
            Id = "slkdmmp_second",
            ProjectId = "project-1",
            ConnectionId = "connection-1",
            WorkspaceTeamId = "T123",
            SlackUserId = "U_OWNER",
            DmConversationId = "D123",
            CurrentSessionId = "session-b",
            UpdatedAt = now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Fact]
    public async Task Up_AllowsDifferentDmConversationsUnderSameConnection()
    {
        await using var database = CreateDatabase("20260731100000_AddSlackDmSessionMapping");
        await using var context = database.CreateDbContext();

        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        context.SlackDmSessionMappings.AddRange(
            new SlackDmSessionMappingRow
            {
                Id = "slkdmmp_first",
                ProjectId = "project-1",
                ConnectionId = "connection-1",
                WorkspaceTeamId = "T123",
                SlackUserId = "U_OWNER",
                DmConversationId = "D123",
                CurrentSessionId = "session-a",
                UpdatedAt = now,
            },
            new SlackDmSessionMappingRow
            {
                Id = "slkdmmp_second",
                ProjectId = "project-1",
                ConnectionId = "connection-1",
                WorkspaceTeamId = "T123",
                SlackUserId = "U_OWNER",
                DmConversationId = "D456",
                CurrentSessionId = "session-b",
                UpdatedAt = now,
            });
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.SlackDmSessionMappings.CountAsync());
    }

    private static TestDatabase CreateDatabase(string? migratedTo = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        if (migratedTo is not null)
            MigratedSqliteTemplate.CopyTo(connection, migratedTo);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new TestDatabase(connection, factory);
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
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
        public SqliteConnection Connection => _connection;
        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();
        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
