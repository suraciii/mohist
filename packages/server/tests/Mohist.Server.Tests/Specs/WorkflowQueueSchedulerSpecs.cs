using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Scheduling;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowQueueSchedulerSpecs : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _dbFactory = null!;
    private WorkflowScheduler _scheduler = null!;
    private WorkflowQueueMaintenanceService _maintenance = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source=mohist-queue-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbFactory = new TestDbContextFactory(options);
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _scheduler = new WorkflowScheduler(_dbFactory, NullLogger<WorkflowScheduler>.Instance);
        _maintenance = new WorkflowQueueMaintenanceService(_dbFactory, _scheduler, NullLogger<WorkflowQueueMaintenanceService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Claim_LeasesQueuedWorkflow_AndPreventsSecondRunner()
    {
        await _scheduler.EnqueueAsync("wr-1", "project-a");

        var first = await _scheduler.ClaimAsync("runner-1", ["project-a"], 1);
        var second = await _scheduler.ClaimAsync("runner-2", ["project-a"], 1);

        Assert.NotNull(first);
        Assert.Equal("wr-1", first.WorkflowRunId);
        Assert.Equal("runner-1", first.RunnerId);
        Assert.Null(second);
    }

    [Fact]
    public async Task ExpiredLease_ReturnsToQueued_AndCanBeClaimedByAnotherRunner()
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkflowQueue.Add(new WorkflowQueueRow
            {
                WorkflowRunId = "wr-expired",
                ProjectId = "project-a",
                State = WorkflowQueueStates.Leased,
                RunnerId = "runner-old",
                WorkId = "task-1.1",
                WorkType = "task",
                Stage = "build",
                LogicalId = "task-1.1",
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        var expired = await _scheduler.ExpireLeasesAsync();
        var claimed = await _scheduler.ClaimAsync("runner-new", ["project-a"], 1);

        Assert.Equal(1, expired);
        Assert.NotNull(claimed);
        Assert.Equal("wr-expired", claimed.WorkflowRunId);
        Assert.Equal("runner-new", claimed.RunnerId);
    }

    [Fact]
    public async Task Maintain_ClearsQueueRows_ForNonRunnableWorkflows_AndEnqueuesMissingRunnableOnes()
    {
        var projectId = "test-project";
        var pausedId = SeedRun("wf-paused", WorkflowRunStatus.Paused, projectId, hasWork: true);
        var failedId = SeedRun("wf-failed", WorkflowRunStatus.Failed, projectId, hasWork: true);
        var queuedId = SeedRun("wf-queued", WorkflowRunStatus.Running, projectId, hasWork: true);
        var runnableId = SeedRun("wf-runnable", WorkflowRunStatus.Running, projectId, hasWork: true);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkflowQueue.AddRange(
                MakeQueueRow(pausedId, projectId, WorkflowQueueStates.Queued),
                MakeQueueRow(failedId, projectId, WorkflowQueueStates.Queued),
                MakeQueueRow(queuedId, projectId, WorkflowQueueStates.Queued),
                MakeQueueRow("wf-missing", projectId, WorkflowQueueStates.Queued));
            await db.SaveChangesAsync();
        }

        await _maintenance.MaintainOnceAsync();

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var ids = await db.WorkflowQueue.Select(row => row.WorkflowRunId).ToListAsync();
            Assert.DoesNotContain(pausedId, ids);
            Assert.DoesNotContain(failedId, ids);
            Assert.DoesNotContain("wf-missing", ids);
            Assert.Contains(runnableId, ids);
        }
    }

    [Fact]
    public async Task Maintain_ClearsLeasedQueueRow_WhenWorkflowHasNoRunnableWork()
    {
        var projectId = "test-project";
        var workflowId = SeedRun("wf-idle", WorkflowRunStatus.Running, projectId, hasWork: false);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkflowQueue.Add(MakeQueueRow(workflowId, projectId, WorkflowQueueStates.Leased,
                runnerId: "runner-x", workId: "task-1.1"));
            await db.SaveChangesAsync();
        }

        await _maintenance.MaintainOnceAsync();

        Assert.Null(await _scheduler.GetAsync(workflowId));
    }

    [Fact]
    public async Task Maintain_PreservesLeasedQueueRow_WhenWorkflowIsRunnable()
    {
        var projectId = "test-project";
        var workflowId = SeedRun("wf-active", WorkflowRunStatus.Running, projectId, hasWork: true);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkflowQueue.Add(MakeQueueRow(workflowId, projectId, WorkflowQueueStates.Leased,
                runnerId: "runner-x", workId: "task-1.1"));
            await db.SaveChangesAsync();
        }

        await _maintenance.MaintainOnceAsync();

        var row = await _scheduler.GetAsync(workflowId);
        Assert.NotNull(row);
        Assert.Equal(WorkflowQueueStates.Leased, row!.State);
        Assert.Equal("runner-x", row.RunnerId);
    }

    [Fact]
    public async Task Maintain_ClearsLeasedQueueRow_WhenWorkflowRunIsMissing()
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkflowQueue.Add(MakeQueueRow("wf-orphan", "test-project", WorkflowQueueStates.Leased,
                runnerId: "runner-orphan", workId: "task-1.1"));
            await db.SaveChangesAsync();
        }

        await _maintenance.MaintainOnceAsync();

        Assert.Null(await _scheduler.GetAsync("wf-orphan"));
    }

    private string SeedRun(string workflowId, WorkflowRunStatus status, string projectId, bool hasWork)
    {
        var taskStatus = hasWork ? TaskRunStatus.Pending : TaskRunStatus.Completed;
        var run = new WorkflowRun
        {
            Id = workflowId,
            Metadata = new WorkflowRunMetadata(
                Name: "test",
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: new Dictionary<string, string> { ["projectId"] = projectId }),
            Status = status,
            CurrentStageId = "build",
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    Initialized = true,
                    RequiresApproval = false,
                    Tasks = [new TaskRun { Id = "task-1.1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1", Status = taskStatus }],
                    Checks = []
                }
            ]
        };

        using var db = _dbFactory.CreateDbContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowId,
            State = JsonSerializer.Serialize(run, WorkflowStorageJson.Options)
        });
        db.WorkflowVariables.Add(new WorkflowVariablesRow
        {
            WorkflowRunId = workflowId,
            StateJson = JsonSerializer.Serialize(new WorkflowExecutionContext($$"""{"project":{"id":"{{projectId}}"}"""))
        });
        db.SaveChanges();
        return workflowId;
    }

    private static WorkflowQueueRow MakeQueueRow(
        string workflowId,
        string projectId,
        string state,
        string? runnerId = null,
        string? workId = null) => new()
    {
        WorkflowRunId = workflowId,
        ProjectId = projectId,
        State = state,
        RunnerId = runnerId,
        WorkId = workId,
        WorkType = workId is null ? null : "task",
        Stage = workId is null ? null : "build",
        LogicalId = workId is null ? null : "task-1",
        LeaseExpiresAt = runnerId is null ? null : DateTimeOffset.UtcNow.AddMinutes(2),
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MohistDbContext(options));
    }
}
