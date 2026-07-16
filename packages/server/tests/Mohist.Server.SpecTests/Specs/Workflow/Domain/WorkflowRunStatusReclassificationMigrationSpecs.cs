using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Domain;

public class WorkflowRunStatusReclassificationMigrationSpecs
{
    private const string MigrationId = "20260702060000_WorkflowRunStatus";
    private const string PreviousMigrationId = "20260629120000_BackfillIssueCompletedAt";

    [Fact]
    public async Task Up_ReclassifiesOldPendingRow_ToCreated()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
            rowId = "wf_legacy_pending";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Pending",
                assignmentWorkerId: null,
                stagesInFlight: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("created", await ReadStateStatusAsync(verify, rowId));
        Assert.Equal("created", await ReadStatusColumnAsync(verify, rowId));
    }

    [Fact]
    public async Task Up_ReclassifiesOldRunningRowWithoutAssignment_ToPending()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
            rowId = "wf_legacy_running_unassigned";
            // Old "Running" with no assignment and no in-flight work.
            // Under the new state machine this is exactly Pending
            // (waiting for any worker).
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentWorkerId: null,
                stagesInFlight: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("pending", await ReadStateStatusAsync(verify, rowId));
        Assert.Equal("pending", await ReadStatusColumnAsync(verify, rowId));
    }

    [Fact]
    public async Task Up_KeepsOldRunningRowWithInFlightTask_AsRunning()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
            rowId = "wf_legacy_running_inflight_task";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_legacy_a",
                stagesInFlight: true,
                inFlightTask: true,
                inFlightChecksWorkId: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("running", await ReadStateStatusAsync(verify, rowId));
        Assert.Equal("running", await ReadStatusColumnAsync(verify, rowId));
    }

    [Fact]
    public async Task Up_KeepsOldRunningRowWithInFlightChecksWorkId_AsRunning()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
            rowId = "wf_legacy_running_inflight_checks";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_legacy_b",
                stagesInFlight: true,
                inFlightTask: false,
                inFlightChecksWorkId: true));
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("running", await ReadStateStatusAsync(verify, rowId));
        Assert.Equal("running", await ReadStatusColumnAsync(verify, rowId));
    }

    [Fact]
    public async Task Up_ReclassifiesOldRunningRowWithAssignmentAndNoInFlight_ToReady()
    {
        await using var database = CreateDatabase();
        string rowId;
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
            rowId = "wf_legacy_running_assigned_idle";
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_legacy_c",
                stagesInFlight: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal("ready", await ReadStateStatusAsync(verify, rowId));
        Assert.Equal("ready", await ReadStatusColumnAsync(verify, rowId));
    }

    [Fact]
    public async Task Up_LeavesTerminalRows_Unchanged()
    {
        await using var database = CreateDatabase();
        var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var setup = database.CreateDbContext())
        {
            await MigrateToPreviousAsync(setup);
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
                    assignmentWorkerId: null,
                    stagesInFlight: false));
                seeded[id] = status;
            }
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var verify = database.CreateDbContext();
        foreach (var (id, originalStatus) in seeded)
        {
            // The STORED column is normalized to lowercase via LOWER()
            // in the computed expression; this is the "Status
            // unchanged" contract — the column mirrors the
            // pre-migration value (lowercased) because the
            // reclassification UPDATEs do not match these statuses.
            Assert.Equal(originalStatus, await ReadStateStatusAsync(verify, id));
            Assert.Equal(CamelCaseStatus(originalStatus).ToLowerInvariant(), await ReadStatusColumnAsync(verify, id));
        }
    }

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
            await MigrateToPreviousAsync(setup);
            await SeedWorkflowRunAsync(setup, rowId, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_x",
                stagesInFlight: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);
        string firstState;
        await using (var firstRead = database.CreateDbContext())
        {
            firstState = await ReadStateAsync(firstRead, rowId);
        }

        await MigrateToWorkflowRunStatusAsync(database);

        await using var secondRead = database.CreateDbContext();
        var secondState = await ReadStateAsync(secondRead, rowId);
        Assert.Equal(firstState, secondState);
    }

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
            await MigrateToPreviousAsync(setup);
            await SeedWorkflowRunAsync(setup, assignedRow, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_e2e",
                stagesInFlight: false));
            await SeedWorkflowRunAsync(setup, unassignedRow, BuildState(
                status: "Running",
                assignmentWorkerId: null,
                stagesInFlight: false));
            await SeedWorkflowRunAsync(setup, inFlightRow, BuildState(
                status: "Running",
                assignmentWorkerId: "worker_e2e",
                stagesInFlight: true,
                inFlightTask: true,
                inFlightChecksWorkId: false));
        }

        await MigrateToWorkflowRunStatusAsync(database);

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
        Assert.Equal("ready", await ReadStateStatusAsync(verify, assignedRow));
        Assert.Equal("pending", await ReadStateStatusAsync(verify, unassignedRow));
        Assert.Equal("running", await ReadStateStatusAsync(verify, inFlightRow));
        Assert.Equal("ready", await ReadStatusColumnAsync(verify, assignedRow));
        Assert.Equal("pending", await ReadStatusColumnAsync(verify, unassignedRow));
        Assert.Equal("running", await ReadStatusColumnAsync(verify, inFlightRow));
    }

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
        string? assignmentWorkerId,
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
        var assignment = assignmentWorkerId is null
            ? ""
            : $$"""
              ,
              "assignment": {
                "runnerId": "{{assignmentWorkerId}}",
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

    private static Task MigrateToPreviousAsync(MohistDbContext ctx) =>
        ctx.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

    private static async Task MigrateToWorkflowRunStatusAsync(TestDatabase database)
    {
        await using var ctx = database.CreateDbContext();
        await ctx.GetService<IMigrator>().MigrateAsync(MigrationId);
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

    private static async Task<string?> ReadStateStatusAsync(MohistDbContext ctx, string workflowRunId)
    {
        var state = await ReadStateAsync(ctx, workflowRunId);
        using var document = JsonDocument.Parse(state);
        if (document.RootElement.TryGetProperty("status", out var status))
            return status.GetString();
        if (document.RootElement.TryGetProperty("Status", out var pascalStatus))
            return pascalStatus.GetString();
        return null;
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
