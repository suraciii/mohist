using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class BackfillIssueLegacyArrayLabelsMigrationSpecs
{
    private const string MigrationId = "20260703140000_BackfillIssueLegacyArrayLabels";

    [Fact]
    public async Task Up_NonEmptyArrayLabels_RewrittenToEmptyObject()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_a",
                """{"id":"issue_a","number":1,"labels":["a","b"],"status":"backlog"}""");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var labels = await ReadLabelsTokenAsync(verify, "issue_a");
        Assert.Equal("object", labels);
        Assert.Equal("{}", await ReadLabelsRawAsync(verify, "issue_a"));
    }

    [Fact]
    public async Task Up_EmptyArrayLabels_RewrittenToEmptyObject()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_b",
                """{"id":"issue_b","number":2,"labels":[],"status":"backlog"}""");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("object", await ReadLabelsTokenAsync(verify, "issue_b"));
        Assert.Equal("{}", await ReadLabelsRawAsync(verify, "issue_b"));
    }

    [Fact]
    public async Task Up_DictLabels_LeftUnchanged()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            // Post-#149 shape: a real key-value map. Must survive the backfill
            // byte-for-byte — the rewrite is scoped to arrays only.
            await SeedIssueAsync(setup, "issue_c",
                """{"id":"issue_c","number":3,"labels":{"kind":"bug","domain":"workflow"},"status":"in_progress"}""");
        }

        var stateBefore = await ReadStateAsync(database.CreateDbContext(), "issue_c");

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal(stateBefore, await ReadStateAsync(verify, "issue_c"));
    }

    [Fact]
    public async Task Up_MixedBatch_RewritesArraysOnly_LeavesDictsAlone()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_arr_empty",
                """{"id":"issue_arr_empty","number":10,"labels":[],"status":"done"}""");
            await SeedIssueAsync(setup, "issue_arr_full",
                """{"id":"issue_arr_full","number":11,"labels":["bug","runner","workflow"],"status":"backlog"}""");
            await SeedIssueAsync(setup, "issue_dict",
                """{"id":"issue_dict","number":12,"labels":{"kind":"feature"},"status":"backlog"}""");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("{}", await ReadLabelsRawAsync(verify, "issue_arr_empty"));
        Assert.Equal("{}", await ReadLabelsRawAsync(verify, "issue_arr_full"));
        Assert.Equal("""{"kind":"feature"}""", await ReadLabelsRawAsync(verify, "issue_dict"));
    }

    [Fact]
    public async Task Up_NonLabelsFields_Preserved()
    {
        // The json_set rewrite touches only $.labels; every other State field
        // — id, number, status, risk, completedAt, nested workflow refs — must
        // survive byte-for-byte. Guards against an over-broad rewrite.
        await using var database = CreateModelSchemaDatabase();
        const string state =
            """{"id":"issue_d","projectId":"proj_x","number":42,"title":"Keep me","status":"cancelled","priority":"p1","risk":"medium","labels":["bug","design"],"createdAt":"2026-06-01T00:00:00Z","updatedAt":"2026-06-02T00:00:00Z","completedAt":"2026-06-02T00:00:00Z","workflowRunId":"wr_abc","workflowStage":"plan"}""";
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_d", state);
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var after = await ReadStateAsync(verify, "issue_d");
        // Labels rewritten to {}; every other field preserved.
        Assert.Contains("\"number\":42", after);
        Assert.Contains("\"status\":\"cancelled\"", after);
        Assert.Contains("\"risk\":\"medium\"", after);
        Assert.Contains("\"workflowRunId\":\"wr_abc\"", after);
        Assert.Contains("\"labels\":{}", after);
        Assert.DoesNotContain("\"labels\":[\"bug\",\"design\"]", after);
    }

    [Fact]
    public async Task Up_SecondRun_IsIdempotent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_e",
                """{"id":"issue_e","number":5,"labels":["a"],"status":"backlog"}""");
        }

        await RunMigrationUpAsync(database);
        var afterFirst = await ReadStateAsync(database.CreateDbContext(), "issue_e");

        // A second run matches zero array rows and must be a no-op.
        await RunMigrationUpAsync(database);

        var afterSecond = await ReadStateAsync(database.CreateDbContext(), "issue_e");
        Assert.Equal(afterFirst, afterSecond);
        Assert.Contains("\"labels\":{}", afterSecond);
    }

    [Fact]
    public async Task Up_OnEmptyIssuesTable_IsNoOp()
    {
        await using var database = CreateModelSchemaDatabase();

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal(0, await CountRowsAsync(verify, "Issues"));
    }

    [Fact]
    public async Task DatabaseMigrate_IncludesBackfillIssueLegacyArrayLabelsMigration()
    {
        await using var database = CreateDatabase();
        await using var ctx = database.CreateDbContext();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationId);
    }

    [Fact]
    public async Task RealMigrationPipeline_RewritesArrayLabelsThroughMigrationBuilder()
    {
        // End-to-end: drive the actual migration class through the real EF
        // migration pipeline (IMigrator → MigrationBuilder → migrationBuilder.Sql)
        // rather than re-executing the SQL via ExecuteSqlRawAsync. This proves
        // the migration's own SQL — including the json('{}') payload — is not
        // subject to ExecuteSqlRaw-style '{' interpolation when the migrator
        // applies it, which is the path Program.cs Migrate() takes at startup.
        // 1. Bring the schema up to just before this migration.
        await using var database = CreateDatabase("20260702120000_BackfillIssueEventsTerminalTypeRename");

        // 2. Seed legacy array-form labels the way the pre-#149 era persisted them.
        await using (var seed = database.CreateDbContext())
        {
            await SeedHistoricalIssueAsync(seed, "issue_real_arr",
                """{"id":"issue_real_arr","number":100,"labels":["bug","workflow"],"status":"backlog"}""");
            await SeedHistoricalIssueAsync(seed, "issue_real_dict",
                """{"id":"issue_real_dict","number":101,"labels":{"kind":"feature"},"status":"backlog"}""");
        }

        // 3. Apply this migration via the real pipeline.
        var apply = database.CreateDbContext();
        await apply.GetService<IMigrator>().MigrateAsync(MigrationId);
        await apply.DisposeAsync();

        // 4. Array rewritten to {} through migrationBuilder.Sql; dict untouched.
        await using var verify = database.CreateDbContext();
        Assert.Equal("{}", await ReadLabelsRawAsync(verify, "issue_real_arr"));
        Assert.Equal("""{"kind":"feature"}""", await ReadLabelsRawAsync(verify, "issue_real_dict"));
    }

    [Fact]
    public void MigrationClass_DoesNotOverrideBuildTargetModel()
    {
        // Pure data backfill: the migration must not modify the EF model,
        // so BuildTargetModel is intentionally not overridden. Guards the
        // invariant — any future override would imply a model change, which
        // would need a Designer partial and a snapshot update.
        var method = typeof(BackfillIssueLegacyArrayLabels).GetMethod(
            "BuildTargetModel",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration), method!.DeclaringType);
    }

    private static async Task RunMigrationUpAsync(TestDatabase database)
    {
        await using var ctx = database.CreateDbContext();
        // json('{}') is parameterised: ExecuteSqlRawAsync treats '{' in the
        // literal SQL as a format placeholder, so the empty-object payload is
        // passed in as a parameter instead. This mirrors the migration's own
        // migrationBuilder.Sql (which is not subject to that interpolation).
        var emptyObject = "{}";
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE Issues
            SET State = json_set(State, '$.labels', json($emptyObject))
            WHERE json_type(State, '$.labels') = 'array';
            """,
            new SqliteParameter("$emptyObject", emptyObject));
    }

    private static async Task SeedIssueAsync(
        MohistDbContext ctx,
        string issueId,
        string stateJson)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO Issues (ProjectId, Number, State)
            VALUES (
                COALESCE(json_extract($state, '$.projectId'), json_extract($state, '$.ProjectId'), 'migration-test'),
                CAST(COALESCE(json_extract($state, '$.number'), json_extract($state, '$.Number')) AS INTEGER),
                $state);
            """;
        var stateParam = command.CreateParameter();
        stateParam.ParameterName = "$state";
        stateParam.Value = stateJson;
        command.Parameters.Add(stateParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedHistoricalIssueAsync(
        MohistDbContext ctx,
        string issueId,
        string stateJson)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "INSERT INTO Issues (IssueId, State) VALUES ($id, $state);";
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = issueId;
        command.Parameters.Add(idParam);
        var stateParam = command.CreateParameter();
        stateParam.ParameterName = "$state";
        stateParam.Value = stateJson;
        command.Parameters.Add(stateParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadStateAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT State FROM Issues WHERE json_extract(State, '$.id') = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = issueId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return (result as string)!;
    }

    private static async Task<string> ReadLabelsRawAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT json_extract(State, '$.labels') FROM Issues WHERE json_extract(State, '$.id') = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = issueId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return (result as string)!;
    }

    private static async Task<string> ReadLabelsTokenAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT json_type(State, '$.labels') FROM Issues WHERE json_extract(State, '$.id') = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = issueId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return (result as string)!;
    }

    private static async Task<long> CountRowsAsync(MohistDbContext ctx, string table)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
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

    private static TestDatabase CreateModelSchemaDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyModelSchemaTo(connection);
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
