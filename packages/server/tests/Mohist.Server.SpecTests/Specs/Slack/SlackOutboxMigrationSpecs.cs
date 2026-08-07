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

public class SlackOutboxMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesSlackOutboxRowsTableWithExpectedColumns()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        var columnTypes = await ReadColumnTypesAsync(context, "SlackOutboxRows");
        Assert.Equal("TEXT", columnTypes["Id"]);
        Assert.Equal("TEXT", columnTypes["ProjectId"]);
        Assert.Equal("TEXT", columnTypes["ConnectionId"]);
        Assert.Equal("TEXT", columnTypes["WorkspaceTeamId"]);
        Assert.Equal("TEXT", columnTypes["DmConversationId"]);
        Assert.Equal("TEXT", columnTypes["Kind"]);
        Assert.Equal("TEXT", columnTypes["State"]);
        Assert.Equal("TEXT", columnTypes["DispatchRef"]);
        Assert.Equal("TEXT", columnTypes["PayloadJson"]);
        Assert.Equal("INTEGER", columnTypes["AttemptCount"]);
        Assert.Equal("TEXT", columnTypes["NextAttemptAt"]);
        Assert.Equal("TEXT", columnTypes["ClaimedAt"]);
        Assert.Equal("TEXT", columnTypes["ClaimedByAdapterId"]);
        Assert.Equal("TEXT", columnTypes["DeliveredAt"]);
        Assert.Equal("TEXT", columnTypes["DeliveryUncertainAt"]);
        Assert.Equal("TEXT", columnTypes["DeadLetteredAt"]);
        Assert.Equal("TEXT", columnTypes["LastError"]);
        Assert.Equal("TEXT", columnTypes["CreatedAt"]);
        Assert.Equal("TEXT", columnTypes["UpdatedAt"]);
    }

    [Fact]
    public async Task Thread_delivery_migration_uses_generic_conversation_and_thread_columns()
    {
        await using var database = CreateDatabase("20260731120000_AddSlackThreadDelivery");
        await using var context = database.CreateDbContext();

        var outboxColumns = await ReadColumnTypesAsync(context, "SlackOutboxRows");
        Assert.Contains("ConversationId", outboxColumns.Keys);
        Assert.Contains("ThreadTs", outboxColumns.Keys);
        Assert.DoesNotContain("DmConversationId", outboxColumns.Keys);

        var inboxColumns = await ReadColumnTypesAsync(context, "SlackProviderInboxRows");
        Assert.Contains("ConversationId", inboxColumns.Keys);
        Assert.DoesNotContain("DmConversationId", inboxColumns.Keys);

        var sessionColumns = await ReadColumnTypesAsync(context, "AgentSessions");
        Assert.Contains("LabelSlackThreadTs", sessionColumns.Keys);
    }

    [Fact]
    public async Task Up_CreatesFiveIndexes()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "SlackOutboxRows");
        Assert.Contains("IX_SlackOutboxRows_ProjectId_ConnectionId_State", indexes.Keys);
        Assert.Contains("IX_SlackOutboxRows_ConnectionId_State_NextAttemptAt", indexes.Keys);
        Assert.Contains("IX_SlackOutboxRows_ConnectionId_State_ClaimedAt", indexes.Keys);
        Assert.Contains("IX_SlackOutboxRows_ConnectionId_State_DeliveryUncertainAt", indexes.Keys);
        Assert.Contains("IX_SlackOutboxRows_ConnectionId_DispatchRef_Kind_State", indexes.Keys);
    }

    [Fact]
    public async Task Up_KindCheckConstraint_AcceptsDefinedKinds()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        foreach (var kind in new[] { "replaceable_progress", "terminal_result", "explicit_failure", "user_action" })
        {
            await InsertAsync(context, kind, "pending");
        }

        Assert.Equal(4, await context.SlackOutboxRows.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Up_KindCheckConstraint_RejectsUnsupportedKind()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(context, "weird_kind", "pending"));
    }

    [Fact]
    public async Task Up_StateCheckConstraint_AcceptsDefinedStates()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        foreach (var state in new[] { "pending", "claimed", "delivered", "delivery_uncertain", "dead_lettered" })
        {
            await InsertAsync(context, "terminal_result", state);
        }

        Assert.Equal(5, await context.SlackOutboxRows.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Up_StateCheckConstraint_RejectsUnsupportedState()
    {
        await using var database = CreateDatabase("20260729110000_AddSlackProviderInboxOutbox");
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(context, "terminal_result", "abandoned"));
    }

    [Fact]
    public async Task DbContext_ExposesOutboxDbSet()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.SlackOutboxRows);
        var entityType = context.Model.FindEntityType(typeof(SlackOutboxRow));
        Assert.NotNull(entityType);
        Assert.Equal("SlackOutboxRows", entityType.GetTableName());
    }

    private static Task InsertAsync(MohistDbContext context, string kind, string state)
    {
        var id = $"slkout_{Guid.NewGuid():N}";
        var sql = "INSERT INTO \"SlackOutboxRows\" (" +
            "\"Id\", \"ProjectId\", \"ConnectionId\", \"WorkspaceTeamId\", \"DmConversationId\", " +
            "\"Kind\", \"State\", \"DispatchRef\", \"PayloadJson\", \"AttemptCount\", " +
            "\"NextAttemptAt\", \"ClaimedAt\", \"ClaimedByAdapterId\", \"DeliveredAt\", " +
            "\"DeliveryUncertainAt\", \"DeadLetteredAt\", \"LastError\", " +
            "\"CreatedAt\", \"UpdatedAt\"" +
            ") VALUES (" +
            $"'{id}', 'proj_a', 'conn_1', 'team-1', 'D1', " +
            $"'{kind}', '{state}', NULL, json(char(123) || char(125)), 0, " +
            "NULL, NULL, NULL, NULL, NULL, NULL, NULL, " +
            "'2026-07-29T00:00:00.0000000+00:00', '2026-07-29T00:00:00.0000000+00:00'" +
            ")";
        return context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<IDictionary<string, string>> ReadColumnTypesAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\", \"type\" FROM pragma_table_xinfo('{tableName}')";

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
