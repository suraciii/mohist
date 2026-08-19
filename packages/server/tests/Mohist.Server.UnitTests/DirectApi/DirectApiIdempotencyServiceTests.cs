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

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
