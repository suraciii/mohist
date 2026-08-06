using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Thread reply delivery behavior split off from
/// <see cref="SlackChannelThreadIngressSpecs"/>: redelivery idempotency and
/// the addressing of post-launch replies into the originating thread. Shares
/// the same fixture, seed helpers and cached connection leases as the partial.
/// </summary>
public sealed partial class SlackChannelThreadIngressSpecs
{
    [Fact]
    public async Task Redelivered_root_mention_creates_no_duplicate_resources()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-L",
            messageTs: "1710000000.001200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> replay me");
        var firstSessionId = first.GetProperty("sessionId").GetString();
        List<string> beforeProjection;
        await using (var beforeScope = _fixture.Services.CreateAsyncScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            beforeProjection = await beforeDb.SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "C-channel-L")
                .OrderBy(row => row.Id)
                .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
                .ToListAsync();
        }

        var replay = await PostChannelAsync(connection, "C-channel-L",
            messageTs: "1710000000.001200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> replay me");

        Assert.Equal(firstSessionId, replay.GetProperty("sessionId").GetString());
        Assert.Equal("queued", replay.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var sessions = await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id
                && row.LabelSlackConversationId == "C-channel-L")
            .ToListAsync();
        Assert.Single(sessions);

        var inboxRows = await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-L"
                && row.SlackMessageIdentity.EndsWith("1710000000.001200"))
            .ToListAsync();
        Assert.Single(inboxRows);

        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var bindings = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId,
            "C-channel-L", "1710000000.001200");
        Assert.Single(bindings);
        var afterProjection = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "C-channel-L")
            .OrderBy(row => row.Id)
            .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
            .ToListAsync();
        Assert.Equal(beforeProjection, afterProjection);
        Assert.DoesNotContain(afterProjection, row => row.Contains("xoxb-", StringComparison.Ordinal));
    }

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
