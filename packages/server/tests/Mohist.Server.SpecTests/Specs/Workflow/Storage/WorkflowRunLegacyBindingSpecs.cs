using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class WorkflowRunLegacyBindingSpecs
{
    [Fact]
    public async Task LoadAsync_LegacyWorkflowProfileAnnotationRestoresRunBinding()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = new WorkflowRunStore(
            factory,
            new EventStore(factory, NullLogger<EventStore>.Instance),
            new NullEventDispatchGrainFactory(),
            NullLogger<WorkflowRunStore>.Instance, new Mohist.Server.Infrastructure.BackgroundTaskLauncher());

        await using (var db = factory.CreateDbContext())
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_legacy_profile_binding",
                State = """
                    {"id":"wr_legacy_profile_binding","metadata":{"createdAt":"1970-01-01T00:00:00+00:00","annotations":{"workflowProfileId":"legacy-profile"}},"status":"Failed","stages":[]}
                    """,
            });
            await db.SaveChangesAsync();
        }

        await using (var upgradeDb = factory.CreateDbContext())
        {
            await WorkflowRunStateDataUpgrader.UpgradeAsync(
                upgradeDb,
                backup: static (_, _) => Task.FromResult("test-backup"));
        }

        var loaded = await store.LoadAsync("wr_legacy_profile_binding");

        Assert.NotNull(loaded);
        Assert.Equal("legacy-profile", loaded!.WorkflowProfileId);
    }
}
