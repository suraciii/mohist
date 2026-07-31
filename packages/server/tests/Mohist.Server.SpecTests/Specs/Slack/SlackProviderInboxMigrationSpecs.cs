using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public class SlackProviderInboxMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesSlackProviderInboxRowsTableWithExpectedColumns()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        var columnTypes = await ReadColumnTypesAsync(context, "SlackProviderInboxRows");
        Assert.Equal("TEXT", columnTypes["Id"]);
        Assert.Equal("TEXT", columnTypes["ProjectId"]);
        Assert.Equal("TEXT", columnTypes["ConnectionId"]);
        Assert.Equal("TEXT", columnTypes["SlackMessageIdentity"]);
        Assert.Equal("TEXT", columnTypes["WorkspaceTeamId"]);
        Assert.Equal("TEXT", columnTypes["DmConversationId"]);
        Assert.Equal("TEXT", columnTypes["SlackUserId"]);
        Assert.Equal("TEXT", columnTypes["AcceptedAt"]);
        Assert.Equal("TEXT", columnTypes["DispatchedAt"]);
        Assert.Equal("TEXT", columnTypes["CreatedAt"]);
    }

    [Fact]
    public async Task Up_CreatesUniqueIndexOnConnectionIdAndSlackMessageIdentity()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "SlackProviderInboxRows");
        Assert.Contains("UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity", indexes.Keys);
        Assert.Equal(new[] { "ConnectionId", "SlackMessageIdentity" }, indexes["UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity"]);

        var unique = await ReadUniqueFlagAsync(context, "SlackProviderInboxRows", "UX_SlackProviderInboxRows_ConnectionId_SlackMessageIdentity");
        Assert.True(unique);
    }

    [Fact]
    public async Task Up_CreatesPendingLookupIndex()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "SlackProviderInboxRows");
        Assert.Contains("IX_SlackProviderInboxRows_ProjectId_ConnectionId_DispatchedAt", indexes.Keys);
        Assert.Equal(new[] { "ProjectId", "ConnectionId", "DispatchedAt" }, indexes["IX_SlackProviderInboxRows_ProjectId_ConnectionId_DispatchedAt"]);
    }

    [Fact]
    public async Task Up_UniqueConstraintRejectsDuplicateMessageIdentity()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        await InsertAsync(context, "proj_a", "conn_1", "team-1/D1/1234.5678", "2026-07-29T00:00:00.0000000+00:00");
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(context, "proj_a", "conn_1", "team-1/D1/1234.5678", "2026-07-29T00:00:01.0000000+00:00"));
    }

    [Fact]
    public async Task Up_DifferentConnectionOrIdentityPermitsSeparateRows()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        await InsertAsync(context, "proj_a", "conn_1", "team-1/D1/1234.5678", "2026-07-29T00:00:00.0000000+00:00");
        await InsertAsync(context, "proj_a", "conn_1", "team-1/D1/9999.9999", "2026-07-29T00:00:01.0000000+00:00");
        await InsertAsync(context, "proj_a", "conn_2", "team-1/D1/1234.5678", "2026-07-29T00:00:02.0000000+00:00");

        Assert.Equal(3, await context.SlackProviderInboxRows.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesSlackProviderInboxMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(context, "SlackProviderInboxRows"));
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260729110000_AddSlackProviderInboxOutbox");
        var columns = await ReadColumnTypesAsync(context, "SlackProviderInboxRows");
        Assert.Equal("TEXT", columns["ThreadTs"]);
    }

    [Fact]
    public async Task Down_DropsBothTables()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using (var apply = database.CreateDbContext())
        {
            var migrator = apply.GetService<IMigrator>();
            await migrator.MigrateAsync("20260729100000_AddAgentConnections");
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "SlackProviderInboxRows"));
        Assert.False(await TableExistsAsync(verify, "SlackOutboxRows"));
    }

    [Fact]
    public async Task DbContext_ExposesInboxDbSet()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.SlackProviderInboxRows);
        var entityType = context.Model.FindEntityType(typeof(SlackProviderInboxRow));
        Assert.NotNull(entityType);
        Assert.Equal("SlackProviderInboxRows", entityType.GetTableName());
    }

    private static Task InsertAsync(MohistDbContext context, string projectId, string connectionId, string identity, string acceptedAt)
    {
        return context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SlackProviderInboxRows" (
                "Id", "ProjectId", "ConnectionId", "SlackMessageIdentity",
                "WorkspaceTeamId", "DmConversationId", "SlackUserId",
                "AcceptedAt", "DispatchedAt", "CreatedAt"
            ) VALUES (
                $id, $projectId, $connectionId, $identity,
                'team-1', 'D1', 'U1',
                $acceptedAt, NULL, $acceptedAt
            )
            """,
            new SqliteParameter("$id", $"slkinb_{Guid.NewGuid():N}"),
            new SqliteParameter("$projectId", projectId),
            new SqliteParameter("$connectionId", connectionId),
            new SqliteParameter("$identity", identity),
            new SqliteParameter("$acceptedAt", acceptedAt));
    }

    private static async Task<IDictionary<string, string>> ReadColumnTypesAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\", \"type\" FROM pragma_table_info('{tableName}')";

        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static async Task<IDictionary<string, string[]>> ReadIndexesAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\", \"seq\" FROM pragma_index_list('{tableName}') " +
            "WHERE \"origin\" != 'pk' AND \"name\" NOT LIKE 'sqlite_%' " +
            "ORDER BY \"seq\"";

        var ordered = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ordered.Add(reader.GetString(0));
            }
        }

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var indexName in ordered)
        {
            await using var inner = connection.CreateCommand();
            inner.CommandText = $"SELECT \"name\" FROM pragma_index_info('{indexName}') ORDER BY \"seqno\"";
            var columns = new List<string>();
            await using var colReader = await inner.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                columns.Add(colReader.GetString(0));
            }
            result[indexName] = columns.ToArray();
        }
        return result;
    }

    private static async Task<bool> ReadUniqueFlagAsync(MohistDbContext context, string tableName, string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"unique\" FROM pragma_index_list($name) WHERE \"name\" = $idx";
        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "$name";
        tableParam.Value = tableName;
        command.Parameters.Add(tableParam);
        var idxParam = command.CreateParameter();
        idxParam.ParameterName = "$idx";
        idxParam.Value = indexName;
        command.Parameters.Add(idxParam);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) == 1L;
    }

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

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
