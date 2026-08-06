using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackChannelThreadIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackChannelThreadIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Owner_root_mention_launches_work_and_binds_thread_to_session()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-A",
            messageTs = "1710000000.000100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123> summarize the bug",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var sessionId = data.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.Equal("1710000000.000100", data.GetProperty("threadRoot").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var stored = await threadMapping.GetSessionIdAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            "C-channel-A", "1710000000.000100");
        Assert.Equal(sessionId, stored);
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var session = await db.AgentSessions.SingleAsync(row => row.Id == sessionId);
        Assert.Equal("1710000000.000100", session.LabelSlackThreadTs);
        var input = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId!).GetInitialLaunchAsync();
        Assert.Equal(new AgentSessionInputProvenance(
            ProviderKind: "slack",
            WorkspaceId: connection.WorkspaceTeamId,
            ConversationId: "C-channel-A",
            ThreadId: "1710000000.000100",
            MemberId: "U_OWNER",
            MessageId: "1710000000.000100",
            ConnectionId: connection.Id), input!.Input!.Provenance);
        var inboxRow = await db.SlackProviderInboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "C-channel-A"
            && row.SlackMessageIdentity.EndsWith("1710000000.000100"));
        Assert.Equal("1710000000.000100", inboxRow.ThreadTs);
        var initialProgress = await db.SlackOutboxRows.SingleAsync(row =>
            row.ConnectionId == connection.Id
            && row.ConversationId == "C-channel-A"
            && row.ThreadTs == "1710000000.000100"
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.DispatchRef == SlackStatusProjection.DispatchRef(
                new SlackMessageIdentity("T123", "C-channel-A", "1710000000.000100"), "progress"));
        var initialProgressPayload = SlackDeliveryPayload.Parse(initialProgress.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, initialProgressPayload.Operation);
        Assert.Equal("Working...", initialProgressPayload.Text);
        Assert.DoesNotContain("xoxb-", initialProgress.PayloadJson, StringComparison.Ordinal);
        Assert.NotNull(data.GetProperty("jobKey").GetString());
    }

    [Fact]
    public async Task Root_redelivery_retries_when_inbox_route_has_no_session()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-channel-root-recovery";
        const string messageTs = "1710000000.000105";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<SlackProviderInboxStore>();
            await inbox.AcceptAsync(
                new SlackProviderInboxDraft(
                    connection.ProjectId,
                    connection.Id,
                    new SlackMessageIdentity(connection.WorkspaceTeamId, conversationId, messageTs),
                    "U_OWNER"),
                new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.LaunchThread));
        }

        var replay = await PostChannelAsync(
            connection,
            conversationId,
            messageTs,
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> recover root launch");

        Assert.False(string.IsNullOrWhiteSpace(replay.GetProperty("sessionId").GetString()));
        Assert.Equal("queued", replay.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Bare_root_mention_with_no_task_creates_no_resources_and_asks_for_task()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-A",
            messageTs = "1710000000.000110",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123|Mohist>",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Equal("Please send a task for the Agent to perform.",
            data.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        Assert.Null(await threadMapping.GetSessionIdAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            "C-channel-A", "1710000000.000110"));
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-A"
                && row.SlackMessageIdentity.EndsWith("1710000000.000110"))
            .ToListAsync());
    }

    [Fact]
    public async Task Followup_after_terminal_continues_bound_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-B",
            messageTs: "1710000000.000200",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(firstSessionId!)
            .MarkTurnTerminalAsync(
                first.GetProperty("turnId").GetString()!,
                AgentTurnStatus.Completed,
                null);

        var followup = await PostChannelAsync(connection, "C-channel-B",
            messageTs: "1710000000.000210",
            threadTs: "1710000000.000200",
            mentions: Array.Empty<string>(),
            text: "follow-up question");
        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task Followup_redelivery_rebuilds_a_missing_acknowledgement()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-replay-ack",
            messageTs: "1710000000.000250",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(first.GetProperty("sessionId").GetString()!)
            .MarkTurnTerminalAsync(
                first.GetProperty("turnId").GetString()!,
                AgentTurnStatus.Completed,
                null);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(first.GetProperty("sessionId").GetString()!)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                "runtime-channel-replay-ack", "/mohist-tests/slack-replay"));
        await PostChannelAsync(connection, "C-channel-replay-ack",
            messageTs: "1710000000.000260",
            threadTs: "1710000000.000250",
            mentions: Array.Empty<string>(),
            text: "follow-up question");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id
                    && row.DispatchRef == SlackStatusProjection.DispatchRef(
                        new SlackMessageIdentity("T123", "C-channel-replay-ack", "1710000000.000260"), "received"))
                .ExecuteDeleteAsync();
        }

        var replay = await PostChannelAsync(connection, "C-channel-replay-ack",
            messageTs: "1710000000.000260",
            threadTs: "1710000000.000250",
            mentions: Array.Empty<string>(),
            text: "follow-up question");
        Assert.Equal("already_accepted", replay.GetProperty("kind").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var outbox = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.True(await outbox.SlackOutboxRows.AnyAsync(row => row.ConnectionId == connection.Id
            && row.DispatchRef == SlackStatusProjection.DispatchRef(
                new SlackMessageIdentity("T123", "C-channel-replay-ack", "1710000000.000260"), "received")));
    }

    [Fact]
    public async Task Followup_redelivery_retries_when_inbox_was_accepted_before_followup()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-channel-followup-recovery";
        const string rootTs = "1710000000.000270";
        const string followupTs = "1710000000.000271";
        var first = await PostChannelAsync(
            connection,
            conversationId,
            rootTs,
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");
        var sessionId = first.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .MarkTurnTerminalAsync(
                first.GetProperty("turnId").GetString()!,
                AgentTurnStatus.Completed,
                null);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                "runtime-channel-followup-recovery", "/mohist-tests/slack-recovery"));

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<SlackProviderInboxStore>();
            await inbox.AcceptAsync(
                new SlackProviderInboxDraft(
                    connection.ProjectId,
                    connection.Id,
                    new SlackMessageIdentity(connection.WorkspaceTeamId, conversationId, followupTs),
                    "U_OWNER"),
                new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.FollowupThread, sessionId));
        }

        var replay = await PostChannelAsync(
            connection,
            conversationId,
            followupTs,
            threadTs: rootTs,
            mentions: Array.Empty<string>(),
            text: "recover follow-up");

        Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
        Assert.True(replay.GetProperty("followup").GetBoolean());
        Assert.NotEqual("already_accepted", replay.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(replay.GetProperty("inputId").GetString()));
    }

    [Fact]
    public async Task Empty_bound_thread_reply_is_rejected_before_inbox_acceptance()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-channel-empty-followup";
        const string rootTs = "1710000000.000280";
        const string followupTs = "1710000000.000281";
        await PostChannelAsync(
            connection,
            conversationId,
            rootTs,
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");

        var rejected = await PostChannelAsync(
            connection,
            conversationId,
            followupTs,
            threadTs: rootTs,
            mentions: new[] { connection.BotUserId },
            text: "<@U123>");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());
        Assert.Equal("Please send a task for the Agent to perform.", rejected.GetProperty("reason").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.SlackMessageIdentity.EndsWith(followupTs))
            .ToListAsync());
    }

    [Fact]
    public async Task Followup_during_execution_acknowledges_queued()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-C",
            messageTs: "1710000000.000300",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> long task");
        var firstSessionId = first.GetProperty("sessionId").GetString();

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(firstSessionId!)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                "runtime-channel-C", "/mohist-tests/slack-channel-C"));

        var followup = await PostChannelAsync(connection, "C-channel-C",
            messageTs: "1710000000.000310",
            threadTs: "1710000000.000300",
            mentions: Array.Empty<string>(),
            text: "more details");

        Assert.Equal(firstSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());

        await AssertWorkingProjectionAsync(connection, "C-channel-C", "1710000000.000310", firstSessionId!);
    }

    [Fact]
    public async Task Followup_turn_terminal_delivers_result_to_slack_thread()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-channel-followup-delivery";
        const string rootTs = "1710000000.000500";
        const string followupTs = "1710000000.000510";
        const string runtimeSessionId = "runtime-followup-delivery";

        var first = await PostChannelAsync(connection, conversationId,
            messageTs: rootTs, threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> initial task");
        var sessionId = first.GetProperty("sessionId").GetString()!;

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                runtimeSessionId, "/mohist-tests/slack-followup-delivery"));

        var followup = await PostChannelAsync(connection, conversationId,
            messageTs: followupTs, threadTs: rootTs,
            mentions: Array.Empty<string>(),
            text: "follow-up question");
        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.False(string.IsNullOrEmpty(followup.GetProperty("inputId").GetString()),
            $"Follow-up was not accepted; kind={followup.GetProperty("kind").GetString()}");

        var runtimeEvents = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
            {
                new AgentSessionRuntimeEventInput(
                    Type: "session.activity",
                    PayloadJson: "{\"activity\":\"idle\"}"),
            }, runtimeSessionId));
        Assert.NotEmpty(runtimeEvents);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var dispatchRefPrefix = $"agent-job:agent-session-followup:{sessionId}:";

        var deliveryDispatchRef = await TestWait.ForAsync(
            async () =>
            {
                var rows = await db.SlackOutboxRows.AsNoTracking()
                    .Where(row => row.ConnectionId == connection.Id
                        && row.ConversationId == conversationId
                        && row.Kind == SlackOutboxKinds.TerminalResult
                        && row.ThreadTs == rootTs)
                    .Select(row => row.DispatchRef)
                    .ToListAsync();
                return rows.FirstOrDefault(r => r is not null && r.StartsWith(dispatchRefPrefix, StringComparison.Ordinal));
            },
            value => value is not null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            $"follow-up terminal delivery outbox row for session {sessionId}",
            advance: async () =>
            {
                await _fixture.Grains.GetGrain<Mohist.Server.Events.Grains.IEventDispatcherGrain>(
                    Mohist.Server.Events.Grains.EventDispatcherGrain.Global)
                    .DispatchNowAsync();
            });

        Assert.NotNull(deliveryDispatchRef);
        Assert.StartsWith(dispatchRefPrefix, deliveryDispatchRef);
    }

    [Fact]
    public async Task Non_owner_mention_is_rejected_with_no_agent_resources()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-D",
            messageTs = "1710000000.000400",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = "<@U123> do something",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-D")
            .ToListAsync());
        Assert.Empty(await inbox.AgentSessions.Where(row => row.LabelConnectionId == connection.Id).ToListAsync());
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
        Assert.Empty(await dbVerify.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-backpressured")
            .ToListAsync());
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
        Assert.Empty(await dbVerify.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "D-channel-backpressured")
            .ToListAsync());
    }

    [Fact]
    public async Task Accepted_ingress_clears_offline_gap_after_reconnect()
    {
        var connection = await CreateConnectionAsync();
        var gapAt = _fixture.TimeProvider.GetUtcNow();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.ProjectId == connection.ProjectId && row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.OfflineGapAt, gapAt));
        }

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-gap-clear",
            messageTs = "1710000000.000600",
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "first message after reconnect",
        });
        response.EnsureSuccessStatusCode();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var reloaded = await verifyDb.AgentConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        Assert.Null(reloaded.OfflineGapAt);
    }

    [Fact]
    public async Task Bot_sender_is_acknowledged_and_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-E",
            messageTs = "1710000000.000500",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_BOT",
            senderKind = "bot",
            text = "<@U123> ignorable",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-E")
            .ToListAsync());
    }

    [Fact]
    public async Task Unknown_sender_is_acknowledged_and_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-E2",
            messageTs = "1710000000.000510",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = (string?)null,
            senderKind = "unknown",
            text = "<@U123> ignorable",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Plain_channel_message_with_no_mention_is_ignored_without_persisting_text()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-F",
            messageTs = "1710000000.000600",
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "no mention here",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-F")
            .ToListAsync());
    }

    [Fact]
    public async Task Unbound_thread_reply_without_mention_is_ignored()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-G",
            messageTs = "1710000000.000700",
            threadTs = "1710000000.000690",
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "just chatting",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-G")
            .ToListAsync());
    }

    [Fact]
    public async Task Another_connection_ignores_unmentioned_reply_to_bound_thread()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-H",
            messageTs: "1710000000.000800",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> do it");
        var sessionId = first.GetProperty("sessionId").GetString();

        var otherConnection = await CreateConnectionAsync("agent-other");
        var body = new
        {
            isDirectMessage = false,
            teamId = otherConnection.WorkspaceTeamId,
            conversationId = "C-channel-H",
            messageTs = "1710000000.000810",
            threadTs = "1710000000.000800",
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "follow-up not for you",
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(otherConnection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ignored", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await inbox.SlackProviderInboxRows
            .Where(row => row.ConnectionId == otherConnection.Id
                && row.ConversationId == "C-channel-H")
            .ToListAsync());
        Assert.NotNull(sessionId);
    }

    [Fact]
    public async Task Provenance_labels_distinct_workspaces_with_equal_thread_ts_do_not_share_bindings()
    {
        var connectionA = await CreateConnectionAsync("agent-A");
        var connectionB = await CreateConnectionAsync("agent-B");

        await PostChannelAsync(connectionA, "C-channel-I",
            messageTs: "1710000000.000900",
            threadTs: null,
            mentions: new[] { connectionA.BotUserId },
            text: "<@U123> from workspace A");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var fromA = await threadMapping.ListBindingsAsync(
            connectionA.ProjectId, connectionA.WorkspaceTeamId, "C-channel-I", "1710000000.000900");
        var fromB = await threadMapping.ListBindingsAsync(
            connectionB.ProjectId, connectionB.WorkspaceTeamId, "C-channel-I", "1710000000.000900");

        Assert.Single(fromA);
        Assert.Empty(fromB);
    }

    [Fact]
    public async Task Provenance_equal_root_ts_in_two_channels_stay_isolated()
    {
        var connection = await CreateConnectionAsync();
        await PostChannelAsync(connection, "C-channel-J1",
            messageTs: "1710000000.001000",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first channel");
        await PostChannelAsync(connection, "C-channel-J2",
            messageTs: "1710000000.001000",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> second channel");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var fromOne = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId, "C-channel-J1", "1710000000.001000");
        var fromTwo = await threadMapping.ListBindingsAsync(
            connection.ProjectId, connection.WorkspaceTeamId, "C-channel-J2", "1710000000.001000");

        Assert.Single(fromOne);
        Assert.Single(fromTwo);
        Assert.NotEqual(fromOne[0].SessionId, fromTwo[0].SessionId);
    }

    [Fact]
    public async Task Crash_window_repair_rebinds_thread_from_persisted_session()
    {
        var connection = await CreateConnectionAsync();
        var first = await PostChannelAsync(connection, "C-channel-K",
            messageTs: "1710000000.001100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> crash window");
        var originalSessionId = first.GetProperty("sessionId").GetString()!;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
            await threadMapping.DeleteForConnectionAsync(connection.ProjectId, connection.Id);
        }

        var followup = await PostChannelAsync(connection, "C-channel-K",
            messageTs: "1710000000.001110",
            threadTs: "1710000000.001100",
            mentions: Array.Empty<string>(),
            text: "after restart");

        Assert.Equal(originalSessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var reloaded = await verify.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
            .GetSessionIdAsync(connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
                "C-channel-K", "1710000000.001100");
        Assert.Equal(originalSessionId, reloaded);
    }

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
        var first = await PostChannelAsync(connection, "C-channel-M",
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

    private async Task<JsonElement> PostChannelAsync(
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
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task AssertWorkingProjectionAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string sessionId,
        string threadTs = "1710000000.000300")
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var progressRef = $"agent-session-followup:{sessionId}:";
        var progress = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs
                && row.Kind == SlackOutboxKinds.ReplaceableProgress
                && row.DispatchRef != null
                && EF.Functions.Like(row.DispatchRef, progressRef + "%"))
            .SingleAsync();
        Assert.Equal(SlackDeliveryOperations.PostMessage, SlackDeliveryPayload.Parse(progress.PayloadJson).Operation);
    }

    private async Task<AgentConnection> CreateConnectionAsync(string agentNameSuffix = "")
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var botUserId = string.IsNullOrEmpty(agentNameSuffix) ? "U123" : $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
            AppId = "A123",
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = botUserId,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
