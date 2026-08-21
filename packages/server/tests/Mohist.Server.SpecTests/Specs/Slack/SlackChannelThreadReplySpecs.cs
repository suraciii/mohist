using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Thread reply delivery behavior split off from
/// <see cref="SlackChannelThreadIngressSpecs"/>: the addressing of post-launch
/// replies into the originating thread. Shares
/// the same fixture, seed helpers and cached connection leases as the partial.
/// </summary>
public sealed partial class SlackChannelThreadIngressSpecs
{
    [Fact]
    public async Task Threaded_launch_post_replies_are_addressed_into_thread()
    {
        var connection = await CreateConnectionAsync();
        await PostChannelAsync(connection, "C-channel-M",
            messageTs: "1710000000.001300",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> post into thread");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var received = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-M"
                && row.ThreadTs == "1710000000.001300"
                && row.DispatchRef == SlackStatusProjection.DispatchRef(
                    new SlackMessageIdentity("T123", "C-channel-M", "1710000000.001300"), "received"))
            .Select(row => row.PayloadJson)
            .FirstOrDefaultAsync();
        Assert.NotNull(received);
        Assert.Equal(SlackDeliveryOperations.ReactionAdd, SlackDeliveryPayload.Parse(received!).Operation);
    }
}
