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
using Mohist.Server.Runner.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[CollectionDefinition("SlackTurnControlInteraction")]
public class SlackTurnControlInteractionCollection : ICollectionFixture<IsolatedMohistIntegrationFixture>;

[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed class SlackTurnControlInteractionSpecs : IAsyncLifetime
{
    private readonly IsolatedMohistIntegrationFixture _fixture;
    private readonly List<string> _runnerIds = [];
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackTurnControlInteractionSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
    }

    [Fact]
    public async Task Interaction_requires_the_enrolled_api_app_id_before_control_side_effects()
    {
        var connection = await CreateConnectionAsync();
        var seeded = await SeedExecutingSessionAsync(connection, "U_OWNER", "C-app-id");
        var action = await CreateStopActionAsync(connection, seeded, "U_OWNER", "C-app-id");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
        hub.Clear();
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(seeded.SessionId);
        var beforeRows = await SnapshotConnectionRowsAsync(connection);
        var beforeTurns = JsonSerializer.Serialize(await session.ListTurnsAsync());
        var beforeInputs = JsonSerializer.Serialize(await session.ListInputsAsync());

        using var wrong = await _fixture.Client.PostAsJsonAsync(
            IngressPath(connection, "/interactions"),
            new
            {
                apiAppId = "A_WRONG",
                eventType = "block_actions",
                interactionId = "interaction-app-id-wrong",
                teamId = connection.WorkspaceTeamId,
                conversationId = "C-app-id",
                messageTs = "1710000000.000900",
                threadTs = "1710000000.000001",
                actorSlackUserId = "U_OWNER",
                actionId = action.ActionId,
                actionValue = action.ActionValue,
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
        using var missing = await _fixture.Client.PostAsJsonAsync(
            IngressPath(connection, "/interactions"),
            new
            {
                eventType = "block_actions",
                interactionId = "interaction-app-id-missing",
                teamId = connection.WorkspaceTeamId,
                conversationId = "C-app-id",
                messageTs = "1710000000.000901",
                threadTs = "1710000000.000001",
                actorSlackUserId = "U_OWNER",
                actionId = action.ActionId,
                actionValue = action.ActionValue,
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("slack_app_identity_mismatch", await ReadCodeAsync(wrong));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("slack_app_identity_mismatch", await ReadCodeAsync(missing));
        Assert.Empty(hub.Invocations);
        var afterRows = await SnapshotConnectionRowsAsync(connection);
        Assert.Equal(beforeRows.Inbox, afterRows.Inbox);
        Assert.Equal(beforeRows.Outbox, afterRows.Outbox);
        Assert.Equal(beforeTurns, JsonSerializer.Serialize(await session.ListTurnsAsync()));
        Assert.Equal(beforeInputs, JsonSerializer.Serialize(await session.ListInputsAsync()));

        hub.SetInvocationResponse("session.stop", new RunnerStopReply("stopped"));
        var correct = await PostInteractionAsync(connection, action, "U_OWNER", "C-app-id");
        Assert.Equal("stopped", correct.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Expired_replayed_wrong_connection_and_terminal_actions_have_no_runtime_side_effect()
    {
        var connection = await CreateConnectionAsync();
        var seeded = await SeedExecutingSessionAsync(connection, "U_OWNER", "C-stale");
        var action = await CreateStopActionAsync(connection, seeded, "U_OWNER", "C-stale");
        var hub = _fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
        hub.Clear();

        var tampered = await PostInteractionAsync(
            connection,
            action with { ActionValue = action.ActionValue + "tampered" },
            "U_OWNER",
            "C-stale");
        Assert.Equal("invalid_action", tampered.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        // The shared clock moved past the lease TTL: the adapter would have
        // renewed, so re-acquire the current lease before the stale-signature
        // interactions below.
        _connectionLeases[connection.Id] = await SlackRuntimeLeaseTestSupport
            .AcquireConnectionLeaseAsync(_fixture, connection.ProjectId, connection.Id);
        var expired = await PostInteractionAsync(connection, action, "U_OWNER", "C-stale");
        Assert.Equal("expired", expired.GetProperty("state").GetString());
        Assert.Empty(hub.Invocations);

        var fresh = await CreateStopActionAsync(connection, seeded, "U_OWNER", "C-stale");
        hub.SetInvocationResponse("session.stop", new RunnerStopReply("stopped"));
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
            apiAppId = "A123",
            eventType = "block_actions",
            interactionId = $"interaction-{Guid.NewGuid():N}",
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs = "1710000000.000900",
            threadTs = "1710000000.000001",
            actorSlackUserId,
            actionId = action.ActionId,
            actionValue = action.ActionValue,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private async Task<(string[] Inbox, string[] Outbox)> SnapshotConnectionRowsAsync(AgentConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var inboxRows = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ConnectionId == connection.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        var outboxRows = await db.SlackOutboxRows.AsNoTracking()
            .Where(row => row.ConnectionId == connection.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        return (
            inboxRows.Select(row => JsonSerializer.Serialize(new
            {
                row.Id,
                row.ProjectId,
                row.ConnectionId,
                row.SlackMessageIdentity,
                row.WorkspaceTeamId,
                row.ConversationId,
                row.ThreadTs,
                row.SlackUserId,
                row.RouteKind,
                row.RouteSessionId,
                row.RouteTurnId,
                row.AcceptedAt,
                row.DispatchedAt,
                row.CreatedAt,
            })).ToArray(),
            outboxRows.Select(row => JsonSerializer.Serialize(new
            {
                row.Id,
                row.ProjectId,
                row.ConnectionId,
                row.OwnerKind,
                row.WorkspaceTeamId,
                row.ConversationId,
                row.ThreadTs,
                row.Kind,
                row.State,
                row.DispatchRef,
                row.PayloadJson,
                row.AttemptCount,
                row.NextAttemptAt,
                row.ClaimedAt,
                row.ClaimedByAdapterId,
                row.DeliveredAt,
                row.DeliveryUncertainAt,
                row.DeadLetteredAt,
                row.LastError,
                row.CreatedAt,
                row.UpdatedAt,
            })).ToArray());
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
        var seeded = await SeedQueuedSessionAsync(connection, initiatorSlackUserId, conversationId);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(seeded.SessionId).MarkTurnExecutingAsync(seeded.TurnId);
        await RegisterRunnerAsync(connection.ProjectId, seeded.RunnerId);
        _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(seeded.RunnerId, $"{seeded.RunnerId}-connection");
        return seeded;
    }

    private async Task<SeededSession> SeedQueuedSessionAsync(
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
        return new SeededSession(sessionId, inputId, turnId, runnerId);
    }

    private async Task RegisterRunnerAsync(string projectId, string runnerId)
    {
        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
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

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, "T123");
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = "T123",
            AgentConnectionId = id,
            AppId = $"A_SPEC_{Guid.NewGuid():N}",
            BotUserId = "U123",
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
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
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

    private sealed record SeededSession(string SessionId, string InputId, string TurnId, string RunnerId);
}
