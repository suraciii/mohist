using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerToolExecutionFenceSpecs
{
    [Fact]
    public async Task JobKey_is_acquired_once_and_survives_store_reload()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        var factory = new TestDbContextFactory(database.Options);
        var first = new SlackManagerToolExecutionFenceStore(factory, time);
        var second = new SlackManagerToolExecutionFenceStore(factory, time);

        Assert.True(await first.TryAcquireAsync("delivery-job-1", "manager-session-1"));
        Assert.False(await second.TryAcquireAsync("delivery-job-1", "manager-session-1"));

        await first.MarkCompletedAsync("delivery-job-1");

        await using var db = factory.CreateDbContext();
        var row = await db.SlackManagerToolExecutionFences.SingleAsync();
        Assert.Equal("delivery-job-1", row.JobKey);
        Assert.Equal("manager-session-1", row.SessionId);
        Assert.Equal(SlackManagerToolExecutionFenceStates.Completed, row.State);
        Assert.NotNull(row.CompletedAt);
    }
}
