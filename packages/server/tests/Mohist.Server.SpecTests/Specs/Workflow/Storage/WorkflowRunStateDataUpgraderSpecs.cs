using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class WorkflowRunStateDataUpgraderSpecs
{
    [Fact]
    public async Task UpgradeAsync_MigratesLegacyClaimAssignmentRunnerRecoveryAndProfileBinding()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string runId = "wr_legacy_shape";
        var legacy = LegacyState(runId);
        await InsertAsync(database, new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = legacy,
        });

        await using var db = database.CreateContext();
        var backupVerified = false;
        var result = await WorkflowRunStateDataUpgrader.UpgradeAsync(
            db,
            backup: async (source, cancellationToken) =>
            {
                Assert.Equal(ConnectionState.Open, source.State);
                await using var destination = new SqliteConnection("Data Source=:memory:");
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
                await using var command = destination.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", (await command.ExecuteScalarAsync(cancellationToken))?.ToString());
                backupVerified = true;
                return "verified-test-backup";
            });

        Assert.True(backupVerified);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal("verified-test-backup", result.BackupPath);

        var row = await LoadAsync(database, runId);
        Assert.NotEqual(legacy, row.State);
        Assert.Equal(2, await ETagAsync(database, runId));
        using var json = JsonDocument.Parse(row.State);
        var root = json.RootElement;
        Assert.Equal("runner-1", root.GetProperty("assignment").GetProperty("workerId").GetString());
        Assert.Equal("2026-01-01T00:00:00+00:00", root.GetProperty("assignment").GetProperty("assignedAt").GetString());
        Assert.Equal("runner-1", root.GetProperty("stages")[0].GetProperty("tasks")[0].GetProperty("workerId").GetString());
        Assert.Equal("legacy-profile", root.GetProperty("workflowProfileId").GetString());
        Assert.Equal(2, root.GetProperty("stages")[0].GetProperty("tasks")[0]
            .GetProperty("recoveryRemaining").GetInt32());
    }

    [Fact]
    public async Task UpgradeAsync_PreflightFailureNamesRunAndWritesNothing()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string canonicalId = "wr_canonical";
        const string ambiguousId = "wr_ambiguous";
        var canonical = CanonicalState(canonicalId);
        var ambiguous = AmbiguousState(ambiguousId);
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = canonicalId, State = canonical },
            new WorkflowRunRow { WorkflowRunId = ambiguousId, State = ambiguous });

        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.UpgradeAsync(
                db,
                backup: static (_, _) => throw new InvalidOperationException("backup must not run")));

        Assert.Contains(ambiguousId, error.Message);
        Assert.Equal(canonical, (await LoadAsync(database, canonicalId)).State);
        Assert.Equal(ambiguous, (await LoadAsync(database, ambiguousId)).State);
        Assert.Equal(1, await ETagAsync(database, canonicalId));
        Assert.Equal(1, await ETagAsync(database, ambiguousId));
    }

    [Fact]
    public async Task UpgradeAsync_BackupFailurePreventsAllStateAndETagWrites()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string firstId = "wr_backup_failure_a";
        const string secondId = "wr_backup_failure_b";
        var first = LegacyState(firstId);
        var second = LegacyState(secondId);
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = firstId, State = first },
            new WorkflowRunRow { WorkflowRunId = secondId, State = second });

        var backupFailure = new InvalidOperationException("distinctive backup failure");
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.UpgradeAsync(
                db,
                backup: (_, _) => Task.FromException<string>(backupFailure)));

        Assert.Same(backupFailure, error);
        Assert.Equal(first, (await LoadAsync(database, firstId)).State);
        Assert.Equal(second, (await LoadAsync(database, secondId)).State);
        Assert.Equal(1, await ETagAsync(database, firstId));
        Assert.Equal(1, await ETagAsync(database, secondId));
    }

    [Fact]
    public async Task UpgradeAsync_RollsBackAllStateAndETagWritesWhenOneRowFails()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var first = LegacyState("wr_atomic_a");
        var second = LegacyState("wr_atomic_b");
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = "wr_atomic_a", State = first },
            new WorkflowRunRow { WorkflowRunId = "wr_atomic_b", State = second });

        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER WorkflowRunStateUpgradeFailure
                BEFORE UPDATE OF State ON WorkflowRuns
                WHEN OLD.WorkflowRunId = 'wr_atomic_b'
                BEGIN
                    SELECT RAISE(ABORT, 'workflow run state upgrade failure');
                END;
                """);
        }

        await using var upgradeDb = database.CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            WorkflowRunStateDataUpgrader.UpgradeAsync(
                upgradeDb,
                backup: static (_, _) => Task.FromResult("verified-test-backup")));

        Assert.Equal(first, (await LoadAsync(database, "wr_atomic_a")).State);
        Assert.Equal(second, (await LoadAsync(database, "wr_atomic_b")).State);
        Assert.Equal(1, await ETagAsync(database, "wr_atomic_a"));
        Assert.Equal(1, await ETagAsync(database, "wr_atomic_b"));
    }

    [Fact]
    public async Task UpgradeAsync_LeavesCanonicalRowsUntouchedAndIsIdempotent()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        const string canonicalId = "wr_noop";
        const string legacyId = "wr_once";
        var canonical = CanonicalState(canonicalId);
        var legacy = LegacyState(legacyId);
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = canonicalId, State = canonical },
            new WorkflowRunRow { WorkflowRunId = legacyId, State = legacy });

        await using var db = database.CreateContext();
        var first = await WorkflowRunStateDataUpgrader.UpgradeAsync(
            db,
            backup: static (_, _) => Task.FromResult("verified-test-backup"));
        var migratedState = (await LoadAsync(database, legacyId)).State;
        Assert.Equal(1, first.CandidateCount);
        Assert.Equal(1, first.WrittenCount);
        Assert.Equal(canonical, (await LoadAsync(database, canonicalId)).State);
        Assert.Equal(1, await ETagAsync(database, canonicalId));
        Assert.Equal(2, await ETagAsync(database, legacyId));

        var second = await WorkflowRunStateDataUpgrader.UpgradeAsync(db);
        Assert.Equal(0, second.CandidateCount);
        Assert.Equal(0, second.WrittenCount);
        Assert.Null(second.BackupPath);
        Assert.Equal(migratedState, (await LoadAsync(database, legacyId)).State);
        Assert.Equal(2, await ETagAsync(database, legacyId));
    }

    [Fact]
    public async Task UpgradeAsync_UsesBatchesForMoreThanFiveHundredCandidates()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var rows = Enumerable.Range(0, 1001)
            .Select(index => new WorkflowRunRow
            {
                WorkflowRunId = $"wr_batch_{index:D4}",
                State = LegacyState($"wr_batch_{index:D4}"),
            })
            .ToArray();
        await InsertAsync(database, rows);

        await using var db = database.CreateContext();
        var result = await WorkflowRunStateDataUpgrader.UpgradeAsync(
            db,
            backup: static (_, _) => Task.FromResult("verified-test-backup"));

        Assert.Equal(1001, result.CandidateCount);
        Assert.Equal(1001, result.WrittenCount);
        Assert.Equal(2, await ETagAsync(database, "wr_batch_0000"));
        Assert.Equal(2, await ETagAsync(database, "wr_batch_1000"));
    }

    [Fact]
    public async Task CreateAndVerifyBackupAsync_RejectsNonPersistentSourceWithoutChangingOpenState()
    {
        await using var source = new SqliteConnection("Data Source=:memory:");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.CreateAndVerifyBackupAsync(source));

        Assert.Equal(ConnectionState.Closed, source.State);
    }

    private static string LegacyState(string id) => $$"""
        {
          "id": "{{id}}",
          "metadata": {
            "createdAt": "1970-01-01T00:00:00+00:00",
            "annotations": { "workflowProfileId": "legacy-profile" }
          },
          "status": "Failed",
          "claim": { "runnerId": "runner-1", "claimedAt": "2026-01-01T00:00:00+00:00" },
          "dispatchActivated": true,
          "stages": [{
            "id": "check",
            "attempt": 1,
            "requiresApproval": false,
            "initialized": true,
            "status": "Failed",
            "tasks": [{
              "id": "review.1",
              "definitionId": "review",
              "attempt": 1,
              "title": "Review",
              "uses": "spec/review",
              "status": "Pending",
              "runnerId": "runner-1",
              "recovery": { "budget": 2, "handlers": [] }
            }],
            "checks": []
          }]
        }
        """;

    private static string AmbiguousState(string id) => $$"""
        {
          "id": "{{id}}",
          "metadata": { "createdAt": "1970-01-01T00:00:00+00:00" },
          "status": "Failed",
          "stages": [{
            "id": "check",
            "tasks": [
              { "definitionId": "review", "attempt": 1, "recovery": { "budget": 2, "handlers": [] } },
              { "definitionId": "review", "attempt": 2, "recovery": { "budget": 1, "handlers": [{ "when": "different", "tasks": [], "retrySelf": true }] } }
            ],
            "checks": []
          }]
        }
        """;

    private static string CanonicalState(string id) => $$"""
        {"id":"{{id}}","metadata":{"createdAt":"1970-01-01T00:00:00+00:00"},"status":"Failed","stages":[]}
        """.Trim();

    private static async Task InsertAsync(TestSqliteDatabase database, params WorkflowRunRow[] rows)
    {
        await using var db = database.CreateContext();
        db.WorkflowRuns.AddRange(rows);
        foreach (var row in rows)
            db.Entry(row).Property<long>("ETag").CurrentValue = 1;
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
}
