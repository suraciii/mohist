using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class WorkflowDispatchSnapshotDataUpgraderSpecs
{
    private const string RunningSnapshotJson = """{"workId":"t1.1","items":[{"prompt":"first-dispatch"}]}""";

    [Fact]
    public async Task ExternalizeAsync_ExternalizesRunningSnapshotsAndStripsAllDispatchSnapshot()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_externalize";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = LegacyEmbeddedState(runId),
        });

        await using var db = database.CreateContext();
        var result = await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
            db,
            backup: VerifyBackupDelegate);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(1, result.ExternalizedCount);
        Assert.Equal("verified-test-backup", result.BackupPath);

        var row = await LoadAsync(database, runId);
        Assert.Equal(2, await ETagAsync(database, runId));
        Assert.DoesNotContain("dispatchSnapshot", row.State);
        using var json = JsonDocument.Parse(row.State);
        Assert.Equal("running", json.RootElement.GetProperty("stages")[0].GetProperty("tasks")[0].GetProperty("status").GetString());
        Assert.Equal("completed", json.RootElement.GetProperty("stages")[0].GetProperty("tasks")[1].GetProperty("status").GetString());

        var snapshot = await SnapshotAsync(database, runId, "t1.1");
        Assert.NotNull(snapshot);
        Assert.Equal(RunningSnapshotJson, snapshot!.SnapshotJson);
        Assert.Null(await SnapshotAsync(database, runId, "t2.1"));
    }

    [Fact]
    public async Task ExternalizeAsync_InFlightSnapshotSurvivesUpgradeVerbatim()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_inflight";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = LegacyEmbeddedState(runId),
        });

        await using var db = database.CreateContext();
        await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
            db,
            backup: VerifyBackupDelegate);

        var snapshot = await SnapshotAsync(database, runId, "t1.1");
        Assert.Equal(RunningSnapshotJson, snapshot!.SnapshotJson);
        Assert.Equal("first-dispatch",
            JsonDocument.Parse(snapshot.SnapshotJson).RootElement.GetProperty("items")[0].GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ExternalizeAsync_IsIdempotentOnSecondRun()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_idempotent";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = LegacyEmbeddedState(runId),
        });

        await using (var db = database.CreateContext())
        {
            await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
                db,
                backup: VerifyBackupDelegate);
        }
        var stateAfterFirst = (await LoadAsync(database, runId)).State;

        await using var secondDb = database.CreateContext();
        var second = await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(secondDb);

        Assert.Equal(0, second.CandidateCount);
        Assert.Equal(0, second.WrittenCount);
        Assert.Equal(0, second.ExternalizedCount);
        Assert.Null(second.BackupPath);
        Assert.Equal(stateAfterFirst, (await LoadAsync(database, runId)).State);
        Assert.Equal(2, await ETagAsync(database, runId));
        Assert.Equal(RunningSnapshotJson, (await SnapshotAsync(database, runId, "t1.1"))?.SnapshotJson);
    }

    [Fact]
    public async Task ExternalizeAsync_LeavesRowsWithoutSnapshotsUntouched()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string slimId = "wr_already_slim";
        const string legacyId = "wr_still_embedded";
        var slim = $$"""{"id":"{{slimId}}","metadata":{"createdAt":"1970-01-01T00:00:00+00:00"},"status":"Running","stages":[]}""".Trim();
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = slimId, State = slim },
            new WorkflowRunRow { WorkflowRunId = legacyId, State = LegacyEmbeddedState(legacyId) });

        await using var db = database.CreateContext();
        var result = await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
            db,
            backup: VerifyBackupDelegate);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(slim, (await LoadAsync(database, slimId)).State);
        Assert.Equal(1, await ETagAsync(database, slimId));
        Assert.Equal(2, await ETagAsync(database, legacyId));
    }

    [Fact]
    public async Task ExternalizeAsync_PreflightFailureNamesRunAndWritesNothing()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string goodId = "wr_preflight_good";
        const string badId = "wr_preflight_bad";
        var good = LegacyEmbeddedState(goodId);
        var bad = LegacyEmbeddedState(badId, runStatus: "NotAWorkflowStatus");
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = goodId, State = good },
            new WorkflowRunRow { WorkflowRunId = badId, State = bad });

        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
                db,
                backup: static (_, _) => throw new InvalidOperationException("backup must not run")));

        Assert.Contains(badId, error.Message);
        Assert.Equal(good, (await LoadAsync(database, goodId)).State);
        Assert.Equal(bad, (await LoadAsync(database, badId)).State);
        Assert.Equal(1, await ETagAsync(database, goodId));
        Assert.Equal(1, await ETagAsync(database, badId));
        Assert.Null(await SnapshotAsync(database, goodId, "t1.1"));
    }

    [Fact]
    public async Task ExternalizeAsync_PreflightValidatesRowsWithoutSnapshots()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string goodId = "wr_unchanged_good";
        const string badId = "wr_unchanged_bad";
        var good = LegacyEmbeddedState(goodId);
        var bad = $$"""{"id":"{{badId}}","metadata":{"createdAt":"1970-01-01T00:00:00+00:00"},"status":"not-a-status","stages":[]}""";
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = goodId, State = good },
            new WorkflowRunRow { WorkflowRunId = badId, State = bad });

        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
                db,
                backup: static (_, _) => throw new InvalidOperationException("backup must not run")));

        Assert.Contains(badId, error.Message);
        Assert.Equal(good, (await LoadAsync(database, goodId)).State);
        Assert.Equal(1, await ETagAsync(database, goodId));
        Assert.Null(await SnapshotAsync(database, goodId, "t1.1"));
    }

    [Fact]
    public async Task ExternalizeAsync_DoesNotOverwritePreExistingSnapshot()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_existing_snapshot";
        const string existing = "{\"workId\":\"t1.1\",\"items\":[{\"prompt\":\"existing\"}]}";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = LegacyEmbeddedState(runId),
        });
        await InsertSnapshotsAsync(database, new WorkflowDispatchSnapshotRow
        {
            WorkflowRunId = runId,
            WorkId = "t1.1",
            SnapshotJson = existing,
        });

        await using var db = database.CreateContext();
        var result = await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
            db,
            backup: VerifyBackupDelegate);

        Assert.Equal(0, result.ExternalizedCount);
        Assert.Equal(existing, (await SnapshotAsync(database, runId, "t1.1"))?.SnapshotJson);
        Assert.DoesNotContain("dispatchSnapshot", (await LoadAsync(database, runId)).State);
    }

    [Fact]
    public async Task ExternalizeAsync_BackupFailurePreventsAllWrites()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_backup_fail";
        var legacy = LegacyEmbeddedState(runId);
        await InsertAsync(database, new WorkflowRunRow { WorkflowRunId = runId, State = legacy });

        var backupFailure = new InvalidOperationException("distinctive backup failure");
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(
                db,
                backup: (_, _) => Task.FromException<string>(backupFailure)));

        Assert.Same(backupFailure, error);
        Assert.Equal(legacy, (await LoadAsync(database, runId)).State);
        Assert.Equal(1, await ETagAsync(database, runId));
        Assert.Null(await SnapshotAsync(database, runId, "t1.1"));
    }

    [Fact]
    public async Task SweepOrphansAsync_DeletesNonRunningSnapshotsAndKeepsActive()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_orphan_sweep";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = MixedRunningTerminalState(runId),
        });
        await InsertSnapshotsAsync(database,
            new WorkflowDispatchSnapshotRow { WorkflowRunId = runId, WorkId = "active.1", SnapshotJson = "{}" },
            new WorkflowDispatchSnapshotRow { WorkflowRunId = runId, WorkId = "terminal.1", SnapshotJson = "{}" },
            new WorkflowDispatchSnapshotRow { WorkflowRunId = runId, WorkId = "missing.1", SnapshotJson = "{}" });

        await using var db = database.CreateContext();
        var swept = await WorkflowDispatchSnapshotDataUpgrader.SweepOrphansAsync(db);

        Assert.Equal(2, swept);
        Assert.NotNull(await SnapshotAsync(database, runId, "active.1"));
        Assert.Null(await SnapshotAsync(database, runId, "terminal.1"));
        Assert.Null(await SnapshotAsync(database, runId, "missing.1"));
    }

    [Fact]
    public async Task SweepOrphansAsync_PreflightFailureDeletesNothing()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_invalid_sweep";
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = "{\"status\":\"not-a-status\"}",
        });
        await InsertSnapshotsAsync(database,
            new WorkflowDispatchSnapshotRow { WorkflowRunId = runId, WorkId = "unknown.1", SnapshotJson = "{}" },
            new WorkflowDispatchSnapshotRow { WorkflowRunId = "wr_missing", WorkId = "missing.1", SnapshotJson = "{}" });

        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowDispatchSnapshotDataUpgrader.SweepOrphansAsync(db));

        Assert.Contains(runId, error.Message);
        Assert.NotNull(await SnapshotAsync(database, runId, "unknown.1"));
        Assert.NotNull(await SnapshotAsync(database, "wr_missing", "missing.1"));
    }

    [Fact]
    public async Task SweepOrphansAsync_IsNoOpWithNoSnapshots()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = "wr_empty_sweep",
            State = MixedRunningTerminalState("wr_empty_sweep"),
        });

        await using var db = database.CreateContext();
        Assert.Equal(0, await WorkflowDispatchSnapshotDataUpgrader.SweepOrphansAsync(db));
    }

    private static async Task<string> VerifyBackupDelegate(SqliteConnection source, CancellationToken cancellationToken)
    {
        Assert.Equal(ConnectionState.Open, source.State);
        await using var destination = new SqliteConnection("Data Source=:memory:");
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (await command.ExecuteScalarAsync(cancellationToken))?.ToString());
        return "verified-test-backup";
    }

    private static string LegacyEmbeddedState(string id, string runStatus = "running") => $$"""
        {
          "id": "{{id}}",
          "metadata": { "createdAt": "1970-01-01T00:00:00+00:00" },
          "status": "{{runStatus}}",
          "stages": [{
            "id": "build",
            "attempt": 1,
            "requiresApproval": false,
            "tasks": [
              {
                "id": "t1.1",
                "definitionId": "t1",
                "attempt": 1,
                "title": "T1",
                "status": "running",
                "workId": "t1.1",
                "dispatchSnapshot": {{RunningSnapshotJson}}
              },
              {
                "id": "t2.1",
                "definitionId": "t2",
                "attempt": 1,
                "title": "T2",
                "status": "completed",
                "workId": "t2.1",
                "dispatchSnapshot": {"workId":"t2.1","items":[]}
              }
            ],
            "checks": []
          }]
        }
        """;

    private static string MixedRunningTerminalState(string id) => $$"""
        {
          "id": "{{id}}",
          "metadata": { "createdAt": "1970-01-01T00:00:00+00:00" },
          "status": "running",
          "stages": [{
            "id": "build",
            "attempt": 1,
            "requiresApproval": false,
            "tasks": [
              { "id": "active.1", "definitionId": "a", "attempt": 1, "title": "A", "status": "running", "workId": "active.1" },
              { "id": "terminal.1", "definitionId": "b", "attempt": 1, "title": "B", "status": "failed", "workId": "terminal.1" }
            ],
            "checks": []
          }]
        }
        """;

    private static async Task InsertAsync(TestSqliteDatabase database, params WorkflowRunRow[] rows)
    {
        await using var db = database.CreateContext();
        db.WorkflowRuns.AddRange(rows);
        foreach (var row in rows)
            db.Entry(row).Property<long>("ETag").CurrentValue = 1;
        await db.SaveChangesAsync();
    }

    private static async Task InsertSnapshotsAsync(TestSqliteDatabase database, params WorkflowDispatchSnapshotRow[] rows)
    {
        await using var db = database.CreateContext();
        db.WorkflowDispatchSnapshots.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task<WorkflowRunRow> LoadAsync(TestSqliteDatabase database, string id)
    {
        await using var db = database.CreateContext();
        return await db.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == id);
    }

    private static async Task<long> ETagAsync(TestSqliteDatabase database, string id)
    {
        await using var db = database.CreateContext();
        var row = await db.WorkflowRuns.SingleAsync(value => value.WorkflowRunId == id);
        return db.Entry(row).Property<long>("ETag").CurrentValue;
    }

    private static async Task<WorkflowDispatchSnapshotRow?> SnapshotAsync(TestSqliteDatabase database, string runId, string workId)
    {
        await using var db = database.CreateContext();
        return await db.WorkflowDispatchSnapshots.SingleOrDefaultAsync(
            s => s.WorkflowRunId == runId && s.WorkId == workId);
    }
}
