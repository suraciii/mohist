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
    public async Task SaveAsync_WhenPersistedETagChanged_RejectsStaleWrite()
    {
        await using var connection = new SqliteConnection($"Data Source=mohist-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new PooledDbContextFactory<MohistDbContext>(options);

        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        await using var storeDb = await factory.CreateDbContextAsync();
        var store = new WorkflowRunStore(storeDb, NewEventBus());
        var run = WorkflowRun.Create("wf-etag", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build", Tasks: [
                new TaskDefinition("T-001", "Do work", "spec/task")
            ], Checks: [])
        ]));

        await store.SaveAsync(run);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.WorkflowRuns.FindAsync(run.Id);
            Assert.NotNull(row);
            db.Entry(row).Property<long>("ETag").CurrentValue++;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => store.SaveAsync(run));
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

        await using (var storeDb = await factory.CreateDbContextAsync())
        {
            var store = new WorkflowRunStore(storeDb, NewEventBus());
            await store.SaveAsync(run, [new WorkflowRunStarted(), new StageStarted("plan")]);
        }

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
