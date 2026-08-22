using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackChannelThreadIngressSpecs
{
    [Fact]
    public async Task Unavailable_channel_root_gets_a_durable_nudge_before_execution_side_effects()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Unhealthy)
                    .SetProperty(row => row.HealthReason, "Slack service is offline."));
        }

        const string conversationId = "C-channel-unavailable";
        const string rootTs = "1710000000.000170";
        var result = await PostChannelAsync(
            connection,
            conversationId,
            rootTs,
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> please retry this");

        Assert.Equal("connection_unavailable", result.GetProperty("kind").GetString());
        Assert.Equal(SlackIngressResponseOwners.Server, result.GetProperty("responseOwner").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var nudge = Assert.Single(
            await dbVerify.SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id
                    && row.ConversationId == conversationId)
                .ToListAsync(),
            row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal));
        Assert.Equal(rootTs, nudge.ThreadTs);
        Assert.Empty(await dbVerify.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == conversationId)
            .ToListAsync());
        Assert.Empty(await dbVerify.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
        Assert.Empty(await dbVerify.AgentJobs
            .Where(row => row.ProjectId == connection.ProjectId)
            .ToListAsync());
    }

    [Fact]
    public async Task Bound_thread_followup_bypasses_readiness_nudge()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-channel-bound-followup";
        const string rootTs = "1710000000.000180";
        var root = await PostChannelAsync(
            connection,
            conversationId,
            rootTs,
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> establish a session");
        var sessionId = root.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        await SetAgentConfigAsync(connection, null);
        var followup = await PostChannelAsync(
            connection,
            conversationId,
            "1710000000.000181",
            threadTs: rootTs,
            mentions: Array.Empty<string>(),
            text: "ordinary follow-up");

        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.Equal(sessionId, followup.GetProperty("sessionId").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var outboxRows = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.DispatchRef != null)
            .ToListAsync();
        Assert.DoesNotContain(outboxRows,
            row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_blocked_channel_root_redeliveries_create_one_server_owned_nudge()
    {
        var connection = await CreateConnectionAsync();
        await SetAgentConfigAsync(connection, null);
        const string conversationId = "C-channel-concurrent-setup";
        const string rootTs = "1710000000.000190";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => PostChannelAttemptAsync(
                connection,
                conversationId,
                rootTs,
                threadTs: null,
                mentions: new[] { connection.BotUserId },
                text: "<@U123> do this")));

        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal(SlackIngressResponseOwners.Server, response.Data.GetProperty("responseOwner").GetString());
        });

        await using var verify = _fixture.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(
            await db.SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id
                    && row.ConversationId == conversationId)
                .ToListAsync(),
            row => row.DispatchRef!.StartsWith("slack-admission-nudge:", StringComparison.Ordinal));
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == conversationId)
            .ToListAsync());
    }

    private async Task<(HttpStatusCode Status, JsonElement Data)> PostChannelAttemptAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.GetProperty("data").Clone());
    }
}
