using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Secrets;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Security;

public class ConnectionSecretsMigrationSpecs
{
    private static readonly string[] ManagedSlackAgentAppIndexes =
    [
        "IX_ManagedSlackAgentApps_AgentConnectionId",
        "UX_ManagedSlackAgentApps_AgentConnectionId",
        "UX_ManagedSlackAgentApps_WorkspaceTeamId_AppId",
        "IX_ManagedSlackAgentApps_EnrollmentId_UpdatedAt",
    ];

    private static readonly string[] ManagedSlackChildAppIndexes =
    [
        "UX_ManagedSlackChildApps_AgentConnectionId",
        "UX_ManagedSlackChildApps_WorkspaceTeamId_AppId",
        "IX_ManagedSlackChildApps_EnrollmentId_UpdatedAt",
    ];

    [Fact]
    public async Task Up_CreatesConnectionSecretsTableWithExpectedColumns()
    {
        await using var database = CreateDatabase("20260729000000_AddConnectionSecrets");
        await using var context = database.CreateDbContext();

        var columnTypes = await ReadColumnTypesAsync(context, "ConnectionSecrets");
        Assert.Equal("TEXT", columnTypes["ProjectId"]);
        Assert.Equal("TEXT", columnTypes["ConnectionId"]);
        Assert.Equal("TEXT", columnTypes["Kind"]);
        Assert.Equal("BLOB", columnTypes["Blob"]);
        Assert.Equal("TEXT", columnTypes["UpdatedAt"]);
    }

    [Fact]
    public async Task Up_ProjectIdConnectionIdIndex_IsCreated()
    {
        await using var database = CreateDatabase("20260729000000_AddConnectionSecrets");
        await using var context = database.CreateDbContext();

        var indexes = await ReadIndexesAsync(context, "ConnectionSecrets");

        Assert.Contains("IX_ConnectionSecrets_ProjectId_ConnectionId", indexes.Keys);
    }

    [Fact]
    public async Task Up_KindCheckConstraint_RejectsUnsupportedKind()
    {
        await using var database = CreateDatabase("20260729000000_AddConnectionSecrets");
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "ConnectionSecrets" (
                "ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            VALUES (
                'proj_a', 'conn_1', 'unsupported', X'00', '2026-07-29T00:00:00.0000000+00:00')
            """));
    }

    [Fact]
    public async Task Up_KindCheckConstraint_AcceptsBothSupportedKinds()
    {
        await using var database = CreateDatabase("20260729000000_AddConnectionSecrets");
        await using var context = database.CreateDbContext();

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "ConnectionSecrets" (
                "ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            VALUES ('proj_a', 'conn_1', 'appToken', X'00', '2026-07-29T00:00:00.0000000+00:00');
            """);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "ConnectionSecrets" (
                "ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            VALUES ('proj_a', 'conn_1', 'botToken', X'00', '2026-07-29T00:00:00.0000000+00:00');
            """);

        Assert.Equal(2, await CountRowsAsync(context, "ConnectionSecrets"));
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesConnectionSecretsMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(context, "StoredSecrets"));
        Assert.False(await TableExistsAsync(context, "ConnectionSecrets"));
        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260729000000_AddConnectionSecrets");
        Assert.Contains(applied, m => m == "20260805120000_RenameManagedSlackAgentAppAndGeneralizeSecrets");
    }

    [Fact]
    public async Task Down_DropsConnectionSecretsTable()
    {
        await using var database = CreateDatabase("20260729000000_AddConnectionSecrets");
        await using (var apply = database.CreateDbContext())
        {
            var migrator = apply.GetService<IMigrator>();
            await migrator.MigrateAsync("20260728000000_TypedWorkflowRunLineage");
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "ConnectionSecrets"));
    }

    [Fact]
    public async Task DbContext_ExposesTypedStoredSecretsDbSet()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.NotNull(context.StoredSecrets);
        var entityType = context.Model.FindEntityType(typeof(StoredSecretRow));
        Assert.NotNull(entityType);
        Assert.Equal("StoredSecrets", entityType.GetTableName());
    }

    [Fact]
    public async Task StoredSecrets_RestrictsKindsToTheirTypedOwners()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync();

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "StoredSecrets" (
                "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            VALUES (
                'slack_workspace_enrollment', '', 'enrollment_1', 'configurationAccessToken', X'01',
                '2026-08-05T00:00:00.0000000+00:00');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "StoredSecrets" (
                "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            VALUES (
                'slack_workspace_enrollment', '', 'enrollment_1', 'webhookSecret', X'01',
                '2026-08-05T00:00:00.0000000+00:00');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "StoredSecrets" (
                "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            VALUES (
                'managed_slack_agent_app', '', 'agent_app_1', 'configurationRefreshToken', X'01',
                '2026-08-05T00:00:00.0000000+00:00');
            """));

        Assert.Equal(1, await CountRowsAsync(context, "StoredSecrets"));
    }

    [Fact]
    public async Task Upgrade_CopiesLegacySecretsToTypedOwnersAndRenamesAgentAppTable()
    {
        await using var database = CreateDatabase("20260804100000_AddSlackManagerIdentityAndOutboxOwner");
        await using (var before = database.CreateDbContext())
        {
            await before.Database.ExecuteSqlRawAsync("""
                INSERT INTO "ConnectionSecrets" ("ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
                VALUES ('proj_a', 'conn_a', 'appToken', X'01', '2026-08-05T00:00:00.0000000+00:00');
                """);
            var migrator = before.GetService<IMigrator>();
            await migrator.MigrateAsync();
        }

        await using var after = database.CreateDbContext();
        var secret = Assert.Single(await after.StoredSecrets.AsNoTracking().ToListAsync());
        Assert.Equal(SecretOwnerKinds.AgentConnection, secret.OwnerKind);
        Assert.Equal("proj_a", secret.OwnerScope);
        Assert.Equal("conn_a", secret.OwnerId);
        Assert.Equal("appToken", secret.Kind);
        Assert.True(await TableExistsAsync(after, "ManagedSlackAgentApps"));
        Assert.False(await TableExistsAsync(after, "ManagedSlackChildApps"));
        Assert.False(await TableExistsAsync(after, "ConnectionSecrets"));
        AssertIndexNames(
            ManagedSlackAgentAppIndexes,
            await ReadIndexesAsync(after, "ManagedSlackAgentApps"));
    }

    [Fact]
    public async Task Down_RestoresLegacySecretRows()
    {
        await using var database = CreateDatabase("20260804100000_AddSlackManagerIdentityAndOutboxOwner");
        await using (var before = database.CreateDbContext())
        {
            await before.Database.ExecuteSqlRawAsync("""
                INSERT INTO "ConnectionSecrets" ("ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
                VALUES ('proj_down', 'conn_down', 'botToken', X'02', '2026-08-05T00:00:00.0000000+00:00');
                """);
            var migrator = before.GetService<IMigrator>();
            await migrator.MigrateAsync();
            await migrator.MigrateAsync("20260804100000_AddSlackManagerIdentityAndOutboxOwner");
        }

        await using var after = database.CreateDbContext();
        Assert.True(await TableExistsAsync(after, "ConnectionSecrets"));
        Assert.False(await TableExistsAsync(after, "StoredSecrets"));
        Assert.Equal(1, await CountRowsAsync(after, "ConnectionSecrets"));
        AssertIndexNames(
            ManagedSlackChildAppIndexes,
            await ReadIndexesAsync(after, "ManagedSlackChildApps"));
    }

    [Fact]
    public async Task Down_RejectsNonrepresentableEnrollmentAndAgentAppSecrets()
    {
        await using var database = CreateDatabase("20260804100000_AddSlackManagerIdentityAndOutboxOwner");
        await using (var apply = database.CreateDbContext())
        {
            var migrator = apply.GetService<IMigrator>();
            await migrator.MigrateAsync();
            await apply.Database.ExecuteSqlRawAsync("""
                INSERT INTO "StoredSecrets" (
                    "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
                VALUES
                    ('slack_workspace_enrollment', '', 'enrollment_down', 'configurationAccessToken', X'01', '2026-08-05T00:00:00.0000000+00:00'),
                    ('managed_slack_agent_app', '', 'agent_app_down', 'clientSecret', X'02', '2026-08-05T00:00:00.0000000+00:00');
                """);

            await Assert.ThrowsAsync<SqliteException>(
                () => migrator.MigrateAsync("20260804100000_AddSlackManagerIdentityAndOutboxOwner"));
        }

        await using var after = database.CreateDbContext();
        Assert.True(await TableExistsAsync(after, "StoredSecrets"));
        Assert.False(await TableExistsAsync(after, "ConnectionSecrets"));
        Assert.Equal(2, await CountRowsAsync(after, "StoredSecrets"));
        Assert.Contains(
            "20260805120000_RenameManagedSlackAgentAppAndGeneralizeSecrets",
            await after.Database.GetAppliedMigrationsAsync());
    }

    private static void AssertIndexNames(
        string[] expected,
        IDictionary<string, string[]> actual) =>
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            actual.Keys.OrderBy(name => name, StringComparer.Ordinal));

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
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static async Task<int> CountRowsAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
