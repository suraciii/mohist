using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

public sealed class SlackManagedBotConnectionIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagedBotConnectionIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Connection_ignores_manager_and_cross_target_agent_bots_without_work_side_effects()
    {
        var team = $"T_CONNECTION_MANAGED_{Guid.NewGuid():N}";
        const string managerAppId = "A_CONNECTION_MANAGER";
        const string managerBotUserId = "U_CONNECTION_MANAGER";
        var enrollmentId = await SetupManagerAsync(team, managerAppId, managerBotUserId);
        var connectionA = await SeedConnectionAsync(
            team, enrollmentId, "A_CONNECTION_AGENT_A", "U_CONNECTION_AGENT_A", DesiredStateKind.Enabled);
        var connectionB = await SeedConnectionAsync(
            team, enrollmentId, "A_CONNECTION_AGENT_B", "U_CONNECTION_AGENT_B", DesiredStateKind.Enabled);

        var cases = new[]
        {
            PostBotAsync(connectionA, managerAppId, managerBotUserId,
                "1710000000.001001", isDirectMessage: true),
            PostBotAsync(connectionA, connectionA.AppId, connectionA.BotUserId,
                "1710000000.001002", isDirectMessage: true),
            PostBotAsync(connectionB, connectionA.AppId, connectionA.BotUserId,
                "1710000000.001003", isDirectMessage: false,
                threadTs: null, mentionedUserIds: [connectionB.BotUserId]),
            PostBotAsync(connectionB, connectionA.AppId, connectionA.BotUserId,
                "1710000000.001004", isDirectMessage: false,
                threadTs: "1710000000.000900", mentionedUserIds: []),
        };

        foreach (var responseTask in cases)
        {
            using var response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ignored", (await ReadDataAsync(response)).GetProperty("kind").GetString());
        }

        using (var redelivery = await PostBotAsync(
                   connectionB, connectionA.AppId, connectionA.BotUserId,
                   "1710000000.001004", isDirectMessage: false,
                   threadTs: "1710000000.000900", mentionedUserIds: []))
        {
            redelivery.EnsureSuccessStatusCode();
            Assert.Equal("ignored", (await ReadDataAsync(redelivery)).GetProperty("kind").GetString());
        }

        var rows = await CountConnectionRowsAsync(connectionA, connectionB);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
        Assert.Equal(0, rows.Sessions);
    }

    [Fact]
    public async Task Connection_ingress_requires_the_enrolled_api_app_id_before_admission()
    {
        var team = $"T_CONNECTION_APP_ID_{Guid.NewGuid():N}";
        var enrollmentId = await SetupManagerAsync(
            team, "A_APP_ID_MANAGER", "U_APP_ID_MANAGER");
        var connection = await SeedConnectionAsync(
            team, enrollmentId, "A_APP_ID_CONNECTION", "U_APP_ID_CONNECTION", DesiredStateKind.Enabled);
        var before = await SnapshotConnectionRowsAsync(connection);

        using var wrong = await PostBotAsync(
            connection, connection.AppId, connection.BotUserId,
            "1710000000.000010", isDirectMessage: true, apiAppId: "A_WRONG");
        using var missing = await PostBotAsync(
            connection, connection.AppId, connection.BotUserId,
            "1710000000.000011", isDirectMessage: true, apiAppId: string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("slack_app_identity_mismatch", await ReadCodeAsync(wrong));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("slack_app_identity_mismatch", await ReadCodeAsync(missing));
        var after = await SnapshotConnectionRowsAsync(connection);
        Assert.Equal(before.Inbox, after.Inbox);
        Assert.Equal(before.Outbox, after.Outbox);

        using var correct = await PostBotAsync(
            connection, connection.AppId, connection.BotUserId,
            "1710000000.000012", isDirectMessage: true);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.Equal("ignored", (await ReadDataAsync(correct)).GetProperty("kind").GetString());
    }

    private async Task<string> SetupManagerAsync(
        string team,
        string managerAppId,
        string managerBotUserId)
    {
        using var setup = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId,
            managerBotUserId,
        });
        setup.EnsureSuccessStatusCode();
        return await SlackRuntimeLeaseTestSupport.ProvisionVerifiedManagerAsync(
            _fixture, team, $"xapp-{team}", $"xoxb-{team}");
    }

    private async Task<SeededConnection> SeedConnectionAsync(
        string team,
        string enrollmentId,
        string appId,
        string botUserId,
        string desiredState)
    {
        var connectionId = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var agentAppId = $"managed-agent-app_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Agents.Add(new AgentRow
            {
                Id = agentId,
                ProjectId = projectId,
                Name = agentId,
                Status = AgentStatus.Active,
                State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
                {
                    Id = agentId,
                    ProjectId = projectId,
                    Name = agentId,
                    Status = AgentStatus.Active,
                    Instructions = "Handle Slack requests.",
                    AgentConfig = JsonSerializer.SerializeToElement(
                        new { model = "openai/gpt-4o", runtime = "opencode" }),
                }, JSON.Options),
            });
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = connectionId,
                ProjectId = projectId,
                AgentId = agentId,
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = team,
                AppId = appId,
                BotUserId = botUserId,
                BotName = agentId,
                SetupProgress = SetupProgressKind.Complete,
                DesiredState = desiredState,
                ConnectionHealth = ConnectionHealthKind.Healthy,
                AgentReadiness = AgentReadinessKind.Ready,
                OwnerSlackUserId = "U_HUMAN_OWNER",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
            {
                Id = agentAppId,
                EnrollmentId = enrollmentId,
                WorkspaceTeamId = team,
                AgentConnectionId = connectionId,
                AppId = appId,
                BotUserId = botUserId,
                AppLifecycle = SlackAppLifecycle.Created,
                Authorization = SlackAuthorizationState.Authorized,
                DesiredManifestVersion = 1,
                DesiredManifestHash = "managed-bot-test",
                VerifiedScopesJson = "[]",
                InstallUrl = string.Empty,
                RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
                BindingState = SlackAgentAppBindingState.Bound,
                AuditJson = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();

            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
                Encoding.UTF8.GetBytes($"xapp-{agentAppId}"));
            await secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken),
                Encoding.UTF8.GetBytes($"xoxb-{agentAppId}"));
        }

        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(
            _fixture, projectId, connectionId);
        return new SeededConnection(
            connectionId, projectId, team, appId, botUserId, leaseId);
    }

    private async Task<HttpResponseMessage> PostBotAsync(
        SeededConnection target,
        string authorAppId,
        string authorBotUserId,
        string messageTs,
        bool isDirectMessage,
        string? threadTs = null,
        IReadOnlyList<string>? mentionedUserIds = null,
        string? apiAppId = null) =>
        await _fixture.Client.PostAsJsonAsync(IngressPath(target), new
        {
            apiAppId = apiAppId ?? target.AppId,
            isDirectMessage,
            teamId = target.Team,
            conversationId = isDirectMessage ? "D_CONNECTION_MANAGED" : "C_CONNECTION_MANAGED",
            messageTs,
            threadTs,
            mentionedUserIds = mentionedUserIds ?? Array.Empty<string>(),
            senderSlackUserId = (string?)null,
            senderKind = "bot",
            authorBot = new
            {
                appId = authorAppId,
                botId = "B_MANAGED_AUTHOR",
                botUserId = authorBotUserId,
                identityConflict = false,
            },
            text = "managed Bot text must never become work input",
            leaseId = target.LeaseId,
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

    private static string IngressPath(SeededConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private async Task<(int Inbox, int Outbox, int Sessions)> CountConnectionRowsAsync(
        params SeededConnection[] connections)
    {
        var ids = connections.Select(connection => connection.Id).ToArray();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return (
            await db.SlackProviderInboxRows.CountAsync(row => ids.Contains(row.ConnectionId)),
            await db.SlackOutboxRows.CountAsync(row =>
                row.OwnerKind == SlackDeliveryOwnerKinds.Connection
                && ids.Contains(row.ConnectionId)),
            await db.AgentSessions.CountAsync(row =>
                row.LabelConnectionId != null && ids.Contains(row.LabelConnectionId)));
    }

    private async Task<(string[] Inbox, string[] Outbox)> SnapshotConnectionRowsAsync(SeededConnection connection)
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

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed record SeededConnection(
        string Id,
        string ProjectId,
        string Team,
        string AppId,
        string BotUserId,
        string LeaseId);
}
