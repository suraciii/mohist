using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackChannelThreadIngressSpecs
{
    [Fact]
    public async Task Blocked_agent_root_and_first_unbound_thread_mention_nudge_without_work()
    {
        var connection = await CreateConnectionAsync();
        await SetAgentConfigAsync(connection, null);

        var root = await PostChannelAsync(
            connection,
            "C-channel-blocked",
            "1710000000.000900",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> blocked root task");
        Assert.Equal("agent_not_configured", root.GetProperty("kind").GetString());

        var thread = await PostChannelAsync(
            connection,
            "C-channel-blocked",
            "1710000000.000910",
            threadTs: "1710000000.000900",
            mentions: new[] { connection.BotUserId },
            text: "<@U123> blocked thread task");
        Assert.Equal("agent_not_configured", thread.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-blocked")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
        Assert.Empty(await db.AgentJobs
            .Where(row => row.ProjectId == connection.ProjectId)
            .ToListAsync());

        var nudges = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-blocked"
                && row.DispatchRef != null
                && EF.Functions.Like(row.DispatchRef, "slack-setup-nudge:%"))
            .ToListAsync();
        Assert.Equal(2, nudges.Count);
        Assert.Null(nudges.Single(row => row.DispatchRef!.EndsWith("1710000000.000900", StringComparison.Ordinal)).ThreadTs);
        Assert.Equal("1710000000.000900", nudges.Single(row => row.DispatchRef!.EndsWith("1710000000.000910", StringComparison.Ordinal)).ThreadTs);
        Assert.All(nudges, row =>
        {
            var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
            Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
            Assert.Contains("Agent is not ready", payload.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model-missing", row.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("mo agent edit", row.PayloadJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Backpressured_channel_returns_visible_rejection_without_accepting_work()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Degraded)
                    .SetProperty(row => row.HealthReason, SlackProviderBackpressureReasons.OutboxOverflow));
        }

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-backpressured",
            messageTs = "1710000000.000450",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123> do work",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("backpressured", data.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("reason").GetString()));

        await using var verify = _fixture.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await dbVerify.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-backpressured")
            .ToListAsync());
        Assert.Empty(await dbVerify.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
        var nudge = await dbVerify.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "C-channel-backpressured"
            && row.Kind == SlackOutboxKinds.UserAction
            && row.DispatchRef == $"slack-setup-nudge:{connection.Id}:T123/C-channel-backpressured/1710000000.000450");
        var nudgePayload = SlackDeliveryPayload.Parse(nudge.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, nudgePayload.Operation);
        Assert.Equal("T123/C-channel-backpressured/1710000000.000450", nudgePayload.ClientMessageId);
        Assert.Contains("Connection is not ready", nudgePayload.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SlackProviderBackpressureReasons.OutboxOverflow, nudge.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backpressured_dm_returns_visible_rejection_without_accepting_work()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Degraded)
                    .SetProperty(row => row.HealthReason, SlackProviderBackpressureReasons.InboxOverflow));
        }

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-channel-backpressured",
            messageTs = "1710000000.000455",
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "do work",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("backpressured", data.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("reason").GetString()));

        await using var verify = _fixture.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await dbVerify.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "D-channel-backpressured")
            .ToListAsync());
        Assert.Empty(await dbVerify.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
        var nudge = await dbVerify.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "D-channel-backpressured"
            && row.Kind == SlackOutboxKinds.UserAction
            && row.DispatchRef == $"slack-setup-nudge:{connection.Id}:T123/D-channel-backpressured/1710000000.000455");
        var nudgePayload = SlackDeliveryPayload.Parse(nudge.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, nudgePayload.Operation);
        Assert.Equal("T123/D-channel-backpressured/1710000000.000455", nudgePayload.ClientMessageId);
        Assert.Contains("Connection is not ready", nudgePayload.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SlackProviderBackpressureReasons.InboxOverflow, nudge.PayloadJson, StringComparison.Ordinal);
    }
}
