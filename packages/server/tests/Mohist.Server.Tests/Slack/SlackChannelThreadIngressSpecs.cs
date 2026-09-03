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
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed partial class SlackChannelThreadIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackChannelThreadIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Owner_root_mention_launches_work_and_binds_thread_to_session()
    {
        var connection = await CreateConnectionAsync();
        var body = new
        {
            apiAppId = "A123",
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-channel-A",
            messageTs = "1710000000.000100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text = "<@U123> summarize the bug",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
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
            ConnectionId: connection.Id,
            BoundThreadRootMessageId: "1710000000.000100"), input!.Input!.Provenance);
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
        Assert.Contains($"Session: {sessionId}", initialProgressPayload.Text, StringComparison.Ordinal);
        Assert.Contains(sessionId!, initialProgressPayload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Working", initialProgressPayload.Text, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal("queued", replay.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(replay.GetProperty("sessionId").GetString()));
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
    public async Task Followup_acknowledgement_is_rebuilt_on_redelivery_and_queued_while_executing()
    {
        var connection = await CreateConnectionAsync();
        var replayRoot = await PostChannelAsync(connection, "C-channel-replay-ack",
            messageTs: "1710000000.000250",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> first task");
        var replaySession = _fixture.Grains.GetGrain<IAgentSessionGrain>(replayRoot.GetProperty("sessionId").GetString()!);
        await replaySession.MarkTurnTerminalAsync(
            replayRoot.GetProperty("turnId").GetString()!,
            AgentTurnStatus.Completed,
            null);
        await replaySession.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
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
        Assert.True(replay.GetProperty("followup").GetBoolean());

        await using (var verify = _fixture.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
            Assert.True(await db.SlackOutboxRows.AnyAsync(row => row.ConnectionId == connection.Id
                && row.DispatchRef == SlackStatusProjection.DispatchRef(
                    new SlackMessageIdentity("T123", "C-channel-replay-ack", "1710000000.000260"), "received")));
        }

        var executionRoot = await PostChannelAsync(connection, "C-channel-executing-ack",
            messageTs: "1710000000.000300",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            text: "<@U123> long task");
        var executionSessionId = executionRoot.GetProperty("sessionId").GetString()!;
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(executionSessionId)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                "runtime-channel-executing-ack", "/mohist-tests/slack-channel-C"));

        var queued = await PostChannelAsync(connection, "C-channel-executing-ack",
            messageTs: "1710000000.000310",
            threadTs: "1710000000.000300",
            mentions: Array.Empty<string>(),
            text: "more details");
        Assert.Equal(executionSessionId, queued.GetProperty("sessionId").GetString());
        Assert.True(queued.GetProperty("followup").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(queued.GetProperty("inputId").GetString()));

        await using var progressScope = _fixture.Services.CreateAsyncScope();
        var progressDb = progressScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var progressRow = await progressDb.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-executing-ack"
                && row.ThreadTs == "1710000000.000300"
                && row.Kind == SlackOutboxKinds.ReplaceableProgress)
            .SingleAsync(row => row.DispatchRef != null
                && row.DispatchRef.StartsWith($"agent-session-followup:{executionSessionId}:"));
        Assert.Equal(
            SlackDeliveryOperations.PostMessage,
            SlackDeliveryPayload.Parse(progressRow.PayloadJson).Operation);
        var progressPayload = SlackDeliveryPayload.Parse(progressRow.PayloadJson);
        Assert.Contains($"Session: {executionSessionId}", progressPayload.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Working", progressPayload.Text, StringComparison.OrdinalIgnoreCase);
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
            apiAppId = "A123",
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
        Assert.Equal("adapter", data.GetProperty("responseOwner").GetString());
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
        Assert.Empty(await dbVerify.AgentJobs
            .Where(row => row.ProjectId == connection.ProjectId)
            .ToListAsync());
        Assert.Empty(await dbVerify.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-channel-backpressured")
            .ToListAsync());
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

    private async Task SetAgentConfigAsync(AgentConnection connection, object? config)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Agents.SingleAsync(agent => agent.ProjectId == connection.ProjectId);
        var agent = new Mohist.Server.Agent.Domain.Agent
        {
            Id = row.Id,
            ProjectId = connection.ProjectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            Instructions = "Handle Slack requests.",
            AgentConfig = config is null ? null : JsonSerializer.SerializeToElement(config),
        };
        row.State = JsonSerializer.Serialize(agent, JSON.Options);
        await db.SaveChangesAsync();
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
            apiAppId = "A123",
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
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
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
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, "T123");
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
                Instructions = "Handle Slack requests.",
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

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = "T123",
            AgentConnectionId = id,
            AppId = $"A_SPEC_{Guid.NewGuid():N}",
            BotUserId = botUserId,
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.Authorized,
            RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
            DesiredManifestVersion = 1,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            AppLevelTokenRef = agentAppId,
            BotTokenRef = agentAppId,
            BindingState = SlackAgentAppBindingState.Bound,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAtomicallyAsync([
            new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
                Encoding.UTF8.GetBytes("xapp")),
            new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken),
                Encoding.UTF8.GetBytes("xoxb")),
        ]);
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
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
