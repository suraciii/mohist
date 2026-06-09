using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class WorkflowRunStoreSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_IncrementsETagEvenAfterExternalMutation()
    {
        // After #2.1 the store uses a per-call DbContext, so the "stale OriginalValue
        // vs DB ETag" check the old scoped-context implementation did no longer
        // fires on its own. The store always reads the persisted ETag and
        // increments it. Real concurrency protection comes from the Orleans grain
        // single-thread model; ETag here is just a versioned audit trail.
        await using var connection = new SqliteConnection($"Data Source=mohist-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);

        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        var store = new WorkflowRunStore(factory);
        var run = WorkflowRun.Create("wf-etag", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build", Tasks: [
                new TaskDefinition("T-001", "Do work", "spec/task")
            ], Checks: [])
        ]));

        await store.SaveAsync(run);

        await using (var verify = await factory.CreateDbContextAsync())
        {
            var row = await verify.WorkflowRuns.FindAsync(run.Id);
            Assert.NotNull(row);
            Assert.Equal(1, verify.Entry(row!).Property<long>("ETag").CurrentValue);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.WorkflowRuns.FindAsync(run.Id);
            Assert.NotNull(row);
            db.Entry(row!).Property<long>("ETag").CurrentValue++;
            await db.SaveChangesAsync();
        }

        await store.SaveAsync(run);

        await using (var verify = await factory.CreateDbContextAsync())
        {
            var row = await verify.WorkflowRuns.FindAsync(run.Id);
            Assert.NotNull(row);
            // ETag was bumped to 2 by the external mutator, then the store
            // incremented it again to 3 on its own save. No concurrency
            // exception is thrown because the store reads the persisted ETag
            // per call instead of relying on a cached OriginalValue.
            Assert.Equal(3, verify.Entry(row!).Property<long>("ETag").CurrentValue);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithEvents_CommitsWorkflowRunAndEventsTogether()
    {
        await using var connection = new SqliteConnection($"Data Source=mohist-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);

        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        var run = WorkflowRun.Create("wf-events", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("plan", Tasks: [], Checks: [])
        ]));

        var store = new WorkflowRunStore(factory);
        await store.SaveAsync(run, [new WorkflowRunStarted(), new StageStarted("plan")]);

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.NotNull(await db.WorkflowRuns.FindAsync(run.Id));
            var events = await db.Events
                .Where(e => e.Source == "/workflow-runs/wf-events")
                .OrderBy(e => e.Id)
                .Select(e => new
                {
                    e.Id,
                    Type = EF.Property<string>(e, "Type"),
                    e.Data,
                })
                .ToListAsync();

            Assert.Collection(events,
                first =>
                {
                    Assert.Equal(1, first.Id);
                    Assert.Equal(nameof(WorkflowRunStarted), first.Type);
                },
                second =>
                {
                    Assert.Equal(2, second.Id);
                    Assert.Equal(nameof(StageStarted), second.Type);
                    Assert.Equal("plan", second.Data.GetProperty("stage").GetString());
                });
        }
    }

    private static InMemoryEventBus NewEventBus() => new(NullLogger<InMemoryEventBus>.Instance);
}
