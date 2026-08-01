using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Storage;

public class DispatchSnapshotStoreTests
{
    private static WorkDispatch BuildDispatch(string workflowRunId, string workId, string uses = "spec/task") =>
        new WorkDispatch(
            WorkflowRunId: workflowRunId,
            WorkId: workId,
            Uses: uses,
            With: """{"prompt":"hello"}""");

    private static async Task<Harness> CreateHarnessAsync()
    {
        var connectionString = $"Data Source=dispatch-snapshot-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        await using (var db = new MohistDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        var factory = new TestDbContextFactory(options);
        var store = new DispatchSnapshotStore(factory, NullLogger<DispatchSnapshotStore>.Instance);
        return new Harness(store, factory, keeper);
    }

    [Fact]
    public async Task SaveFirstAsync_InsertsRowAndReturnsCallerPayload()
    {
        await using var harness = await CreateHarnessAsync();
        var dispatch = BuildDispatch("wr_a", "task-1.1");

        var stored = await harness.Store.SaveFirstAsync("wr_a", "task-1.1", dispatch);

        Assert.Equal(dispatch, stored);
        var loaded = await harness.Store.LoadAsync("wr_a", "task-1.1");
        Assert.Equal(dispatch, loaded);
    }

    [Fact]
    public async Task SaveFirstAsync_OnSecondCallForSameAttempt_ReturnsFirstStoredUnchanged()
    {
        await using var harness = await CreateHarnessAsync();
        var first = BuildDispatch("wr_b", "task-1.1", uses: "spec/first");
        var second = BuildDispatch("wr_b", "task-1.1", uses: "spec/second");

        await harness.Store.SaveFirstAsync("wr_b", "task-1.1", first);
        var result = await harness.Store.SaveFirstAsync("wr_b", "task-1.1", second);

        Assert.Equal(first, result);
        Assert.NotEqual(second, result);
        var loaded = await harness.Store.LoadAsync("wr_b", "task-1.1");
        Assert.Equal(first, loaded);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRowThatExists()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Store.SaveFirstAsync("wr_c", "task-1.1", BuildDispatch("wr_c", "task-1.1"));

        await harness.Store.DeleteAsync("wr_c", "task-1.1");

        Assert.Null(await harness.Store.LoadAsync("wr_c", "task-1.1"));
    }

    [Fact]
    public async Task DeleteAsync_OnMissingRow_IsNoop()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Store.DeleteAsync("wr_missing", "task-1.1");

        Assert.Null(await harness.Store.LoadAsync("wr_missing", "task-1.1"));
    }

    [Fact]
    public async Task DeleteForRunAsync_RemovesAllSnapshotsForRun()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Store.SaveFirstAsync("wr_d", "task-1.1", BuildDispatch("wr_d", "task-1.1"));
        await harness.Store.SaveFirstAsync("wr_d", "task-1.2", BuildDispatch("wr_d", "task-1.2"));
        await harness.Store.SaveFirstAsync("wr_e", "task-1.1", BuildDispatch("wr_e", "task-1.1"));

        await harness.Store.DeleteForRunAsync("wr_d");

        Assert.Null(await harness.Store.LoadAsync("wr_d", "task-1.1"));
        Assert.Null(await harness.Store.LoadAsync("wr_d", "task-1.2"));
        Assert.NotNull(await harness.Store.LoadAsync("wr_e", "task-1.1"));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullForUnknownRun()
    {
        await using var harness = await CreateHarnessAsync();

        Assert.Null(await harness.Store.LoadAsync("wr_unknown", "task-1.1"));
    }

    [Fact]
    public async Task LoadAsync_EmptyKeysReturnNull()
    {
        await using var harness = await CreateHarnessAsync();

        Assert.Null(await harness.Store.LoadAsync("", "task-1.1"));
        Assert.Null(await harness.Store.LoadAsync("wr_x", ""));
    }

    private sealed record Harness(
        DispatchSnapshotStore Store,
        IDbContextFactory<MohistDbContext> Factory,
        SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
