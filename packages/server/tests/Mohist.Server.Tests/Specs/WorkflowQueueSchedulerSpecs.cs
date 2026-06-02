using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Scheduling;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowQueueSchedulerSpecs : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestDbContextFactory _dbFactory = null!;
    private WorkflowScheduler _scheduler = null!;

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

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MohistDbContext(options));
    }
}
