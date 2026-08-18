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
    public async Task SaveAsync_KeepsUnknownActiveLeaseAndClearsBlockedProjectionForCapacity()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = CreateStore(factory, new EventStore(factory, NullLogger<EventStore>.Instance));
        var unknown = new WorkflowRun
        {
            Id = "wr_unknown_attention",
            Metadata = new WorkflowRunMetadata(null, FixedTime, ProjectId: ProjectId, IssueNumber: IssueNumber),
            Status = WorkflowRunStatus.Running,
            Assignment = new WorkflowAssignment("runner-1", FixedTime),
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
                            Uses = "mohist/opencode",
                            Status = TaskRunStatus.Running,
                            WorkId = "build-work",
                            WorkerId = "runner-1",
                            AgentResultSettlement = new AgentResultSettlement
                            {
                                State = AgentResultSettlementState.Unknown,
                                TaskRunId = "build.1",
                                WorkId = "build-work",
                                RunnerId = "runner-1",
                                ReasonCode = "stop-unconfirmed",
                                DeadlineAt = FixedTime.AddMinutes(5),
                            },
                        },
                    ],
                },
            ],
        };

        await store.SaveAsync(unknown);

        var querier = new WorkflowRunQuerier(factory);
        Assert.Equal([unknown.Id], await querier.FindRunningAssignedToAsync("runner-1"));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync("runner-1"));
        Assert.Empty(await querier.FindBlockedAsync(ProjectId));
        Assert.Empty(await querier.FindBlockedAsync("other-project"));
        await using (var unknownDb = new MohistDbContext(database.Options))
        {
            var row = await unknownDb.WorkflowRuns.SingleAsync(run => run.WorkflowRunId == unknown.Id);
            Assert.Equal("build-work", row.ActiveWorkId);
            Assert.Equal("runner-1", row.ActiveWorkerId);
            Assert.Null(row.AttentionStatus);
        }

        unknown.CurrentStage().Tasks.Single().AgentResultSettlement!.State = AgentResultSettlementState.Blocked;
        unknown.Assignment = null;
        await store.SaveAsync(unknown);

        Assert.Equal([unknown.Id], await querier.FindBlockedAsync(ProjectId));
        Assert.Empty(await querier.FindRunningAssignedToAsync("runner-1"));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync("runner-1"));
        await using (var blockedDb = new MohistDbContext(database.Options))
        {
            var row = await blockedDb.WorkflowRuns.SingleAsync(run => run.WorkflowRunId == unknown.Id);
            Assert.Null(row.ActiveWorkId);
            Assert.Null(row.ActiveWorkerId);
            Assert.Equal("blocked", row.AttentionStatus);
        }

        // A matching late authoritative result clears the settlement through
        // the terminal path; the projection then drops blocked attention and
        // the row stays clear of active-work queries.
        var task = unknown.CurrentStage().Tasks.Single();
        task.AgentResultSettlement = null;
        task.Status = TaskRunStatus.Completed;
        unknown.Status = WorkflowRunStatus.Completed;
        await store.SaveAsync(unknown);

        Assert.Empty(await querier.FindBlockedAsync(ProjectId));
        Assert.Empty(await querier.FindRunningAssignedToAsync("runner-1"));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync("runner-1"));
        await using (var completedDb = new MohistDbContext(database.Options))
        {
            var row = await completedDb.WorkflowRuns.SingleAsync(run => run.WorkflowRunId == unknown.Id);
            Assert.Null(row.AttentionStatus);
            Assert.Null(row.ActiveWorkId);
            Assert.Null(row.ActiveWorkerId);
        }
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
