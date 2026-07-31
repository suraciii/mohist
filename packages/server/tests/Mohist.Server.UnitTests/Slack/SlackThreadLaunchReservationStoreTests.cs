using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackThreadLaunchReservationStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReserveAsync_AllowsOnlyOneLaunchAndRecognizesRedelivery()
    {
        await using var harness = CreateHarness();

        var first = await harness.Store.ReserveAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "1710.0002", "U_OWNER");
        var concurrent = await harness.Store.ReserveAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "1710.0003", "U_OWNER");

        Assert.Equal(SlackThreadLaunchReservationKind.Owner, first.Kind);
        Assert.Equal(SlackThreadLaunchReservationKind.InProgress, concurrent.Kind);

        Assert.Equal("session-A", await harness.Store.BindSessionAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "session-A"));

        var otherMessage = await harness.Store.ReserveAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "1710.0003", "U_OWNER");
        var redelivery = await harness.Store.ReserveAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "1710.0002", "U_OWNER");

        Assert.Equal(SlackThreadLaunchReservationKind.Bound, otherMessage.Kind);
        Assert.Equal("session-A", otherMessage.SessionId);
        Assert.Equal(SlackThreadLaunchReservationKind.Owner, redelivery.Kind);
        Assert.Equal("session-A", redelivery.SessionId);
    }

    [Fact]
    public async Task DeleteForConnectionAsync_RemovesReservations()
    {
        await using var harness = CreateHarness();
        await harness.Store.ReserveAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "1710.0002", "U_OWNER");

        var affected = await harness.Store.DeleteForConnectionAsync(harness.ProjectId, harness.ConnectionId);

        Assert.Equal(1, affected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Store.BindSessionAsync(
            harness.ProjectId, "T123", harness.ConnectionId, "C-channel", "1710.0001", "session-A"));
    }

    private static Harness CreateHarness()
    {
        var keeper = new SqliteConnection($"Data Source=thread-launch-reservation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keeper.Open();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(keeper)
            .Options;
        using (var db = new MohistDbContext(options))
            db.Database.EnsureCreated();

        return new Harness(
            new SlackThreadLaunchReservationStore(new TestDbContextFactory(options), new FakeTimeProvider(FixedNow)),
            $"project_{Guid.NewGuid():N}",
            $"connection_{Guid.NewGuid():N}",
            keeper);
    }

    private sealed record Harness(
        SlackThreadLaunchReservationStore Store,
        string ProjectId,
        string ConnectionId,
        SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
