using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public partial class WorkflowRunStoreSpecs
{
    [Fact]
    public async Task LoadAsync_MigratedFailedRun_RerunPersistsFreshStageAttempt()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = CreateLegacyExhaustedRecoveryRun();

        await using (var db = factory.CreateDbContext())
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = run.Id,
                State = ToLegacyRecoveryState(run),
            });
            await db.SaveChangesAsync();
        }

        await using (var upgradeDb = factory.CreateDbContext())
        {
            await WorkflowRunStateDataUpgrader.UpgradeAsync(
                upgradeDb,
                backup: static (_, _) => Task.FromResult("test-backup"));
        }

        var loaded = await store.LoadAsync(run.Id);
        Assert.NotNull(loaded);

        var events = loaded!.Rerun(DateTimeOffset.UnixEpoch);
        Assert.Collection(events,
            eventItem => Assert.IsType<WorkflowRunResumed>(eventItem.Value),
            eventItem => Assert.Equal("check", Assert.IsType<StageStarted>(eventItem.Value).Stage));
        Assert.Equal(WorkflowRunStatus.Pending, loaded.Status);
        Assert.Null(loaded.Failure);
        Assert.Equal(2, loaded.CurrentStage().Attempt);
        Assert.Empty(loaded.CurrentStage().Tasks);

        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync(run.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(WorkflowRunStatus.Pending, reloaded!.Status);
        Assert.Null(reloaded.Failure);
        Assert.Equal(2, reloaded.CurrentStage().Attempt);
        Assert.Empty(reloaded.CurrentStage().Tasks);
    }
}
