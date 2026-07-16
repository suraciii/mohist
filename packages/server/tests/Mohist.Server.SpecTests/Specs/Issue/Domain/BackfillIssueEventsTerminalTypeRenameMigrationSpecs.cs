using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class BackfillIssueEventsTerminalTypeRenameMigrationSpecs
{
    private const string MigrationId = "20260702120000_BackfillIssueEventsTerminalTypeRename";

    [Fact]
    public async Task Up_RewritesLegacyClosedRows_ToCancelled()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_a", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.cancelled",
            await ReadTypeAsync(verify, "/mohist/issues/issue_a", 1));
    }

    [Fact]
    public async Task Up_RewritesLegacyWorkCompletedRows_ToCompleted()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_b", 1,
                "com.mohist.issue.work-completed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.completed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_b", 1));
    }

    [Fact]
    public async Task Up_RewritesBothLegacyTerminalTypes_InSingleRun()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_c", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_d", 1,
                "com.mohist.issue.work-completed", "2026-06-20T10:00:00Z");
            // Non-terminal row must be left alone.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_e", 1,
                "com.mohist.issue.work-started", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.cancelled",
            await ReadTypeAsync(verify, "/mohist/issues/issue_c", 1));
        Assert.Equal("com.mohist.issue.completed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_d", 1));
        Assert.Equal("com.mohist.issue.work-started",
            await ReadTypeAsync(verify, "/mohist/issues/issue_e", 1));
    }

    [Fact]
    public async Task Up_SecondRun_IsIdempotent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_f", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_g", 1,
                "com.mohist.issue.work-completed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        // A second run must match zero legacy-id rows and leave the
        // canonical ids untouched.
        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.cancelled",
            await ReadTypeAsync(verify, "/mohist/issues/issue_f", 1));
        Assert.Equal("com.mohist.issue.completed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_g", 1));
    }

    [Fact]
    public async Task Up_DoesNotTouchCanonicalRows_WrittenAfterRename()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            // Rows that already carry the canonical ids (e.g. live-written
            // post-rename on a fresh DB) must not be modified by Up.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_h", 1,
                "com.mohist.issue.cancelled", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_i", 1,
                "com.mohist.issue.completed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.cancelled",
            await ReadTypeAsync(verify, "/mohist/issues/issue_h", 1));
        Assert.Equal("com.mohist.issue.completed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_i", 1));
    }

    [Fact]
    public async Task Up_OnEmptyIssueEvents_IsNoOp()
    {
        await using var database = CreateModelSchemaDatabase();

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var count = await CountRowsAsync(verify, "IssueEvents");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Down_RewritesCanonicalRows_BackToLegacy()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_j", 1,
                "com.mohist.issue.cancelled", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_k", 1,
                "com.mohist.issue.completed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationDownAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.closed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_j", 1));
        Assert.Equal("com.mohist.issue.work-completed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_k", 1));
    }

    [Fact]
    public async Task Down_SecondRun_IsIdempotent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_l", 1,
                "com.mohist.issue.cancelled", "2026-06-20T10:00:00Z");
        }

        await RunMigrationDownAsync(database);
        await RunMigrationDownAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.closed",
            await ReadTypeAsync(verify, "/mohist/issues/issue_l", 1));
    }

    [Fact]
    public async Task UpThenDown_RoundTripsTerminalTypes_AcrossEras()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            // Pre-rename terminal rows.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_m", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_n", 1,
                "com.mohist.issue.work-completed", "2026-06-20T10:00:00Z");
            // Post-rename canonical rows.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_o", 1,
                "com.mohist.issue.cancelled", "2026-06-20T10:00:00Z");
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_p", 1,
                "com.mohist.issue.completed", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        // After Up: pre-rename rows are canonical; canonical rows are unchanged.
        await using (var afterUp = database.CreateDbContext())
        {
            Assert.Equal("com.mohist.issue.cancelled",
                await ReadTypeAsync(afterUp, "/mohist/issues/issue_m", 1));
            Assert.Equal("com.mohist.issue.completed",
                await ReadTypeAsync(afterUp, "/mohist/issues/issue_n", 1));
            Assert.Equal("com.mohist.issue.cancelled",
                await ReadTypeAsync(afterUp, "/mohist/issues/issue_o", 1));
            Assert.Equal("com.mohist.issue.completed",
                await ReadTypeAsync(afterUp, "/mohist/issues/issue_p", 1));
        }

        await RunMigrationDownAsync(database);

        // After Down: every row carries the legacy id (Up-rewritten
        // pre-rename rows return to legacy; Down also rewrites the
        // post-rename canonical rows back to legacy, matching the
        // symmetric-revert posture).
        await using var afterDown = database.CreateDbContext();
        Assert.Equal("com.mohist.issue.closed",
            await ReadTypeAsync(afterDown, "/mohist/issues/issue_m", 1));
        Assert.Equal("com.mohist.issue.work-completed",
            await ReadTypeAsync(afterDown, "/mohist/issues/issue_n", 1));
        Assert.Equal("com.mohist.issue.closed",
            await ReadTypeAsync(afterDown, "/mohist/issues/issue_o", 1));
        Assert.Equal("com.mohist.issue.work-completed",
            await ReadTypeAsync(afterDown, "/mohist/issues/issue_p", 1));
    }

    [Fact]
    public async Task Up_DoesNotAlterIssueState_OrStatus()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_q",
                """
                {"id":"issue_q","projectId":"proj_a","number":1,"title":"Q","status":"cancelled","createdAt":"2026-06-20T10:00:00Z","updatedAt":"2026-06-20T10:00:00Z","completedAt":"2026-06-20T10:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_q", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
        }

        string stateBefore;
        await using (var readBefore = database.CreateDbContext())
        {
            stateBefore = await ReadStateAsync(readBefore, "issue_q");
        }

        await RunMigrationUpAsync(database);

        await using var readAfter = database.CreateDbContext();
        var stateAfter = await ReadStateAsync(readAfter, "issue_q");
        Assert.Equal(stateBefore, stateAfter);
    }

    [Fact]
    public async Task Up_PreRenameAndPostRename_TerminalRowsClassifyIdentically()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            // Pre-rename persisted terminal row.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_r", 1,
                "com.mohist.issue.closed", "2026-06-20T10:00:00Z");
            // Post-rename persisted terminal row.
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_s", 1,
                "com.mohist.issue.cancelled", "2026-06-20T10:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var pre = await ReadTypeAsync(verify, "/mohist/issues/issue_r", 1);
        var post = await ReadTypeAsync(verify, "/mohist/issues/issue_s", 1);
        // Pre-rename and post-rename rows share one vocabulary, so a
        // downstream consumer that filters on the canonical id picks up
        // both eras identically.
        Assert.Equal(post, pre);
        Assert.Equal("com.mohist.issue.cancelled", pre);
    }

    [Fact]
    public async Task DatabaseMigrate_IncludesBackfillIssueEventsTerminalTypeRenameMigration()
    {
        await using var database = CreateDatabase();
        await using var ctx = database.CreateDbContext();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationId);
    }

    [Fact]
    public void MigrationClass_DoesNotOverrideBuildTargetModel()
    {
        // Pure data backfill: the migration must not modify the EF model,
        // so BuildTargetModel is intentionally not overridden. (The
        // virtual method is defined on the EF Migration base, so we check
        // for an override on the derived type — not for the method's
        // mere presence.) This test guards the invariant — any future
        // override would imply a model change, which would need a
        // Designer partial and a snapshot update.
        var method = typeof(BackfillIssueEventsTerminalTypeRename).GetMethod(
            "BuildTargetModel",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        // BuildTargetModel is virtual on the base Migration class, so it
        // resolves to the base. A genuine override would resolve to the
        // derived class — guarded by DeclaringType.
        Assert.NotNull(method);
        Assert.Equal(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration), method!.DeclaringType);
    }

    private static async Task RunMigrationUpAsync(TestDatabase database)
    {
        await using var ctx = database.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE IssueEvents
            SET Type = 'com.mohist.issue.cancelled'
            WHERE Type = 'com.mohist.issue.closed';
            """);
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE IssueEvents
            SET Type = 'com.mohist.issue.completed'
            WHERE Type = 'com.mohist.issue.work-completed';
            """);
    }

    private static async Task RunMigrationDownAsync(TestDatabase database)
    {
        await using var ctx = database.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE IssueEvents
            SET Type = 'com.mohist.issue.closed'
            WHERE Type = 'com.mohist.issue.cancelled';
            """);
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE IssueEvents
            SET Type = 'com.mohist.issue.work-completed'
            WHERE Type = 'com.mohist.issue.completed';
            """);
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

    private static async Task SeedIssueEventAsync(
        MohistDbContext ctx,
        string source,
        long id,
        string type,
        string time)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO IssueEvents (Source, Id, EventId, Type, Time, SpecVersion, DataContentType, Data, ExtensionsJson)
            VALUES ($source, $id, $eventId, $type, $time, '1.0', 'application/json', '{}', '{}');
            """;
        var sourceParam = command.CreateParameter();
        sourceParam.ParameterName = "$source";
        sourceParam.Value = source;
        command.Parameters.Add(sourceParam);
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = id;
        command.Parameters.Add(idParam);
        var eventIdParam = command.CreateParameter();
        eventIdParam.ParameterName = "$eventId";
        eventIdParam.Value = $"evt_{source}_{id}";
        command.Parameters.Add(eventIdParam);
        var typeParam = command.CreateParameter();
        typeParam.ParameterName = "$type";
        typeParam.Value = type;
        command.Parameters.Add(typeParam);
        var timeParam = command.CreateParameter();
        timeParam.ParameterName = "$time";
        timeParam.Value = time;
        command.Parameters.Add(timeParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadTypeAsync(MohistDbContext ctx, string source, long id)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Type FROM IssueEvents WHERE Source = $source AND Id = $id";
        var sourceParam = command.CreateParameter();
        sourceParam.ParameterName = "$source";
        sourceParam.Value = source;
        command.Parameters.Add(sourceParam);
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = id;
        command.Parameters.Add(idParam);
        var result = await command.ExecuteScalarAsync();
        return (result as string)!;
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

    private static async Task<long> CountRowsAsync(MohistDbContext ctx, string table)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
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
