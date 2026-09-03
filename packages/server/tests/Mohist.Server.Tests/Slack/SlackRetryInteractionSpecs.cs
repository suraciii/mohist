using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Collection("SlackApiSurface")]
[Trait("level", "L1")]
public sealed class SlackRetryInteractionSpecs : IAsyncLifetime
{
    private readonly IsolatedMohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackRetryInteractionSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Accepted_retry_click_is_idempotent_and_updates_one_identity_stable_reply()
    {
        var connection = await CreateConnectionAsync();
        var failed = await SeedFailedRootAsync(connection, "U_OWNER", "C-retry-accepted");
        var action = await CreateRetryActionAsync(connection, failed);

        var first = await PostInteractionAsync(connection, action, "U_OWNER", "C-retry-accepted");
        Assert.Equal("attempt_accepted", first.GetProperty("state").GetString());

        var replay = await PostInteractionAsync(connection, action, "U_OWNER", "C-retry-accepted");
        Assert.Equal("attempt_accepted", replay.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var operation = await db.AgentRetryOperations
            .SingleAsync(row => row.ProjectId == connection.ProjectId
                && row.SessionId == failed.SessionId
                && row.TurnId == failed.TurnId);
        Assert.Equal("finished", operation.State);
        Assert.Equal("accepted", operation.ResultState);
        var feedback = Assert.Single(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.Kind == SlackOutboxKinds.UserAction
                && row.DispatchRef == SlackInteractionRoutes.ActionDispatchRef(action.ActionValue))
            .ToListAsync());
        Assert.Equal("Retry attempt accepted.", SlackDeliveryPayload.Parse(feedback.PayloadJson).Text);

        var retrySession = _fixture.Grains.GetGrain<IAgentSessionGrain>(operation.ResultSessionId!);
        Assert.NotNull(await retrySession.GetInitialLaunchAsync());
        var original = _fixture.Grains.GetGrain<IAgentSessionGrain>(failed.SessionId);
        var originalTurn = (await original.ListTurnsAsync()).Single(turn => turn.Id == failed.TurnId);
        Assert.Equal(AgentTurnStatus.Failed, originalTurn.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, originalTurn.Result!.FailureCategory);
    }

    private async Task<SlackRetryAction> CreateRetryActionAsync(
        AgentConnection connection,
        SeededFailedTurn failed)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SlackRetryActionService>();
        return Assert.IsType<SlackRetryAction>(await service.CreateRetryActionAsync(
            connection,
            failed.SessionId,
            failed.TurnId,
            new SlackMessageIdentity(connection.WorkspaceTeamId, failed.ConversationId, failed.MessageTs),
            failed.ThreadTs));
    }

    private async Task<JsonElement> PostInteractionAsync(
        AgentConnection connection,
        SlackRetryAction action,
        string actorSlackUserId,
        string conversationId)
    {
        using var response = await PostRawAsync(connection, action, actorSlackUserId, conversationId);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private Task<HttpResponseMessage> PostRawAsync(
        AgentConnection connection,
        SlackRetryAction action,
        string actorSlackUserId,
        string conversationId) =>
        _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            apiAppId = "A123",
            eventType = "block_actions",
            interactionId = $"interaction-{Guid.NewGuid():N}",
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs = "1710000000.000900",
            threadTs = action.ActionValue.Contains("\"threadTs\":\"", StringComparison.Ordinal) ? "1710000000.000001" : (string?)null,
            actorSlackUserId,
            actionId = action.ActionId,
            actionValue = action.ActionValue,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

    private async Task<SeededFailedTurn> SeedFailedRootAsync(
        AgentConnection connection,
        string initiatorSlackUserId,
        string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<AgentQuerier>()
            .GetByIdAsync(connection.ProjectId, connection.AgentId);
        var origin = new ConnectionLaunchOrigin(
            connection.Id,
            connection.WorkspaceTeamId,
            initiatorSlackUserId,
            conversationId,
            "1710000000.000001");
        var launch = await scope.ServiceProvider.GetRequiredService<IAgentLauncher>()
            .LaunchConnectionAsync(agent!, "retryable failure", origin);
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(launch.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        await session.MarkInitialTurnTerminalAsync(
            initial!.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                FailureReason: "runner unavailable",
                FailureCategory: AgentJobFailureReasons.RunnerUnavailable));
        return new SeededFailedTurn(
            launch.SessionId,
            launch.TurnId,
            conversationId,
            "1710000000.000001",
            null);
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
            AccessPolicy = AccessPolicyKind.OwnerOnly,
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
        _connectionLeases[id] = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
            OwnerSlackUserId = "U_OWNER",
            AccessPolicy = AccessPolicyKind.OwnerOnly,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/interactions";

    private sealed record SeededFailedTurn(
        string SessionId,
        string TurnId,
        string ConversationId,
        string MessageTs,
        string? ThreadTs);
}
