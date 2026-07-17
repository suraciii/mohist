using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

public class InboxSubscriptionsMigrationSpecs
{
    [Fact]
    public async Task Up_CreatesInboxSubscriptionsTableWithExpectedColumns()
    {
        await using var database = CreateDatabase("20260629003200_AddInboxSubscriptionsTable");
        await using var context = database.CreateDbContext();

        var columnTypes = await ReadColumnTypesAsync(context, "InboxSubscriptions");
        Assert.Equal("TEXT", columnTypes["ProjectId"]);
        Assert.Equal("INTEGER", columnTypes["WorkflowFailedEnabled"]);
        Assert.Equal("INTEGER", columnTypes["ApprovalRequestedEnabled"]);
        Assert.Equal("INTEGER", columnTypes["IssueStartedEnabled"]);
        Assert.Equal("INTEGER", columnTypes["IssueCompletedEnabled"]);
        Assert.Equal("TEXT", columnTypes["UpdatedAt"]);
    }

    [Fact]
    public async Task Up_HasPrimaryKeyOnProjectId()
    {
        await using var database = CreateDatabase("20260629003200_AddInboxSubscriptionsTable");
        await using var context = database.CreateDbContext();

        var pks = await ReadPrimaryKeyColumnsAsync(context, "InboxSubscriptions");
        Assert.Equal(new[] { "ProjectId" }, pks);
    }

    [Fact]
    public async Task Up_HasForeignKeyToProjectsOnProjectId()
    {
        await using var database = CreateDatabase("20260629003200_AddInboxSubscriptionsTable");
        await using var context = database.CreateDbContext();

        var foreignKeys = await ReadForeignKeysAsync(context, "InboxSubscriptions");
        var fk = Assert.Single(foreignKeys);
        Assert.Equal("Projects", fk.Table);
        Assert.Equal("ProjectId", fk.From);
        Assert.Equal("Id", fk.To);
        Assert.Equal("CASCADE", fk.OnDelete);
    }

    [Fact]
    public async Task Up_StoresAndReadsSubscriptionRow()
    {
        await using var database = CreateDatabase("20260629003200_AddInboxSubscriptionsTable");
        await using var context = database.CreateDbContext();

        context.Projects.Add(new ProjectRow
        {
            Id = "proj_a",
            Name = "project-a",
            RepositoriesJson = """[{"name":"test-repo","gitUrl":"git@example.com:test-repo.git","baseBranch":"main","isDefault":true}]""",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });

        context.InboxSubscriptions.Add(new InboxSubscriptionRow
        {
            ProjectId = "proj_a",
            WorkflowFailedEnabled = false,
            ApprovalRequestedEnabled = true,
            IssueStartedEnabled = false,
            IssueCompletedEnabled = true,
            UpdatedAt = TestTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var row = Assert.Single(context.InboxSubscriptions);
        Assert.Equal("proj_a", row.ProjectId);
        Assert.False(row.WorkflowFailedEnabled);
        Assert.True(row.ApprovalRequestedEnabled);
        Assert.False(row.IssueStartedEnabled);
        Assert.True(row.IssueCompletedEnabled);
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(context, "InboxSubscriptions"));
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260629003200_AddInboxSubscriptionsTable");
    }

    [Fact]
    public async Task Down_DropsInboxSubscriptionsTable()
    {
        await using var database = CreateDatabase("20260629003200_AddInboxSubscriptionsTable");
        await using (var tearDown = database.CreateDbContext())
        {
            var migrator = tearDown.GetService<IMigrator>();
            await migrator.MigrateAsync("20260629003151_AddInboxItemsTable");
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "InboxSubscriptions"));
    }

    [Fact]
    public async Task DbContext_ExposesInboxSubscriptionsDbSet()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.InboxSubscriptions);
        var entityType = context.Model.FindEntityType(typeof(InboxSubscriptionRow));
        Assert.NotNull(entityType);
        Assert.Equal("InboxSubscriptions", entityType.GetTableName());
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

    private static async Task<string[]> ReadPrimaryKeyColumnsAsync(
        MohistDbContext context,
        string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\" FROM pragma_table_info('{tableName}') WHERE \"pk\" = 1 ORDER BY \"cid\"";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }
        return columns.ToArray();
    }

    private static async Task<ForeignKeyInfo[]> ReadForeignKeysAsync(
        MohistDbContext context,
        string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"table\", \"from\", \"to\", \"on_delete\" FROM pragma_foreign_key_list('{tableName}')";

        var result = new List<ForeignKeyInfo>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ForeignKeyInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return result.ToArray();
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

    private sealed record ForeignKeyInfo(string Table, string From, string To, string OnDelete);
}
