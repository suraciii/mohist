using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using TestSqliteDatabase = Mohist.Server.Tests.Support.TestSqliteDatabase;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Trait("level", "L0")]
public sealed class AgentRetryOperationStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
    private AgentRetryOperationStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _store = new AgentRetryOperationStore(
            new TestDbContextFactory(_database.Options),
            _time);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ClaimOrCreate_ReplaysByIdempotencyAndSessionTurn()
    {
        var firstProvenance = new AgentSessionInputProvenance(
            "slack", "workspace", "conversation", "thread-1", "member", "message-1", "connection", "thread-1");
        var first = await _store.ClaimOrCreateAsync(
            "project", "failed-session", "failed-turn", "click-1",
            AgentRetryOperationKind.Root, "new-session", "new-input", "new-turn", firstProvenance);
        var sameKey = await _store.ClaimOrCreateAsync(
            "project", "failed-session", "failed-turn", "click-1",
            AgentRetryOperationKind.Root, "other-session", "other-input", "other-turn",
            firstProvenance with { MessageId = "message-2" });
        var differentKey = await _store.ClaimOrCreateAsync(
            "project", "failed-session", "failed-turn", "click-2",
            AgentRetryOperationKind.Root, "other-session", "other-input", "other-turn",
            firstProvenance with { MessageId = "message-3" });

        Assert.False(first.AlreadyExists);
        Assert.True(sameKey.AlreadyExists);
        Assert.True(differentKey.AlreadyExists);
        Assert.Equal(first.Operation.OperationId, sameKey.Operation.OperationId);
        Assert.Equal(first.Operation.OperationId, differentKey.Operation.OperationId);
        Assert.Equal("new-session", differentKey.Operation.PreAllocatedSessionId);
        Assert.Equal(firstProvenance, differentKey.Operation.ReplyProvenance);

        await using var db = new MohistDbContext(_database.Options);
        Assert.Equal(1, await db.AgentRetryOperations.CountAsync());
        Assert.Equal("pending", await db.AgentRetryOperations.Select(row => row.State).SingleAsync());
    }

    [Fact]
    public async Task FinishedCleanupDeletesOnlyExpiredFinishedRows()
    {
        var old = await _store.ClaimOrCreateAsync(
            "project", "session-old", "turn-old", "old",
            AgentRetryOperationKind.Root, "s-old", "i-old", "t-old");
        await _store.MarkFinishedAsync(old.Operation.OperationId, "accepted", "done");

        var pending = await _store.ClaimOrCreateAsync(
            "project", "session-pending", "turn-pending", "pending",
            AgentRetryOperationKind.Root, "s-pending", "i-pending", "t-pending");
        _time.Advance(TimeSpan.FromHours(25));

        var removed = await _store.DeleteFinishedBeforeAsync(_time.GetUtcNow().UtcDateTime.AddHours(-24));

        Assert.Equal(1, removed);
        Assert.Null(await _store.GetAsync("project", old.Operation.OperationId));
        Assert.NotNull(await _store.GetAsync("project", pending.Operation.OperationId));
    }
}
