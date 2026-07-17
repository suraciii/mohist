using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

public class InboxItemsMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesInboxItemsTableWithExpectedColumns()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using var context = database.CreateDbContext();

        var columnTypes = await ReadColumnTypesAsync(context, "InboxItems");
        Assert.Equal("TEXT", columnTypes["Id"]);
        Assert.Equal("TEXT", columnTypes["ProjectId"]);
        Assert.Equal("TEXT", columnTypes["IssueId"]);
        Assert.Equal("INTEGER", columnTypes["IssueNumber"]);
        Assert.Equal("TEXT", columnTypes["IssueTitle"]);
        Assert.Equal("TEXT", columnTypes["NotificationKind"]);
        Assert.Equal("TEXT", columnTypes["SourceEventSource"]);
        Assert.Equal("TEXT", columnTypes["SourceEventId"]);
        Assert.Equal("TEXT", columnTypes["CreatedAt"]);
        Assert.Equal("TEXT", columnTypes["ReadAt"]);
        Assert.Equal("TEXT", columnTypes["ArchivedAt"]);
    }

    [Fact]
    public async Task Up_CreatesThreeNamedIndexes()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "InboxItems");

        Assert.Contains("IX_InboxItems_ProjectId_CreatedAt", indexes.Keys);
        Assert.Contains("UQ_InboxItems_SourceEvent", indexes.Keys);
        Assert.Contains("IX_InboxItems_ProjectId_Id", indexes.Keys);
    }

    [Fact]
    public async Task Up_ProjectIdCreatedAtIndex_IsDescending()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using var context = database.CreateDbContext();

        var desc = await ReadIndexColumnsAsync(context, "InboxItems", "IX_InboxItems_ProjectId_CreatedAt");
        Assert.Equal(new[] { "ProjectId", "CreatedAt" }, desc.Columns);
        Assert.False(desc.Descending[0]);
        Assert.True(desc.Descending[1]);
    }

    [Fact]
    public async Task Up_SourceEventIndex_IsUniqueOnSourceAndId()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using var context = database.CreateDbContext();

        var columns = await ReadIndexColumnsAsync(context, "InboxItems", "UQ_InboxItems_SourceEvent");
        Assert.Equal(new[] { "SourceEventSource", "SourceEventId" }, columns.Columns);

        var unique = await ReadUniqueFlagAsync(context, "InboxItems", "UQ_InboxItems_SourceEvent");
        Assert.True(unique);
    }

    [Fact]
    public async Task Up_NotificationKindCheckConstraint_RejectsUnsupportedKind()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "InboxItems" (
                "Id", "ProjectId", "IssueId", "IssueNumber", "IssueTitle", "NotificationKind",
                "SourceEventSource", "SourceEventId", "CreatedAt")
            VALUES (
                'inb_invalid', 'proj_a', 'issue_1', 1, 'Issue 1', 'unsupported',
                '/mohist/issues/issue_1', 'evt_invalid', '2026-06-30T00:00:00.0000000+00:00')
            """));
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesInboxItemsMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(context, "InboxItems"));
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260629003151_AddInboxItemsTable");
    }

    [Fact]
    public async Task Down_DropsInboxItemsTable()
    {
        await using var database = CreateDatabase("20260629003151_AddInboxItemsTable");
        await using (var apply = database.CreateDbContext())
        {
            var migrator = apply.GetService<IMigrator>();
            await migrator.MigrateAsync("20260628022822_DropEpicIssueMembershipUniqueIndex");
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "InboxItems"));
    }

    [Fact]
    public async Task DbContext_ExposesInboxItemsDbSet()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.InboxItems);
        var entityType = context.Model.FindEntityType(typeof(InboxItemRow));
        Assert.NotNull(entityType);
        Assert.Equal("InboxItems", entityType.GetTableName());
    }

    private static async Task<IDictionary<string, string>> ReadColumnTypesAsync(
        MohistDbContext context,
        string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\", \"type\" FROM pragma_table_info('{tableName}')";

        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            result[name] = type;
        }
        return result;
    }

    private static async Task<IDictionary<string, string[]>> ReadIndexesAsync(
        MohistDbContext context,
        string tableName)
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

    private sealed record IndexColumns(string[] Columns, bool[] Descending);

    private static async Task<IndexColumns> ReadIndexColumnsAsync(
        MohistDbContext context,
        string tableName,
        string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        // pragma_index_xinfo returns one row per column in the index
        // (key + auxiliary). Column 0 is the position within the index, 2
        // is the column name (NULL for the implicit rowid), 3 is "1 if the
        // index-column is sorted in reverse (DESC) order" and 5 marks key
        // vs auxiliary columns. Filter to key columns so we only see the
        // ones the user declared.
        command.CommandText = "SELECT \"name\", \"desc\" FROM pragma_index_xinfo($indexName) WHERE \"key\" = 1 ORDER BY \"seqno\"";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$indexName";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        var columns = new List<string>();
        var descending = new List<bool>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
            descending.Add(reader.GetInt32(1) != 0);
        }
        return new IndexColumns(columns.ToArray(), descending.ToArray());
    }

    private static async Task<bool> ReadUniqueFlagAsync(
        MohistDbContext context,
        string tableName,
        string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"unique\" FROM pragma_index_list($tableName) WHERE \"name\" = $indexName";
        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "$tableName";
        tableParam.Value = tableName;
        command.Parameters.Add(tableParam);
        var indexParam = command.CreateParameter();
        indexParam.ParameterName = "$indexName";
        indexParam.Value = indexName;
        command.Parameters.Add(indexParam);
        var result = await command.ExecuteScalarAsync();
        return result is long l && l != 0;
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
        {
            MigratedSqliteTemplate.CopyTo(connection, migratedTo);
        }
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
