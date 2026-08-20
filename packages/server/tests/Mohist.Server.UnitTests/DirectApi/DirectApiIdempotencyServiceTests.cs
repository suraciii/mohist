using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.DirectApi;

public sealed class DirectApiIdempotencyServiceTests
{
    [Fact]
    public async Task PendingStop_FencesOtherScopesUntilCompletion()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new DirectApiIdempotencyService(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));

        var first = await service.GetOrCreateAsync(
            DirectApiCommands.Stop,
            "turn-1|caller-a|key-a",
            "caller-a",
            "fingerprint",
            "turn-1",
            "outcome-a");
        var fenced = await service.GetOrCreateAsync(
            DirectApiCommands.Stop,
            "turn-1|caller-b|key-b",
            "caller-b",
            "fingerprint",
            "turn-1",
            "outcome-b");

        Assert.True(first.Created);
        Assert.True(fenced.StopOutcomeUnknown);
        Assert.Equal(first.Mapping.ScopeKey, fenced.Mapping.ScopeKey);

        await service.CompleteAsync(
            DirectApiCommands.Stop,
            first.Mapping.ScopeKey,
            DirectApiMappingStates.Completed,
            "completed-a");
        var second = await service.GetOrCreateAsync(
            DirectApiCommands.Stop,
            "turn-1|caller-b|key-b",
            "caller-b",
            "fingerprint",
            "turn-1",
            "outcome-b");

        Assert.True(second.Created);
        Assert.False(second.StopOutcomeUnknown);
        await using var db = factory.CreateDbContext();
        Assert.Equal(2, await db.DirectApiIdempotencyMappings.CountAsync());
    }

    [Fact]
    public async Task FreezeCompletedOutcome_ReplacesOnlyTheExpectedVersion()
    {
        using var database = TestSqliteDatabase.CreateModelSchema();
        var factory = new TestDbContextFactory(database.Options);
        var service = new DirectApiIdempotencyService(
            factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero)));
        const string scopeKey = "session-1|key-1";

        await service.GetOrCreateAsync(
            DirectApiCommands.Followup,
            scopeKey,
            "caller-a",
            "fingerprint",
            turnId: null,
            "pending");
        await service.CompleteAsync(
            DirectApiCommands.Followup,
            scopeKey,
            DirectApiMappingStates.Completed,
            "completed");

        var frozen = await service.FreezeCompletedOutcomeAsync(
            DirectApiCommands.Followup,
            scopeKey,
            "completed",
            "frozen");
        var staleWriter = await service.FreezeCompletedOutcomeAsync(
            DirectApiCommands.Followup,
            scopeKey,
            "completed",
            "stale");

        Assert.Equal("frozen", frozen.Outcome);
        Assert.Equal("frozen", staleWriter.Outcome);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
