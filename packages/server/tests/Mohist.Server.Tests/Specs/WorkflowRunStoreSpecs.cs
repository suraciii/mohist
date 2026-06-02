using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowRunStoreSpecs
{
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
        var store = new WorkflowRunStore(storeDb);
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
}
