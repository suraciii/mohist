using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class WorkflowRunWorkProjectionDataUpgraderSpecs
{
    [Fact]
    public async Task UpgradeAsync_BackfillsTerminalAndActiveRunsAndIsIdempotent()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var terminal = TerminalRun("wr_projection_terminal");
        var active = ActiveRun("wr_projection_active");
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = terminal.Id, State = JSON.Serialize(terminal) },
            new WorkflowRunRow { WorkflowRunId = active.Id, State = JSON.Serialize(active) });

        await using (var db = database.CreateContext())
        {
            var result = await WorkflowRunWorkProjectionDataUpgrader.UpgradeAsync(db);

            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(2, result.WrittenCount);
        }

        var terminalState = await StateAsync(database, terminal.Id);
        var activeState = await StateAsync(database, active.Id);
        Assert.Equal(1, await ETagAsync(database, terminal.Id));
        Assert.Equal(1, await ETagAsync(database, active.Id));
        Assert.Equal(
            [
                (terminal.Id, "build.1", "build-work"),
                (terminal.Id, "plan.1", "plan.1"),
            ],
            await MapRowsAsync(database, terminal.Id));
        Assert.Equal(
            [
                (active.Id, "build.1", "active-work"),
                (active.Id, "plan.1", "plan-work"),
            ],
            await MapRowsAsync(database, active.Id));
        Assert.Null((await LoadAsync(database, terminal.Id)).ActiveWorkId);
        Assert.Null((await LoadAsync(database, terminal.Id)).ActiveWorkerId);
        Assert.Equal("active-work", (await LoadAsync(database, active.Id)).ActiveWorkId);
        Assert.Equal("worker-1", (await LoadAsync(database, active.Id)).ActiveWorkerId);

        await using (var db = database.CreateContext())
        {
            var result = await WorkflowRunWorkProjectionDataUpgrader.UpgradeAsync(db);

            Assert.Equal(0, result.CandidateCount);
            Assert.Equal(0, result.WrittenCount);
        }

        Assert.Equal(terminalState, await StateAsync(database, terminal.Id));
        Assert.Equal(activeState, await StateAsync(database, active.Id));
        Assert.Equal(1, await ETagAsync(database, terminal.Id));
        Assert.Equal(1, await ETagAsync(database, active.Id));
    }

    [Fact]
    public async Task UpgradeAsync_PreflightFailureWritesNoProjection()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var good = TerminalRun("wr_projection_good");
        const string badId = "wr_projection_bad";
        await InsertAsync(database,
            new WorkflowRunRow { WorkflowRunId = good.Id, State = JSON.Serialize(good) },
            new WorkflowRunRow { WorkflowRunId = badId, State = "{\"status\":\"not-a-status\"}" });

        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowRunWorkProjectionDataUpgrader.UpgradeAsync(db));

        Assert.Contains(badId, error.Message);
        Assert.Empty(await MapRowsAsync(database, good.Id));
        Assert.Null((await LoadAsync(database, good.Id)).ActiveWorkId);
        Assert.Null((await LoadAsync(database, good.Id)).ActiveWorkerId);
        Assert.Equal(1, await ETagAsync(database, good.Id));
    }

    private static WorkflowRun TerminalRun(string id) => new()
    {
        Id = id,
        Metadata = Metadata,
        Status = WorkflowRunStatus.Completed,
        Stages =
        [
            Stage("plan", StageRunStatus.Completed, Task("plan.1", TaskRunStatus.Completed, null)),
            Stage("build", StageRunStatus.Completed, Task("build.1", TaskRunStatus.Failed, "build-work")),
        ],
    };

    private static WorkflowRun ActiveRun(string id) => new()
    {
        Id = id,
        Metadata = Metadata,
        Status = WorkflowRunStatus.Running,
        Assignment = new WorkflowAssignment("worker-1", FixedTime),
        CurrentStageId = "build",
        Stages =
        [
            Stage("plan", StageRunStatus.Completed, Task("plan.1", TaskRunStatus.Completed, "plan-work")),
            Stage("build", StageRunStatus.Running, Task("build.1", TaskRunStatus.Running, "active-work", "worker-1")),
        ],
    };

    private static StageRun Stage(string id, StageRunStatus status, params TaskRun[] tasks) => new()
    {
        Id = id,
        Attempt = 1,
        RequiresApproval = false,
        Status = status,
        Initialized = true,
        Tasks = tasks.ToList(),
    };

    private static TaskRun Task(string id, TaskRunStatus status, string? workId, string? workerId = null) => new()
    {
        Id = id,
        DefinitionId = id,
        Attempt = 1,
        Title = id,
        Status = status,
        WorkId = workId,
        WorkerId = workerId,
    };

    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly WorkflowRunMetadata Metadata = new("run", FixedTime, ProjectId: "proj_projection", IssueNumber: 1);

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

    private static async Task<string> StateAsync(TestSqliteDatabase database, string id) =>
        (await LoadAsync(database, id)).State;

    private static async Task<long> ETagAsync(TestSqliteDatabase database, string id)
    {
        await using var db = database.CreateContext();
        var row = await db.WorkflowRuns.SingleAsync(value => value.WorkflowRunId == id);
        return db.Entry(row).Property<long>("ETag").CurrentValue;
    }

    private static async Task<(string WorkflowRunId, string TaskId, string WorkId)[]> MapRowsAsync(
        TestSqliteDatabase database,
        string id)
    {
        await using var db = database.CreateContext();
        var rows = await db.WorkflowRunTaskMaps
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == id)
            .OrderBy(row => row.TaskId)
            .ToArrayAsync();
        return rows.Select(row => (row.WorkflowRunId, row.TaskId, row.WorkId)).ToArray();
    }
}
