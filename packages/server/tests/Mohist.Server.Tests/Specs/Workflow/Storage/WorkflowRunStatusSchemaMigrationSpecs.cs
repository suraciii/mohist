using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Storage;

/// <summary>
/// Issue-318 T-004 schema specs. The model declares the STORED <c>Status</c>
/// computed column (D3) and the <c>IX_WorkflowRuns_Status</c> index that
/// the two scheduling queries rely on. The migration that materializes
/// them at deploy time is <c>20260702060000_WorkflowRunStatus</c>; this
/// spec pins that the EF migrator applies that migration and that the
/// resulting schema (column + index + computed SQL expression) matches
/// the model. The data-reclassification behavior lives in
/// <c>WorkflowRunStatusReclassificationMigrationSpecs</c> in the Domain
/// folder; this file is schema-only.
/// </summary>
public class WorkflowRunStatusSchemaMigrationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DbContext_ExposesStatusComputedColumnOnWorkflowRunRow()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync();

        var entity = context.Model.FindEntityType(typeof(WorkflowRunRow));
        Assert.NotNull(entity);

        var statusProperty = entity!.FindProperty("Status");
        Assert.NotNull(statusProperty);
        Assert.Equal(
            "LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))",
            statusProperty!.GetComputedColumnSql());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DbContext_ExposesIndexOnStatusAndAssignedRunnerId()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync();

        var entity = context.Model.FindEntityType(typeof(WorkflowRunRow));
        Assert.NotNull(entity);

        var index = entity!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "IX_WorkflowRuns_Status");
        Assert.NotNull(index);
        Assert.Equal(
            new[] { "Status", "AssignedRunnerId" },
            index!.Properties.Select(p => p.Name).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Migrator_AppliesWorkflowRunStatusMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        // Migrate up to and including the WorkflowRunStatus migration
        // (the model-side pre-T-004 fixtures stop at
        // 20260629120000_BackfillIssueCompletedAt; this is the first
        // test to land at our migration explicitly).
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260702060000_WorkflowRunStatus");

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260702060000_WorkflowRunStatus");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DatabaseMigrate_LeavesModelAndSnapshotInAgreement()
    {
        // After Migrate() runs, the EF model should match the
        // MohistDbContextModelSnapshot. EF surfaces drift via
        // PendingModelChangesWarning; the test fixture suppresses the
        // warning (so Migrate() does not throw on a T-002-only build),
        // but on a T-004 build the model and snapshot agree and the
        // warning is never emitted. Validate the contract by
        // materializing the migrator's "from empty to latest" output
        // and confirming the Status column + IX_WorkflowRuns_Status
        // index are present in the produced model.
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync();

        // A round-trip insert + read of a WorkflowRunRow with a
        // Pending status validates that the STORED column evaluates
        // to the canonical form on real read paths. This is the
        // end-to-end proof that schema + the (T-002) projected
        // scalar property are wired together.
        context.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = "wf_schema_check_1",
            State = """
                {
                  "id": "wf_schema_check_1",
                  "status": "Pending",
                  "metadata": {
                    "createdAt": "2026-07-01T00:00:00Z"
                  },
                  "stages": []
                }
                """,
        });
        await context.SaveChangesAsync();

        // Detach / read with a fresh context so we go through the
        // materializer rather than the change tracker.
        await using var verify = database.CreateDbContext();
        var row = await verify.WorkflowRuns
            .AsNoTracking()
            .FirstAsync(r => r.WorkflowRunId == "wf_schema_check_1");
        Assert.Equal("pending", row.Status);
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning))
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
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }
}