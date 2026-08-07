using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public class SlackThreadSessionMappingMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesSlackThreadSessionMappingsTable()
    {
        await using var database = CreateDatabase("20260731130000_AddSlackThreadSessionMappings");
        await using var context = database.CreateDbContext();

        Assert.False(await context.SlackThreadSessionMappings.AnyAsync());
        Assert.Empty(await context.SlackThreadSessionMappings.ToListAsync());
    }

    [Fact]
    public async Task Up_AddsExpectedColumns()
    {
        await using var database = CreateDatabase("20260731130000_AddSlackThreadSessionMappings");
        await using var context = database.CreateDbContext();

        var columns = await ReadColumnNamesAsync(context, "SlackThreadSessionMappings");
        Assert.Contains("Id", columns);
        Assert.Contains("ProjectId", columns);
        Assert.Contains("ConnectionId", columns);
        Assert.Contains("WorkspaceTeamId", columns);
        Assert.Contains("ConversationId", columns);
        Assert.Contains("ThreadTs", columns);
        Assert.Contains("SlackUserId", columns);
        Assert.Contains("SessionId", columns);
        Assert.Contains("RootMessageTs", columns);
        Assert.Contains("CreatedAt", columns);
        Assert.Contains("UpdatedAt", columns);
    }

    [Fact]
    public async Task Up_AddsUniqueIndexScopedToConnectionWorkspaceConversationThread()
    {
        await using var database = CreateDatabase("20260731130000_AddSlackThreadSessionMappings");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "SlackThreadSessionMappings");
        Assert.Contains(
            "UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs",
            indexes.Keys);
        Assert.Equal(
            new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
            indexes["UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs"]);

        var unique = await ReadUniqueFlagAsync(
            context,
            "SlackThreadSessionMappings",
            "UX_SlackThreadSessionMappings_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs");
        Assert.True(unique);
    }

    [Fact]
    public async Task Up_AddsWorkspaceLookupIndex()
    {
        await using var database = CreateDatabase("20260731150000_AddSlackThreadWorkspaceLookupIndex");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "SlackThreadSessionMappings");
        Assert.Equal(
            new[] { "WorkspaceTeamId", "ConversationId", "ThreadTs" },
            indexes["IX_SlackThreadSessionMappings_WorkspaceTeamId_ConversationId_ThreadTs"]);
    }

    [Fact]
    public async Task Up_CreatesLaunchReservationTableAndUniqueIndex()
    {
        await using var database = CreateDatabase("20260731170000_AddSlackThreadLaunchReservations");
        await using var context = database.CreateDbContext();

        var columns = await ReadColumnNamesAsync(context, "SlackThreadLaunchReservations");
        Assert.Contains("LaunchMessageTs", columns);
        Assert.Contains("SessionId", columns);

        var indexes = await ReadIndexesAsync(context, "SlackThreadLaunchReservations");
        Assert.Equal(
            new[] { "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs" },
            indexes["UX_SlackThreadLaunchReservations_ConnectionId_WorkspaceTeamId_ConversationId_ThreadTs"]);
    }

    [Fact]
    public async Task Up_UniqueConstraintRejectsSameBindingForSameConnection()
    {
        await using var database = CreateDatabase("20260731130000_AddSlackThreadSessionMappings");
        await using var context = database.CreateDbContext();

        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        context.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
        {
            Id = "slkthrdsmp_first",
            ProjectId = "project-1",
            ConnectionId = "connection-1",
            WorkspaceTeamId = "T123",
            ConversationId = "C-shared",
            ThreadTs = "1710.0001",
            SlackUserId = "U_OWNER",
            SessionId = "session-a",
            RootMessageTs = "1710.0001",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();

        context.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
        {
            Id = "slkthrdsmp_second",
            ProjectId = "project-1",
            ConnectionId = "connection-1",
            WorkspaceTeamId = "T123",
            ConversationId = "C-shared",
            ThreadTs = "1710.0001",
            SlackUserId = "U_OWNER",
            SessionId = "session-b",
            RootMessageTs = "1710.0001",
            CreatedAt = now,
            UpdatedAt = now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Fact]
    public async Task Up_AllowsEqualThreadTsAcrossChannelsOrWorkspaces()
    {
        await using var database = CreateDatabase("20260731130000_AddSlackThreadSessionMappings");
        await using var context = database.CreateDbContext();

        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        context.SlackThreadSessionMappings.AddRange(
            new SlackThreadSessionMappingRow
            {
                Id = "slkthrdsmp_first",
                ProjectId = "project-1",
                ConnectionId = "connection-1",
                WorkspaceTeamId = "T123",
                ConversationId = "C-one",
                ThreadTs = "1710.0001",
                SlackUserId = "U_OWNER",
                SessionId = "session-a",
                RootMessageTs = "1710.0001",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SlackThreadSessionMappingRow
            {
                Id = "slkthrdsmp_second",
                ProjectId = "project-1",
                ConnectionId = "connection-1",
                WorkspaceTeamId = "T123",
                ConversationId = "C-two",
                ThreadTs = "1710.0001",
                SlackUserId = "U_OWNER",
                SessionId = "session-b",
                RootMessageTs = "1710.0001",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SlackThreadSessionMappingRow
            {
                Id = "slkthrdsmp_third",
                ProjectId = "project-1",
                ConnectionId = "connection-1",
                WorkspaceTeamId = "T-OTHER",
                ConversationId = "C-one",
                ThreadTs = "1710.0001",
                SlackUserId = "U_OWNER",
                SessionId = "session-c",
                RootMessageTs = "1710.0001",
                CreatedAt = now,
                UpdatedAt = now,
            });

        await context.SaveChangesAsync();
        Assert.Equal(3, await context.SlackThreadSessionMappings.CountAsync());
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

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\" FROM pragma_table_info('{tableName}')";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<IReadOnlyDictionary<string, string[]>> ReadIndexesAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT ""name"", ""sql"" FROM sqlite_master
            WHERE ""type"" = 'index' AND ""tbl_name"" = '{tableName}' AND ""sql"" IS NOT NULL";
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var sql = reader.GetString(1);
            var columns = ParseIndexColumns(sql);
            if (columns.Length > 0)
                result[name] = columns;
        }
        return result;
    }

    private static async Task<bool> ReadUniqueFlagAsync(MohistDbContext context, string tableName, string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT ""sql"" FROM sqlite_master
            WHERE ""type"" = 'index' AND ""name"" = '{indexName}'";
        var result = false;
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var sql = reader.GetString(0) ?? string.Empty;
            result = sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string[] ParseIndexColumns(string sql)
    {
        var open = sql.IndexOf('(');
        var close = sql.IndexOf(')');
        if (open < 0 || close < 0 || close <= open) return Array.Empty<string>();
        var raw = sql.Substring(open + 1, close - open - 1);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Trim('"', ' ', '[', ']'))
            .Where(c => c.Length > 0)
            .ToArray();
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
