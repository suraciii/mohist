using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public partial class WorkflowRunStoreSpecs
{
    [Fact]
    public async Task SaveAsync_PersistsTerminalLogOwnershipRecordedByTaskSettlement()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = CreateStore(factory, new EventStore(factory, NullLogger<EventStore>.Instance));
        var run = new WorkflowRun
        {
            Id = "wr_terminal_log_ownership",
            Metadata = new WorkflowRunMetadata(null, FixedTime),
            Status = WorkflowRunStatus.Ready,
            Assignment = new WorkflowAssignment("worker-1", FixedTime),
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = "build.1",
                            DefinitionId = "build",
                            Attempt = 1,
                            Title = "Build",
                            Uses = "core/script",
                            Status = TaskRunStatus.Pending,
                        },
                    ],
                },
            ],
        };

        run.StartTask("work-terminal-log-ownership", "worker-1", FixedTime);
        run.CompleteTask(FixedTime);
        await store.SaveAsync(run);

        await using var db = new MohistDbContext(database.Options);
        var ownership = await db.TerminalLogOwnerships.SingleAsync(row =>
            row.OwnerKind == "workflow"
            && row.OwnerId == run.Id
            && row.WorkId == "work-terminal-log-ownership");
        Assert.Equal("worker-1", ownership.RunnerId);
    }

    [Fact]
    public async Task SaveAsync_RebuildsRunWideTaskMapAndActiveWorkProjection()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = CreateStore(factory, new EventStore(factory, NullLogger<EventStore>.Instance));
        var run = new WorkflowRun
        {
            Id = "wr_work_projection",
            Metadata = new WorkflowRunMetadata(null, FixedTime, ProjectId: ProjectId, IssueNumber: IssueNumber),
            Status = WorkflowRunStatus.Running,
            Assignment = new WorkflowAssignment("worker-1", FixedTime),
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Completed,
                    Initialized = true,
                    Tasks =
                    [
                        CreateTask("plan.1", TaskRunStatus.Completed, workId: null),
                        CreateTask("plan.2", TaskRunStatus.Failed, workId: "plan-work-2"),
                    ],
                },
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Initialized = true,
                    Tasks =
                    [
                        CreateTask("build.1", TaskRunStatus.Running, "build-work", "worker-1"),
                    ],
                },
            ],
        };

        await store.SaveAsync(run);

        await using (var db = new MohistDbContext(database.Options))
        {
            var rows = await db.WorkflowRunTaskMaps
                .AsNoTracking()
                .Where(row => row.WorkflowRunId == run.Id)
                .OrderBy(row => row.TaskId)
                .ToListAsync();

            Assert.Equal(
                [
                    (run.Id, "build.1", "build-work"),
                    (run.Id, "plan.1", "plan.1"),
                    (run.Id, "plan.2", "plan-work-2"),
                ],
                rows.Select(row => (row.WorkflowRunId, row.TaskId, row.WorkId)).ToArray());

            var storedRun = await db.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == run.Id);
            Assert.Equal("build-work", storedRun.ActiveWorkId);
            Assert.Equal("worker-1", storedRun.ActiveWorkerId);
        }

        run.CurrentStage().Tasks.Single().Status = TaskRunStatus.Completed;
        run.Status = WorkflowRunStatus.Completed;
        await store.SaveAsync(run);

        await using var reloadedDb = new MohistDbContext(database.Options);
        var inactive = await reloadedDb.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == run.Id);
        Assert.Null(inactive.ActiveWorkId);
        Assert.Null(inactive.ActiveWorkerId);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRunTaskMapRows()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = CreateStore(factory, new EventStore(factory, NullLogger<EventStore>.Instance));
        var run = new WorkflowRun
        {
            Id = "wr_work_projection_delete",
            Metadata = new WorkflowRunMetadata(null, FixedTime, ProjectId: ProjectId, IssueNumber: IssueNumber),
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Completed,
                    Initialized = true,
                    Tasks = [CreateTask("plan.1", TaskRunStatus.Completed, workId: "plan-work")],
                },
            ],
        };

        await store.SaveAsync(run);
        await store.DeleteAsync(run.Id);

        await using var db = new MohistDbContext(database.Options);
        Assert.Empty(await db.WorkflowRunTaskMaps.Where(row => row.WorkflowRunId == run.Id).ToListAsync());
        Assert.Null(await db.WorkflowRuns.SingleOrDefaultAsync(row => row.WorkflowRunId == run.Id));
    }

    private static TaskRun CreateTask(
        string id,
        TaskRunStatus status,
        string? workId,
        string? workerId = null) =>
        new()
        {
            Id = id,
            DefinitionId = id,
            Attempt = 1,
            Title = id,
            Status = status,
            WorkId = workId,
            WorkerId = workerId,
        };

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
