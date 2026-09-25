using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.Idempotency;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.DirectApi;

[Trait("level", "L0")]
public sealed class IdempotencyFenceTests
{
    [Fact]
    public async Task PendingStop_FencesOtherScopesUntilCompletion()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new IdempotencyFence(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));

        var first = await service.GetOrCreateAsync(
            IdempotencyCommands.Stop,
            "turn-1|caller-a|key-a",
            "caller-a",
            "fingerprint",
            "turn-1",
            "outcome-a");
        var fenced = await service.GetOrCreateAsync(
            IdempotencyCommands.Stop,
            "turn-1|caller-b|key-b",
            "caller-b",
            "fingerprint",
            "turn-1",
            "outcome-b");

        Assert.True(first.Created);
        Assert.True(fenced.StopOutcomeUnknown);
        // The losing scope must be fenced to caller A's pending row, so it
        // reads that row's outcome instead of creating a second mapping.
        Assert.Equal(first.Outcome, fenced.Outcome);

        await service.CompleteAsync(
            IdempotencyCommands.Stop,
            "turn-1|caller-a|key-a",
            IdempotencyMappingStates.Completed,
            "completed-a");
        var second = await service.GetOrCreateAsync(
            IdempotencyCommands.Stop,
            "turn-1|caller-b|key-b",
            "caller-b",
            "fingerprint",
            "turn-1",
            "outcome-b");

        Assert.True(second.Created);
        Assert.False(second.StopOutcomeUnknown);
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.IdempotencyMappings.CountAsync());
    }

    [Fact]
    public async Task FreezeCompletedOutcome_ReplacesOnlyTheExpectedVersion()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new IdempotencyFence(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));
        const string scopeKey = "session-1|key-1";

        await service.GetOrCreateAsync(
            IdempotencyCommands.Followup,
            scopeKey,
            "caller-a",
            "fingerprint",
            turnId: null,
            "pending");
        await service.CompleteAsync(
            IdempotencyCommands.Followup,
            scopeKey,
            IdempotencyMappingStates.Completed,
            "completed");

        var frozen = await service.FreezeCompletedOutcomeAsync(
            IdempotencyCommands.Followup,
            scopeKey,
            "completed",
            "frozen");
        var staleWriter = await service.FreezeCompletedOutcomeAsync(
            IdempotencyCommands.Followup,
            scopeKey,
            "completed",
            "stale");

        Assert.Equal("frozen", frozen.Outcome);
        Assert.Equal("frozen", staleWriter.Outcome);
    }

    [Fact]
    public async Task Completion_KeepsTheFirstRecordedDecision()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new IdempotencyFence(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));
        const string command = IdempotencyCommands.WorkflowControl;
        const string scope = "wr_1|caller-a|key-a";
        await service.GetOrCreateAsync(command, scope, "caller-a", "fingerprint", null, null);

        await service.CompleteAsync(command, scope, IdempotencyMappingStates.Completed, "first");
        var late = await service.CompleteAsync(command, scope, IdempotencyMappingStates.Rejected, "second");

        // Two attempts can race past the pending lease. The first decision is
        // the one the key records, so every later replay observes it.
        Assert.Equal(IdempotencyMappingStates.Completed, late.State);
        Assert.Equal("first", late.Outcome);
    }

    [Fact]
    public async Task Abandon_RemovesOnlyTheRowTheAttemptClaimed()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new IdempotencyFence(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));
        const string command = IdempotencyCommands.WorkflowControl;
        const string scope = "wr_1|caller-a|key-a";
        var creator = await service.GetOrCreateAsync(command, scope, "caller-a", "fingerprint", null, null);
        Assert.True(creator.Created);

        // An attempt that took the fence over after the lease must leave the
        // creator's row alone: the creator may still be executing and about to
        // record the decision for this key.
        await service.AbandonPendingAsync(command, scope, owned: false);
        var surviving = await service.FindAsync(command, scope);
        Assert.NotNull(surviving);
        Assert.Equal(IdempotencyMappingStates.Pending, surviving!.State);

        await service.AbandonPendingAsync(command, scope, owned: true);
        Assert.Null(await service.FindAsync(command, scope));
    }

    [Fact]
    public async Task PruneFinishedBefore_RemovesExpiredDecisionsAndKeepsLiveFences()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new IdempotencyFence(factory, clock);
        const string command = IdempotencyCommands.WorkflowControl;

        await service.GetOrCreateAsync(command, "expired", "caller-a", "fingerprint", null, null);
        await service.CompleteAsync(command, "expired", IdempotencyMappingStates.Completed, "recorded");

        clock.Advance(TimeSpan.FromDays(6));
        await service.GetOrCreateAsync(command, "recent", "caller-a", "fingerprint", null, null);
        await service.CompleteAsync(command, "recent", IdempotencyMappingStates.Completed, "recorded");
        await service.GetOrCreateAsync(IdempotencyCommands.Stop, "pending", "caller-a", "fingerprint", "turn-1", null);

        clock.Advance(TimeSpan.FromDays(2));
        var removed = await service.PruneFinishedBeforeAsync(clock.GetUtcNow() - TimeSpan.FromDays(7));

        // Only the decision that aged out of the window is dropped. A pending
        // row stays because the next attempt may still be executing it, and a
        // decision inside the window stays because the key still replays it.
        Assert.Equal(1, removed);
        Assert.Null(await service.FindAsync(command, "expired"));
        Assert.Equal("recorded", (await service.FindAsync(command, "recent"))!.Outcome);
        Assert.Equal(
            IdempotencyMappingStates.Pending,
            (await service.FindAsync(IdempotencyCommands.Stop, "pending"))!.State);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
