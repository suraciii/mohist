using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Storage;

public sealed class WorkflowRunWorkProjectionTests
{
    [Fact]
    public async Task ReadsReturnProjectionValuesWithoutSelectingState()
    {
        var connection = new SqliteConnection($"Data Source=workflow-work-projection-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await using var database = connection;
        var recorder = new SelectRecorder();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .Options;

        await using (var db = new MohistDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WorkflowRuns" (
                    "WorkflowRunId" TEXT NOT NULL PRIMARY KEY,
                    "State" TEXT NOT NULL,
                    "EpicNumber" INTEGER NULL,
                    "MetadataProjectId" TEXT NULL,
                    "CreatedAt" TEXT NULL,
                    "AssignedWorkerId" TEXT NULL,
                    "ReadySince" TEXT NULL,
                    "Status" TEXT NULL,
                    "IssueNumber" INTEGER NULL,
                    "ActiveWorkId" TEXT NULL,
                    "ActiveWorkerId" TEXT NULL,
                    "WorkflowProfileIdKey" TEXT NULL,
                    "ETag" INTEGER NOT NULL
                );
                CREATE TABLE "WorkflowRunTaskMap" (
                    "WorkflowRunId" TEXT NOT NULL,
                    "TaskId" TEXT NOT NULL,
                    "WorkId" TEXT NOT NULL,
                    PRIMARY KEY ("WorkflowRunId", "TaskId")
                );
                """);
            var state = "{\"metadata\":{\"projectId\":\"proj_read\"}}";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WorkflowRuns"
                    ("WorkflowRunId", "State", "Status", "MetadataProjectId", "ActiveWorkId", "ActiveWorkerId", "ETag")
                VALUES ('wr_projection_read', {state}, 'running', 'proj_read', 'work-1', 'runner-1', 1);
                INSERT INTO "WorkflowRuns"
                    ("WorkflowRunId", "State", "Status", "ActiveWorkId", "ActiveWorkerId", "ETag")
                VALUES ('wr_projection_stale', {"{\"status\":\"completed\"}"}, 'completed', 'work-1', 'runner-1', 1);
                INSERT INTO "WorkflowRunTaskMap" ("WorkflowRunId", "TaskId", "WorkId")
                VALUES ('wr_projection_read', 'task-1', 'work-1');
                """);
        }

        recorder.Commands.Clear();
        var projection = new WorkflowRunWorkProjection(new TestDbContextFactory(options));

        Assert.Equal("work-1", await projection.ResolveWorkIdAsync("wr_projection_read", "task-1"));
        Assert.Equal("task-1", await projection.ResolveTaskIdAsync("wr_projection_read", "work-1"));
        Assert.True(await projection.IsActiveWorkAsync("wr_projection_read", "work-1", "runner-1"));
        Assert.False(await projection.IsActiveWorkAsync("wr_projection_read", "work-1", "runner-2"));
        Assert.Equal("proj_read", await projection.GetProjectIdAsync("wr_projection_read"));
        Assert.Null(await projection.ResolveWorkIdAsync("wr_missing", "task-1"));
        Assert.False(await projection.IsActiveWorkAsync("wr_missing", "work-1", "runner-1"));
        Assert.False(await projection.IsActiveWorkAsync("wr_projection_stale", "work-1", "runner-1"));

        Assert.NotEmpty(recorder.Commands);
        Assert.DoesNotContain(recorder.Commands, command => command.Contains("State", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TerminalWorkReadsAuthoritativeRunStateAfterActiveProjectionIsCleared()
    {
        var connection = new SqliteConnection($"Data Source=workflow-terminal-work-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await using var database = connection;
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        WorkflowRun stoppedRun;

        await using (var db = new MohistDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "WorkflowRuns" (
                    "WorkflowRunId" TEXT NOT NULL PRIMARY KEY,
                    "State" TEXT NOT NULL,
                    "EpicNumber" INTEGER NULL,
                    "MetadataProjectId" TEXT NULL,
                    "CreatedAt" TEXT NULL,
                    "AssignedWorkerId" TEXT NULL,
                    "ReadySince" TEXT NULL,
                    "Status" TEXT NULL,
                    "IssueNumber" INTEGER NULL,
                    "ActiveWorkId" TEXT NULL,
                    "ActiveWorkerId" TEXT NULL,
                    "WorkflowProfileIdKey" TEXT NULL,
                    "ETag" INTEGER NOT NULL
                );
                CREATE TABLE "WorkflowRunTaskMap" (
                    "WorkflowRunId" TEXT NOT NULL,
                    "TaskId" TEXT NOT NULL,
                    "WorkId" TEXT NOT NULL,
                    PRIMARY KEY ("WorkflowRunId", "TaskId")
                );
                """);

            var run = new WorkflowRun
            {
                Id = "wr_terminal_projection",
                Metadata = new WorkflowRunMetadata("terminal", new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                Status = WorkflowRunStatus.Completed,
                CurrentStageId = "build",
                Assignment = new WorkflowAssignment("runner-terminal", new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                Stages =
                [
                    new StageRun
                    {
                        Id = "build",
                        Attempt = 1,
                        RequiresApproval = false,
                        Status = StageRunStatus.Completed,
                        Tasks =
                        [
                            new TaskRun
                            {
                                Id = "task-previous",
                                DefinitionId = "task-previous",
                                Attempt = 1,
                                Title = "Previous task",
                                Uses = "core/script",
                                WorkId = "work-previous",
                                WorkerId = "runner-terminal",
                                Status = TaskRunStatus.Completed,
                                Classification = TaskClassification.Orchestration,
                            },
                            new TaskRun
                            {
                                Id = "task-terminal",
                                DefinitionId = "task-terminal",
                                Attempt = 1,
                                Title = "Terminal task",
                                Uses = "core/script",
                                WorkId = "work-terminal",
                                WorkerId = "runner-terminal",
                                Status = TaskRunStatus.Completed,
                                Classification = TaskClassification.Orchestration,
                            },
                        ],
                    },
                ],
            };
            stoppedRun = new WorkflowRun
            {
                Id = "wr_stopped_projection",
                Metadata = new WorkflowRunMetadata("stopped", new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                Status = WorkflowRunStatus.Stopped,
                CurrentStageId = "build",
                Assignment = new WorkflowAssignment("runner-stopped", new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
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
                                Id = "task-stopped-previous",
                                DefinitionId = "task-stopped-previous",
                                Attempt = 1,
                                Title = "Previous stopped-run task",
                                Uses = "core/script",
                                WorkId = "work-stopped-previous",
                                WorkerId = "runner-stopped",
                                Status = TaskRunStatus.Completed,
                                Classification = TaskClassification.Orchestration,
                            },
                            new TaskRun
                            {
                                Id = "task-interrupted",
                                DefinitionId = "task-interrupted",
                                Attempt = 1,
                                Title = "Interrupted task",
                                Uses = "core/script",
                                WorkId = "work-interrupted",
                                WorkerId = "runner-stopped",
                                Status = TaskRunStatus.Running,
                                Classification = TaskClassification.Orchestration,
                            },
                        ],
                    },
                ],
            };

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ActiveWorkId", "ActiveWorkerId", "ETag")
                VALUES ('wr_terminal_projection', {JSON.Serialize(run)}, NULL, NULL, 1),
                       ('wr_stopped_projection', {JSON.Serialize(stoppedRun)}, 'work-interrupted', 'runner-stopped', 1);
                """);
        }

        var projection = new WorkflowRunWorkProjection(new TestDbContextFactory(options));

        Assert.True(await projection.IsTerminalWorkAsync("wr_terminal_projection", "work-terminal", "runner-terminal"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_terminal_projection", "work-previous", "runner-terminal"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_terminal_projection", "work-terminal", "runner-other"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_terminal_projection", "work-other", "runner-terminal"));
        Assert.True(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-interrupted", "runner-stopped"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-stopped-previous", "runner-stopped"));

        stoppedRun.Stages[0].Tasks[0].Status = TaskRunStatus.Running;
        await UpdateStateAsync(options, stoppedRun);
        Assert.False(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-interrupted", "runner-stopped"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-stopped-previous", "runner-stopped"));

        stoppedRun.Stages[0].Tasks[0].Status = TaskRunStatus.Completed;
        stoppedRun.Stages[0].Tasks[1].Status = TaskRunStatus.Completed;
        await UpdateStateAsync(options, stoppedRun);
        Assert.False(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-interrupted", "runner-stopped"));
        Assert.False(await projection.IsTerminalWorkAsync("wr_stopped_projection", "work-stopped-previous", "runner-stopped"));
    }

    private static async Task UpdateStateAsync(DbContextOptions<MohistDbContext> options, WorkflowRun run)
    {
        await using var db = new MohistDbContext(options);
        await db.WorkflowRuns
            .Where(row => row.WorkflowRunId == run.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, JSON.Serialize(run)));
    }

    private sealed class SelectRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
