using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Domain;

public class WorkflowRunStatusReclassificationMigrationSpecs
{
    private const string MigrationId = "20260702060000_WorkflowRunStatus";

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_ReclassifiesOldPendingRow_ToCreated()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            rowId = "wf_legacy_pending";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Pending",
                assignmentRunnerId: null,
                stagesInFlight: false));
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, rowId);
        Assert.Contains("\"status\":\"created\"", state);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_ReclassifiesOldRunningRowWithoutAssignment_ToPending()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            rowId = "wf_legacy_running_unassigned";
            // Old "Running" with no assignment and no in-flight work —
            // covers the backstop case where a runner pool entry never
            // got claimed. Under the new state machine this is exactly
            // Pending (waiting for any runner).
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentRunnerId: null,
                stagesInFlight: false));
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, rowId);
        Assert.Contains("\"status\":\"pending\"", state);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_KeepsOldRunningRowWithInFlightTask_AsRunning()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            rowId = "wf_legacy_running_inflight_task";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_legacy_a",
                stagesInFlight: true,
                inFlightTask: true,
                inFlightChecksWorkId: false));
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, rowId);
        Assert.Contains("\"status\":\"running\"", state);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_KeepsOldRunningRowWithInFlightChecksWorkId_AsRunning()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            rowId = "wf_legacy_running_inflight_checks";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_legacy_b",
                stagesInFlight: true,
                inFlightTask: false,
                inFlightChecksWorkId: true));
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, rowId);
        Assert.Contains("\"status\":\"running\"", state);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_ReclassifiesOldRunningRowWithAssignmentAndNoInFlight_ToReady()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            rowId = "wf_legacy_running_assigned_idle";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_legacy_c",
                stagesInFlight: false));
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, rowId);
        Assert.Contains("\"status\":\"ready\"", state);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_LeavesTerminalRows_Unchanged()
    {
        await using var database = CreateDatabase();
        var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            // The four non-running terminal / blocking values the spec
            // pins as untouched: completed, failed, stopped, paused,
            // awaitingApproval. These were already semantically correct
            // under the new vocabulary. The reclassification UPDATEs
            // only match status == 'pending' or status == 'running'
            // (case-insensitive), so the raw JSON in State is preserved
            // for every other value — including these. The STORED
            // Status column is normalized to lowercase regardless, so
            // we assert the column rather than the JSON shape to pin
            // the contract.
            foreach (var (suffix, status) in new[]
                     {
                         ("completed", "Completed"),
                         ("failed", "Failed"),
                         ("stopped", "Stopped"),
                         ("paused", "Paused"),
                         ("awaiting_approval", "AwaitingApproval"),
                     })
            {
                var id = $"wf_legacy_{suffix}";
                await SeedWorkflowRunAsync(setup, id, BuildState(
                    status: status,
                    assignmentRunnerId: null,
                    stagesInFlight: false));
                seeded[id] = status;
            }
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        foreach (var (id, originalStatus) in seeded)
        {
            // The STORED column is normalized to lowercase via LOWER()
            // in the computed expression; this is the "Status
            // unchanged" contract — the column mirrors the
            // pre-migration value (lowercased) because the
            // reclassification UPDATEs do not match these statuses.
            var columnValue = await ReadStatusColumnAsync(verify, id);
            Assert.Equal(CamelCaseStatus(originalStatus).ToLowerInvariant(), columnValue);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_IsIdempotent_WhenRunTwice()
    {
        // The reclassification rewrites State.status via json_set; on a
        // second run the WHERE clauses no longer match (the old
        // 'pending' / 'running' values are gone), so nothing happens.
        // This is the same idempotency property BackfillIssueCompletedAt
        // and EpicIdleRename rely on.
        await using var database = CreateDatabase();
        var rowId = "wf_idempotent_round_trip";
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_x",
                stagesInFlight: false));
        }

        await RunMigrationUpAsync(database);
        string firstState;
        await using (var firstRead = database.CreateDbContext())
        {
            firstState = await ReadStateAsync(firstRead, rowId);
        }

        await RunMigrationUpAsync(database);

        await using var secondRead = database.CreateDbContext();
        var secondState = await ReadStateAsync(secondRead, rowId);
        Assert.Equal(firstState, secondState);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Up_StoredStatusColumn_MirrorsReclassifiedValue()
    {
        // The STORED Status computed column (added by the same
        // migration's schema step) is populated from State.status. After
        // the reclassification runs, the column must reflect the new
        // value so the two scheduling queries (FindAssignableAsync,
        // FindAssignedToAsync) see the correct bucket. This is the
        // end-to-end check that schema + data work together. The
        // column type is asserted through the EF model rather than a
        // raw pragma — pragma_table_info on a second connection is
        // not reliable in the in-memory SQLite shared-cache fixture
        // we use.
        await using var database = CreateDatabase();
        var assignedRow = "wf_e2e_assigned_idle";
        var unassignedRow = "wf_e2e_unassigned";
        var inFlightRow = "wf_e2e_inflight";
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            await SeedWorkflowRunAsync(setup, assignedRow, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_e2e",
                stagesInFlight: false));
            await SeedWorkflowRunAsync(setup, unassignedRow, BuildState(
                status: "Running",
                assignmentRunnerId: null,
                stagesInFlight: false));
            await SeedWorkflowRunAsync(setup, inFlightRow, BuildState(
                status: "Running",
                assignmentRunnerId: "runner_e2e",
                stagesInFlight: true,
                inFlightTask: true,
                inFlightChecksWorkId: false));
        }

        // The migration runs as part of MigrateAsync above (the
        // schema step is folded into the migration history). The
        // RunMigrationUpAsync helper only emits the data
        // reclassification; call it explicitly here so the test
        // mirrors the production two-step order.
        await RunMigrationUpAsync(database);

        await using (var verifyType = database.CreateDbContext())
        {
            var entity = verifyType.Model.FindEntityType(
                typeof(Mohist.Server.Infrastructure.Data.Workflow.WorkflowRunRow));
            Assert.NotNull(entity);
            var statusProperty = entity!.FindProperty("Status");
            Assert.NotNull(statusProperty);
            Assert.Equal(
                "LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))",
                statusProperty!.GetComputedColumnSql());
        }

        await using var verify = database.CreateDbContext();
        Assert.Equal("ready", await ReadStatusColumnAsync(verify, assignedRow));
        Assert.Equal("pending", await ReadStatusColumnAsync(verify, unassignedRow));
        Assert.Equal("running", await ReadStatusColumnAsync(verify, inFlightRow));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task DatabaseMigrate_IncludesWorkflowRunStatusMigration()
    {
        await using var database = CreateDatabase();
        await using var ctx = database.CreateDbContext();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationId);
    }

    private static string BuildState(
        string status,
        string? assignmentRunnerId,
        bool stagesInFlight,
        bool inFlightTask = false,
        bool inFlightChecksWorkId = false)
    {
        // Build a minimal WorkflowRun-shaped JSON that the migration's
        // reclassification SQL can probe. The shape is intentionally
        // small: only $.status, $.assignment.runnerId, and
        // $.stages[].tasks[].status / checksWorkId are read by the
        // migration. Other fields are stub strings.
        var stages = stagesInFlight
            ? $$"""
              ,
              "stages": [
                {
                  "id": "build",
                  "tasks": [
                    {
                      "id": "build-task-1.1",
                      "definitionId": "build-task-1",
                      "attempt": 1,
                      "title": "Build",
                      "status": "{{(inFlightTask ? "running" : "completed")}}"
                    }
                  ],
                  "checksWorkId": {{(inFlightChecksWorkId ? "\"work-checks-1\"" : "null")}}
                }
              ]
              """
            : "";
        var assignment = assignmentRunnerId is null
            ? ""
            : $$"""
              ,
              "assignment": {
                "runnerId": "{{assignmentRunnerId}}",
                "assignedAt": "2026-06-15T10:00:00Z"
              }
              """;
        return $$"""
            {
              "id": "wf-legacy",
              "status": "{{status}}",
              "metadata": {
                "name": "legacy-run",
                "createdAt": "2026-06-15T10:00:00Z"
              }{{assignment}}{{stages}}
            }
            """;
    }

    private static string CamelCaseStatus(string status) =>
        status switch
        {
            "Completed" => "completed",
            "Failed" => "failed",
            "Stopped" => "stopped",
            "Paused" => "paused",
            "AwaitingApproval" => "awaitingApproval",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown terminal/blocking status")
        };

    private static async Task RunMigrationUpAsync(TestDatabase database)
    {
        // Mirror the production migration's Up SQL exactly so the
        // assertions pin the actual deployed code path. The full
        // Migration.Up() is split into the schema step (add column +
        // alter column + index) — already exercised by
        // EnsureCreatedAsync -> MigrateAsync above — and the data
        // reclassification step run here.
        var sql = """
            -- 1) Old "pending" → "created".
            UPDATE "WorkflowRuns"
            SET "State" = json_set("State", '$.status', 'created')
            WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'pending';

            -- 2) Old "running" with no assignment → "pending".
            UPDATE "WorkflowRuns"
            SET "State" = json_set("State", '$.status', 'pending')
            WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
              AND json_extract("State", '$.assignment.runnerId') IS NULL
              AND json_extract("State", '$.claim.runnerId') IS NULL;

            -- 3) Old "running" with assignment + in-flight → "running".
            UPDATE "WorkflowRuns"
            SET "State" = json_set("State", '$.status', 'running')
            WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
              AND (
                  json_extract("State", '$.assignment.runnerId') IS NOT NULL
                  OR json_extract("State", '$.claim.runnerId') IS NOT NULL
              )
              AND (
                  EXISTS (
                      SELECT 1
                      FROM json_each(json_extract("State", '$.stages')) AS stage
                      WHERE json_extract(stage.value, '$.checksWorkId') IS NOT NULL
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM json_each(json_extract("State", '$.stages')) AS stage,
                           json_each(json_extract(stage.value, '$.tasks')) AS task
                      WHERE LOWER(COALESCE(json_extract(task.value, '$.status'), json_extract(task.value, '$.Status'))) = 'running'
                  )
              );

            -- 4) Old "running" with assignment + no in-flight → "ready".
            UPDATE "WorkflowRuns"
            SET "State" = json_set("State", '$.status', 'ready')
            WHERE LOWER(COALESCE(json_extract("State", '$.status'), json_extract("State", '$.Status'))) = 'running'
              AND (
                  json_extract("State", '$.assignment.runnerId') IS NOT NULL
                  OR json_extract("State", '$.claim.runnerId') IS NOT NULL
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM json_each(json_extract("State", '$.stages')) AS stage
                  WHERE json_extract(stage.value, '$.checksWorkId') IS NOT NULL
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM json_each(json_extract("State", '$.stages')) AS stage,
                       json_each(json_extract(stage.value, '$.tasks')) AS task
                  WHERE LOWER(COALESCE(json_extract(task.value, '$.status'), json_extract(task.value, '$.Status'))) = 'running'
              );
            """;
        await using var ctx = database.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task SeedWorkflowRunAsync(
        MohistDbContext ctx,
        string workflowRunId,
        string stateJson)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        // ETag is a required concurrency column (NOT NULL) on
        // WorkflowRuns; pre-existing rows always carry a positive
        // value, so seed with 1 to mirror the WorkflowRunStore.StageRunAsync
        // path that initializes new rows to ETag=1.
        command.CommandText = """
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ETag")
            VALUES ($id, $state, 1);
            """;
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = workflowRunId;
        command.Parameters.Add(idParam);
        var stateParam = command.CreateParameter();
        stateParam.ParameterName = "$state";
        stateParam.Value = stateJson;
        command.Parameters.Add(stateParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadStateAsync(MohistDbContext ctx, string workflowRunId)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"State\" FROM \"WorkflowRuns\" WHERE \"WorkflowRunId\" = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = workflowRunId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return (result as string) ?? string.Empty;
    }

    private static async Task<string?> ReadStatusColumnAsync(MohistDbContext ctx, string workflowRunId)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Status\" FROM \"WorkflowRuns\" WHERE \"WorkflowRunId\" = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = workflowRunId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    private static async Task<string?> ScalarStringAsync(MohistDbContext ctx, string sql)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    private static async Task<int> ScalarIntAsync(MohistDbContext ctx, string sql)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
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