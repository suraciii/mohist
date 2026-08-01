using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Serializes the channel-side stop/cancel coverage. The channel ingress
/// reads shared <see cref="RecordingSlackApiClient"/> state (per-user
/// <c>UsersInfo</c>, default <c>UsersInfo</c>, <c>ConversationsInfo</c>)
/// for the allowlist branch; running this collection non-parallel keeps
/// the mocks stable while each spec seeds a fresh connection.
/// </summary>
[CollectionDefinition("SlackChannelTurnControl", DisableParallelization = true)]
public class SlackChannelTurnControlCollection : ICollectionFixture<MohistIntegrationFixture>;

[Collection("SlackChannelTurnControl")]
public sealed class SlackChannelTurnControlSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly List<string> _runnerIds = [];

    public SlackChannelTurnControlSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _fixture.Slack.UsersInfoByUser.Clear();
        _fixture.Slack.DefaultUsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));
        _fixture.Slack.DefaultConversationsInfo = new(true, null, new("C-default", null, null, false, true));
        _fixture.Slack.ConversationsInfoResponses.Clear();
        _fixture.Slack.ConversationsInfoCalls.Clear();
        _fixture.Slack.UsersInfoCalls.Clear();
        _fixture.Slack.UsersInfoResolver = null;
        _fixture.Slack.ConversationsInfoResolver = null;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
    }

    [Fact]
    public async Task Owner_can_stop_an_active_turn_in_a_bound_thread()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.OwnerOnly);
        await SeedExecutingSessionAsync(connection, "C-owner-cancel", initiatorSlackUserId: "U_OWNER");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var stop = await PostChannelAsync(
            connection,
            conversationId: "C-owner-cancel",
            messageTs: "1710001000.000100",
            threadTs: "1710001000.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_OWNER",
            text: "stop");

        Assert.Equal("stopped", stop.GetProperty("kind").GetString());
        Assert.Contains("Work stopped",
            await ReadControlReplyAsync(connection, "C-owner-cancel", "1710001000.000100"));
    }

    [Fact]
    public async Task Session_initiator_can_stop_their_own_active_turn()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_INITIATOR");
        _fixture.Slack.UsersInfoByUser["U_INITIATOR"] = new(
            true, null, new("U_INITIATOR", "T123", false, false, false, false, false));
        await SeedExecutingSessionAsync(connection, "C-initiator-stop", initiatorSlackUserId: "U_INITIATOR");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var stop = await PostChannelAsync(
            connection,
            conversationId: "C-initiator-stop",
            messageTs: "1710001100.000100",
            threadTs: "1710001100.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_INITIATOR",
            text: "<@U123> stop");

        Assert.Equal("stopped", stop.GetProperty("kind").GetString());
        Assert.Contains("Work stopped",
            await ReadControlReplyAsync(connection, "C-initiator-stop", "1710001100.000100"));
        Assert.Single(hub.Invocations);
        Assert.Equal("CancelAgentSession", hub.Invocations[0].Method);
    }

    [Fact]
    public async Task Allowed_non_initiator_cannot_stop_another_members_turn()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_INITIATOR");
        await SeedAllowedMemberAsync(connection, "U_B");
        _fixture.Slack.UsersInfoByUser["U_INITIATOR"] = new(
            true, null, new("U_INITIATOR", "T123", false, false, false, false, false));
        _fixture.Slack.UsersInfoByUser["U_B"] = new(
            true, null, new("U_B", "T123", false, false, false, false, false));
        var (_, turnId) = await SeedExecutingSessionAsync(
            connection, "C-non-initiator-stop", initiatorSlackUserId: "U_INITIATOR");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var rejected = await PostChannelAsync(
            connection,
            conversationId: "C-non-initiator-stop",
            messageTs: "1710001200.000100",
            threadTs: "1710001200.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_B",
            text: "stop");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());
        Assert.Contains("Owner or the session initiator",
            rejected.GetProperty("reason").GetString()!);
        Assert.Empty(hub.Invocations);

        var sessionId = await GetSessionIdForThreadAsync(connection, "C-non-initiator-stop", "1710001200.000001");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        Assert.Equal(AgentTurnStatus.Executing,
            Assert.Single(await session.ListTurnsAsync()).Status);
        Assert.Equal(turnId, (await session.ListTurnsAsync())[0].Id);
    }

    [Fact]
    public async Task Expired_gesture_does_not_stop_a_later_turn()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.OwnerOnly);
        var (sessionId, firstTurnId, runnerId) = await SeedQueuedSessionAsync(
            connection, "C-expired-gesture", initiatorSlackUserId: "U_OWNER");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var firstCancel = await PostChannelAsync(
            connection,
            conversationId: "C-expired-gesture",
            messageTs: "1710001300.000100",
            threadTs: "1710001300.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_OWNER",
            text: "cancel");

        Assert.Equal("cancelled", firstCancel.GetProperty("kind").GetString());
        Assert.Equal(firstTurnId, firstCancel.GetProperty("turnId").GetString());

        var secondTurnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", secondTurnId, "later work", "user"));
        await RegisterRunnerAsync(connection.ProjectId, runnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(
            runnerId, $"{runnerId}-connection");
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{runnerId}", "/mohist-tests/slack-channel-control"));
        await session.MarkTurnExecutingAsync(secondTurnId);

        var stop = await PostChannelAsync(
            connection,
            conversationId: "C-expired-gesture",
            messageTs: "1710001300.000100",
            threadTs: "1710001300.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_OWNER",
            text: "cancel");

        Assert.Equal("already_ended", stop.GetProperty("kind").GetString());
        Assert.Equal(firstTurnId, stop.GetProperty("turnId").GetString());
        Assert.Empty(hub.Invocations);
        var turns = await session.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Cancelled, turns.Single(turn => turn.Id == firstTurnId).Status);
        Assert.Equal(AgentTurnStatus.Executing, turns.Single(turn => turn.Id == secondTurnId).Status);
    }

    [Fact]
    public async Task Stop_in_a_thread_with_no_binding_reports_no_active_work()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.OwnerOnly);

        var stop = await PostChannelAsync(
            connection,
            conversationId: "C-no-binding",
            messageTs: "1710001400.000100",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OWNER",
            text: "<@U123> stop");

        Assert.Equal("no_active_work", stop.GetProperty("kind").GetString());
        Assert.Contains("no active work",
            await ReadControlReplyAsync(connection, "C-no-binding", "1710001400.000100"));
    }

    [Fact]
    public async Task Stop_in_a_thread_with_only_ended_turns_reports_already_ended()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.OwnerOnly);
        var sessionId = await SeedEndedSessionAsync(
            connection, "C-already-ended", initiatorSlackUserId: "U_OWNER");

        var stop = await PostChannelAsync(
            connection,
            conversationId: "C-already-ended",
            messageTs: "1710001500.000100",
            threadTs: "1710001500.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_OWNER",
            text: "stop");

        Assert.Equal("already_ended", stop.GetProperty("kind").GetString());
        Assert.Contains("already ended",
            await ReadControlReplyAsync(connection, "C-already-ended", "1710001500.000100"));

        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turns = await session.ListTurnsAsync();
        Assert.All(turns, turn => Assert.Equal(AgentTurnStatus.Completed, turn.Status));
    }

    [Fact]
    public async Task Allowed_non_initiator_followup_is_still_accepted()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_INITIATOR");
        await SeedAllowedMemberAsync(connection, "U_B");
        _fixture.Slack.UsersInfoByUser["U_INITIATOR"] = new(
            true, null, new("U_INITIATOR", "T123", false, false, false, false, false));
        _fixture.Slack.UsersInfoByUser["U_B"] = new(
            true, null, new("U_B", "T123", false, false, false, false, false));
        await SeedExecutingSessionAsync(
            connection, "C-followup-allowed", initiatorSlackUserId: "U_INITIATOR");

        var followup = await PostChannelAsync(
            connection,
            conversationId: "C-followup-allowed",
            messageTs: "1710001600.000100",
            threadTs: "1710001600.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_B",
            text: "follow-up question");

        Assert.True(followup.GetProperty("followup").GetBoolean());
        Assert.Equal("accepted", followup.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(followup.GetProperty("inputId").GetString()));
    }

    [Fact]
    public async Task Unauthorized_stop_creates_no_inbox_or_session_input_resources()
    {
        var connection = await CreateConnectionAsync(accessPolicy: AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_INITIATOR");
        await SeedAllowedMemberAsync(connection, "U_B");
        _fixture.Slack.UsersInfoByUser["U_INITIATOR"] = new(
            true, null, new("U_INITIATOR", "T123", false, false, false, false, false));
        _fixture.Slack.UsersInfoByUser["U_B"] = new(
            true, null, new("U_B", "T123", false, false, false, false, false));
        await SeedExecutingSessionAsync(
            connection, "C-unauthorized-noop", initiatorSlackUserId: "U_INITIATOR");

        var rejected = await PostChannelAsync(
            connection,
            conversationId: "C-unauthorized-noop",
            messageTs: "1710001700.000100",
            threadTs: "1710001700.000001",
            mentions: Array.Empty<string>(),
            senderSlackUserId: "U_B",
            text: "stop");

        Assert.Equal("rejected", rejected.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-unauthorized-noop"
                && row.SlackMessageIdentity.EndsWith("1710001700.000100"))
            .ToListAsync());
    }

    private async Task<string> GetSessionIdForThreadAsync(
        AgentConnection connection,
        string conversationId,
        string threadTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var sessionId = await threadMapping.GetSessionIdAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            conversationId, threadTs);
        Assert.False(string.IsNullOrEmpty(sessionId));
        return sessionId!;
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string senderSlackUserId,
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
            senderSlackUserId,
            senderKind = "human",
            text,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> ReadControlReplyAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == conversationId
                && row.DispatchRef == $"slack-thread-control:T123/{conversationId}/{messageTs}")
            .Select(row => row.PayloadJson)
            .SingleAsync();
        return JsonDocument.Parse(payload).RootElement.GetProperty("text").GetString()!;
    }

    private async Task SeedAllowedMemberAsync(AgentConnection connection, string slackUserId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
        {
            Id = $"slkalm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            SlackUserId = slackUserId,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(string SessionId, string TurnId)> SeedExecutingSessionAsync(
        AgentConnection connection,
        string conversationId,
        string initiatorSlackUserId,
        string runnerId = "channel-control-runner")
    {
        var sessionId = $"slack-channel-ctrl-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-channel-control",
            Metadata: ChannelConnectionMetadata(connection, conversationId)));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", turnId, "queued work", "user",
            Provenance: new AgentSessionInputProvenance(
                ProviderKind: "slack",
                WorkspaceId: connection.WorkspaceTeamId,
                ConversationId: conversationId,
                ThreadId: "1710000000.000001",
                MemberId: initiatorSlackUserId,
                MessageId: "1710000000.000001",
                ConnectionId: connection.Id)));

        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{conversationId}", "/mohist-tests/slack-channel-control"));
        await session.MarkTurnExecutingAsync(turnId);

        await RegisterRunnerAsync(connection.ProjectId, runnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, $"{runnerId}-connection");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var threadTs = conversationId switch
        {
            "C-owner-cancel" => "1710001000.000001",
            "C-initiator-stop" => "1710001100.000001",
            "C-non-initiator-stop" => "1710001200.000001",
            "C-expired-gesture" => "1710001300.000001",
            "C-already-ended" => "1710001500.000001",
            "C-followup-allowed" => "1710001600.000001",
            "C-unauthorized-noop" => "1710001700.000001",
            _ => $"{conversationId}-root",
        };
        await threadMapping.UpsertAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            conversationId, threadTs, initiatorSlackUserId, sessionId, threadTs);
        return (sessionId, turnId);
    }

    private async Task<(string SessionId, string FirstTurnId, string RunnerId)> SeedQueuedSessionAsync(
        AgentConnection connection,
        string conversationId,
        string initiatorSlackUserId)
    {
        var sessionId = $"slack-channel-queued-{Guid.NewGuid():N}";
        var firstTurnId = $"turn-{Guid.NewGuid():N}";
        var runnerId = $"queued-turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-channel-control",
            Metadata: ChannelConnectionMetadata(connection, conversationId)));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", firstTurnId, "first turn", "user",
            Provenance: new AgentSessionInputProvenance(
                ProviderKind: "slack",
                WorkspaceId: connection.WorkspaceTeamId,
                ConversationId: conversationId,
                ThreadId: "1710000000.000001",
                MemberId: initiatorSlackUserId,
                MessageId: "1710000000.000001",
                ConnectionId: connection.Id)));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var threadTs = conversationId == "C-expired-gesture"
            ? "1710001300.000001"
            : $"{conversationId}-root";
        await threadMapping.UpsertAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            conversationId, threadTs, initiatorSlackUserId, sessionId, threadTs);
        return (sessionId, firstTurnId, runnerId);
    }

    private async Task<string> SeedEndedSessionAsync(
        AgentConnection connection,
        string conversationId,
        string initiatorSlackUserId)
    {
        var sessionId = $"slack-channel-ended-{Guid.NewGuid():N}";
        var firstTurnId = $"turn-{Guid.NewGuid():N}";
        var secondTurnId = $"turn-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-channel-control",
            Metadata: ChannelConnectionMetadata(connection, conversationId)));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", firstTurnId, "first turn", "user",
            Provenance: new AgentSessionInputProvenance(
                ProviderKind: "slack",
                WorkspaceId: connection.WorkspaceTeamId,
                ConversationId: conversationId,
                ThreadId: "1710000000.000001",
                MemberId: initiatorSlackUserId,
                MessageId: "1710000000.000001",
                ConnectionId: connection.Id)));
        await session.MarkTurnTerminalAsync(firstTurnId, AgentTurnStatus.Completed, null);
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", secondTurnId, "second turn", "user"));
        await session.MarkTurnTerminalAsync(secondTurnId, AgentTurnStatus.Completed, null);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var threadMapping = scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>();
        var threadTs = "1710001500.000001";
        await threadMapping.UpsertAsync(
            connection.ProjectId, connection.WorkspaceTeamId, connection.Id,
            conversationId, threadTs, initiatorSlackUserId, sessionId, threadTs);
        return sessionId;
    }

    private static AgentSessionMetadata ChannelConnectionMetadata(AgentConnection connection, string conversationId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackUserId] = "U_OWNER",
            [AgentSessionQueryMetadataKeys.SlackConversationId] = conversationId,
            [GenericAgentSessionMetadata.AgentId] = "agent-channel-control",
            [GenericAgentSessionMetadata.AgentName] = "Mohist Agent",
        });

    private async Task RegisterRunnerAsync(string projectId, string runnerId)
    {
        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        register.EnsureSuccessStatusCode();
        _runnerIds.Add(runnerId);
        using var slots = await _fixture.Client.PatchAsJsonAsync($"/api/runner/{runnerId}", new { slots = 1 });
        slots.EnsureSuccessStatusCode();
    }

    private async Task<AgentConnection> CreateConnectionAsync(string accessPolicy)
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Mohist Agent",
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
            BotUserId = "U123",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            AccessPolicy = accessPolicy,
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
            BotUserId = "U123",
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
