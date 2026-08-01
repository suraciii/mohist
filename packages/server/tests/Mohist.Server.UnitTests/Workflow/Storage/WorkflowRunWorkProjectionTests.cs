using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
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
                    ("WorkflowRunId", "State", "MetadataProjectId", "ActiveWorkId", "ActiveWorkerId", "ETag")
                VALUES ('wr_projection_read', {state}, 'proj_read', 'work-1', 'runner-1', 1);
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

        Assert.NotEmpty(recorder.Commands);
        Assert.DoesNotContain(recorder.Commands, command => command.Contains("State", StringComparison.OrdinalIgnoreCase));
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
