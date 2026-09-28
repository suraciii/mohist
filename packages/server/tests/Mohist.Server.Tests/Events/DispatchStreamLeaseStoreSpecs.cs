using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Events;

/// <summary>
/// Real-storage coverage for <see cref="DispatchStreamLeaseStore"/>'s parked
/// streams observation. The store runs on a real migrated SQLite database so
/// the spec exercises the SQL the production dispatcher issues, not a fake.
/// Mixed offsets pin instant comparison against a raw-TEXT implementation:
/// SQLite stores DateTimeOffset as wall-clock text, whose order is not the
/// instant order across offsets.
/// </summary>
[Trait("level", "L0")]
public class DispatchStreamLeaseStoreSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private TestSqliteDatabase _database = null!;
    private DispatchStreamLeaseStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _store = new DispatchStreamLeaseStore(new TestDbContextFactory(_database.Options));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CountParkedAsync_ParkedStream_CountsOne()
    {
        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_parked", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_parked",
            "owner_a",
            attempts: 1,
            nextAttemptAt: Now.AddSeconds(1),
            lastError: "transient",
            now: Now));

        Assert.Equal(1, await store.CountParkedAsync(Now));
    }

    [Fact]
    public async Task CountParkedAsync_ClaimedButNotParked_CountsZero()
    {
        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_claimed", "owner_a", Now, TimeSpan.FromSeconds(30)));

        Assert.Equal(0, await store.CountParkedAsync(Now));
    }

    [Fact]
    public async Task CountParkedAsync_ElapsedPark_CountsZero()
    {
        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_elapsed", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_elapsed",
            "owner_a",
            attempts: 1,
            nextAttemptAt: Now.AddSeconds(-1),
            lastError: "transient",
            now: Now));

        Assert.Equal(0, await store.CountParkedAsync(Now));
    }

    [Fact]
    public async Task CountParkedAsync_TwoFutureParks_CountsTwo()
    {
        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_first", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_first",
            "owner_a",
            attempts: 1,
            nextAttemptAt: Now.AddSeconds(1),
            lastError: "transient",
            now: Now));
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_second", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_second",
            "owner_a",
            attempts: 1,
            nextAttemptAt: Now.AddSeconds(5),
            lastError: "transient",
            now: Now));

        Assert.Equal(2, await store.CountParkedAsync(Now));
    }

    [Fact]
    public async Task CountParkedAsync_MixedOffsets_ComparesInstantsNotWallClockText()
    {
        // A past instant written with a +02:00 offset has a later wall-clock
        // text than Now; a future instant written with a -02:00 offset has an
        // earlier wall-clock text. A raw TEXT comparison inverts both.
        var pastInstantPositiveOffset = new DateTimeOffset(2026, 7, 1, 13, 59, 0, TimeSpan.FromHours(2));
        var futureInstantNegativeOffset = new DateTimeOffset(2026, 7, 1, 10, 1, 0, TimeSpan.FromHours(-2));
        Assert.True(pastInstantPositiveOffset < Now);
        Assert.True(futureInstantNegativeOffset > Now);

        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_past_offset", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_past_offset",
            "owner_a",
            attempts: 1,
            nextAttemptAt: pastInstantPositiveOffset,
            lastError: "transient",
            now: Now));
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_future_offset", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_future_offset",
            "owner_a",
            attempts: 1,
            nextAttemptAt: futureInstantNegativeOffset,
            lastError: "transient",
            now: Now));

        Assert.Equal(1, await store.CountParkedAsync(Now));
    }

    [Fact]
    public async Task ClaimAsync_MixedOffsets_UsesInstantDueAndLeaseComparisons()
    {
        // The parked timestamps deliberately invert wall-clock ordering:
        // the past value has a later +02:00 clock, and the future value has
        // an earlier -02:00 clock. Lexical TEXT comparisons would make both
        // claim decisions backwards.
        var pastInstantPositiveOffset = new DateTimeOffset(2026, 7, 1, 13, 59, 0, TimeSpan.FromHours(2));
        var futureInstantNegativeOffset = new DateTimeOffset(2026, 7, 1, 10, 1, 0, TimeSpan.FromHours(-2));
        Assert.True(pastInstantPositiveOffset < Now);
        Assert.True(futureInstantNegativeOffset > Now);

        IDispatchStreamLeaseStore store = _store;
        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_claim_past_offset", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_claim_past_offset",
            "owner_a",
            attempts: 2,
            nextAttemptAt: pastInstantPositiveOffset,
            lastError: "transient",
            now: Now));

        Assert.Equal(2, await store.ClaimAsync(
            "Issue",
            "/mohist/issues/issue_claim_past_offset",
            "owner_b",
            Now,
            TimeSpan.FromSeconds(30)));

        Assert.Equal(0, await store.ClaimAsync(
            "Issue", "/mohist/issues/issue_claim_future_offset", "owner_a", Now, TimeSpan.FromSeconds(30)));
        Assert.True(await store.ParkAsync(
            "Issue",
            "/mohist/issues/issue_claim_future_offset",
            "owner_a",
            attempts: 3,
            nextAttemptAt: futureInstantNegativeOffset,
            lastError: "transient",
            now: Now));

        Assert.Null(await store.ClaimAsync(
            "Issue",
            "/mohist/issues/issue_claim_future_offset",
            "owner_b",
            Now,
            TimeSpan.FromSeconds(30)));
    }
}
