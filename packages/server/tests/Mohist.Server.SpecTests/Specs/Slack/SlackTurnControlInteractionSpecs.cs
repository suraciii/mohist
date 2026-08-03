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
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[CollectionDefinition("SlackTurnControlInteraction", DisableParallelization = true)]
public class SlackTurnControlInteractionCollection : ICollectionFixture<MohistIntegrationFixture>;

[Collection("SlackTurnControlInteraction")]
public sealed class SlackTurnControlInteractionSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly List<string> _runnerIds = [];

    public SlackTurnControlInteractionSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
    }

    [Fact]
    public async Task Normal_stop_text_is_accepted_as_a_followup_without_a_stop_request()
    {
        var connection = await CreateConnectionAsync();
        var seeded = await SeedExecutingSessionAsync(connection, "U_OWNER", "D-steer");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>().SetCurrentSessionIdAsync(
                connection.ProjectId,
                connection.Id,
                connection.WorkspaceTeamId,
                "U_OWNER",
                "D-steer",
                seeded.SessionId);
        }
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-steer",
            messageTs = "1710000000.000100",
            senderSlackUserId = "U_OWNER",
            text = "stop",
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("data");

        Assert.True(result.GetProperty("followup").GetBoolean());
        Assert.Equal("accepted", result.GetProperty("kind").GetString());
        Assert.DoesNotContain(hub.Invocations, invocation => invocation.Method == "CancelAgentSession");
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(seeded.SessionId);
        Assert.Equal(AgentTurnControlClassification.Executing,
            (await session.ResolveTurnControlAsync(seeded.TurnId))?.Classification);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("inputId").GetString()));
        Assert.Equal(AgentTurnControlClassification.Queued,
            (await session.ResolveTurnControlAsync(result.GetProperty("turnId").GetString()!))?.Classification);
    }

    [Fact]
    public async Task Owner_and_session_initiator_can_stop_their_own_bound_turns()
    {
        var ownerConnection = await CreateConnectionAsync();
        var ownerSession = await SeedExecutingSessionAsync(ownerConnection, "U_INITIATOR", "C-owner");
        var ownerAction = await CreateStopActionAsync(ownerConnection, ownerSession, "U_OWNER", "C-owner");
        Assert.Contains(SlackTurnControlService.StopActionId, ownerAction.Blocks.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb", ownerAction.ActionValue, StringComparison.Ordinal);
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var ownerResult = await PostInteractionAsync(ownerConnection, ownerAction, "U_OWNER", "C-owner");

        Assert.Equal("stopped", ownerResult.GetProperty("state").GetString());
        Assert.Single(hub.Invocations);
        await AssertControlDeliveryAsync(ownerConnection, ownerAction, "Work stopped.");

        var initiatorConnection = await CreateConnectionAsync();
        var initiatorSession = await SeedExecutingSessionAsync(initiatorConnection, "U_INITIATOR", "C-initiator");
        var initiatorAction = await CreateStopActionAsync(initiatorConnection, initiatorSession, "U_INITIATOR", "C-initiator");
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));

        var initiatorResult = await PostInteractionAsync(initiatorConnection, initiatorAction, "U_INITIATOR", "C-initiator");

        Assert.Equal("stopped", initiatorResult.GetProperty("state").GetString());
        Assert.Single(hub.Invocations);
    }

    [Fact]
    public async Task Another_allowlisted_member_cannot_use_an_initiators_action()
    {
        var connection = await CreateConnectionAsync();
        var seeded = await SeedExecutingSessionAsync(connection, "U_INITIATOR", "C-auth");
        var action = await CreateStopActionAsync(connection, seeded, "U_INITIATOR", "C-auth");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var result = await PostInteractionAsync(connection, action, "U_OTHER", "C-auth");

        Assert.Equal("unauthorized", result.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        Assert.Equal(AgentTurnStatus.Executing,
            Assert.Single(await _fixture.Grains.GetGrain<IAgentSessionGrain>(seeded.SessionId).ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task Expired_replayed_wrong_connection_and_terminal_actions_have_no_runtime_side_effect()
    {
        var connection = await CreateConnectionAsync();
        var seeded = await SeedExecutingSessionAsync(connection, "U_OWNER", "C-stale");
        var action = await CreateStopActionAsync(connection, seeded, "U_OWNER", "C-stale");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        hub.Clear();

        var tampered = await PostInteractionAsync(
            connection,
            action with { ActionValue = action.ActionValue + "tampered" },
            "U_OWNER",
            "C-stale");
        Assert.Equal("invalid_action", tampered.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var expired = await PostInteractionAsync(connection, action, "U_OWNER", "C-stale");
        Assert.Equal("expired", expired.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        var fresh = await CreateStopActionAsync(connection, seeded, "U_OWNER", "C-stale");
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("stopped"));
        var first = await PostInteractionAsync(connection, fresh, "U_OWNER", "C-stale");
        Assert.Equal("stopped", first.GetProperty("state").GetString());
        hub.Clear();
        var replay = await PostInteractionAsync(connection, fresh, "U_OWNER", "C-stale");
        Assert.Equal("replayed", replay.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        var other = await CreateConnectionAsync();
        var wrongConnection = await PostInteractionAsync(other, fresh, "U_OWNER", "C-stale");
        Assert.Equal("stale_action", wrongConnection.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        var terminalConnection = await CreateConnectionAsync();
        var terminalSession = await SeedExecutingSessionAsync(terminalConnection, "U_OWNER", "C-terminal");
        var terminalAction = await CreateStopActionAsync(terminalConnection, terminalSession, "U_OWNER", "C-terminal");
        var terminalGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(terminalSession.SessionId);
        await terminalGrain.MarkTurnTerminalAsync(terminalSession.TurnId, AgentTurnStatus.Completed, null);
        var laterTurnId = $"turn-{Guid.NewGuid():N}";
        await terminalGrain.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            $"input-{Guid.NewGuid():N}", laterTurnId, "later work", "user"));
        await terminalGrain.MarkTurnExecutingAsync(laterTurnId);
        var terminal = await PostInteractionAsync(terminalConnection, terminalAction, "U_OWNER", "C-terminal");
        Assert.Equal("stale_action", terminal.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);
        Assert.Equal(AgentTurnControlClassification.Executing,
            (await terminalGrain.ResolveTurnControlAsync(laterTurnId))?.Classification);
    }

    private async Task<SlackStopAction> CreateStopActionAsync(
        AgentConnection connection,
        SeededSession seeded,
        string actorSlackUserId,
        string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SlackTurnControlService>();
        var action = await service.CreateStopActionAsync(
            connection,
            seeded.SessionId,
            seeded.TurnId,
            seeded.InputId,
            $"dispatch:{seeded.SessionId}:{seeded.TurnId}",
            actorSlackUserId,
            new SlackMessageIdentity(connection.WorkspaceTeamId, conversationId, "1710000000.000001"),
            "1710000000.000001");
        return Assert.IsType<SlackStopAction>(action);
    }

    private async Task<JsonElement> PostInteractionAsync(
        AgentConnection connection,
        SlackStopAction action,
        string actorSlackUserId,
        string conversationId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection, "/interactions"), new
        {
            eventType = "block_actions",
            interactionId = $"interaction-{Guid.NewGuid():N}",
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs = "1710000000.000900",
            threadTs = "1710000000.000001",
            actorSlackUserId,
            actionId = action.ActionId,
            actionValue = action.ActionValue,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task AssertControlDeliveryAsync(
        AgentConnection connection,
        SlackStopAction action,
        string expectedText)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DispatchRef == SlackInteractionRoutes.ActionDispatchRef(action.ActionValue))
            .Select(row => row.PayloadJson)
            .SingleAsync();
        var document = JsonDocument.Parse(payload);
        Assert.Equal(expectedText, document.RootElement.GetProperty("text").GetString());
        Assert.True(document.RootElement.GetProperty("blocks").GetArrayLength() > 0);
        Assert.DoesNotContain("xoxb", payload, StringComparison.Ordinal);
    }

    private async Task<SeededSession> SeedExecutingSessionAsync(
        AgentConnection connection,
        string initiatorSlackUserId,
        string conversationId)
    {
        var sessionId = $"slack-control-{Guid.NewGuid():N}";
        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";
        var runnerId = $"slack-control-runner-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            WorkDir: "/mohist-tests/slack-turn-control",
            Metadata: ConnectionMetadata(connection, conversationId)));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            $"runtime-{sessionId}", "/mohist-tests/slack-turn-control"));
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            inputId,
            turnId,
            "work",
            "user",
            Provenance: new AgentSessionInputProvenance(
                "slack",
                connection.WorkspaceTeamId,
                conversationId,
                "1710000000.000001",
                initiatorSlackUserId,
                "1710000000.000001",
                connection.Id)));
        await session.MarkTurnExecutingAsync(turnId);
        await RegisterRunnerAsync(connection.ProjectId, runnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, $"{runnerId}-connection");
        return new SeededSession(sessionId, inputId, turnId);
    }

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

    private async Task<AgentConnection> CreateConnectionAsync()
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
            AccessPolicy = AccessPolicyKind.Anyone,
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
            AgentId = agentId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
            OwnerSlackUserId = "U_OWNER",
            AccessPolicy = AccessPolicyKind.Anyone,
        };
    }

    private static AgentSessionMetadata ConnectionMetadata(AgentConnection connection, string conversationId) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackUserId] = "U_OWNER",
            [AgentSessionQueryMetadataKeys.SlackConversationId] = conversationId,
            [GenericAgentSessionMetadata.AgentId] = connection.AgentId,
            [GenericAgentSessionMetadata.AgentName] = "Mohist Agent",
        });

    private static string IngressPath(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";

    private sealed record SeededSession(string SessionId, string InputId, string TurnId);
}
