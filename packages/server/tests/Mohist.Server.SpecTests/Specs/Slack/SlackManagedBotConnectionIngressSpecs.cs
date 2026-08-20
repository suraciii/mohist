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
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

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
    public async Task Connection_ignores_transition_author_received_by_live_connection()
    {
        var team = $"T_CONNECTION_TRANSITION_{Guid.NewGuid():N}";
        var enrollmentId = await SetupManagerAsync(
            team, "A_TRANSITION_MANAGER", "U_TRANSITION_MANAGER");
        var target = await SeedConnectionAsync(
            team, enrollmentId, "A_TRANSITION_TARGET", "U_TRANSITION_TARGET", DesiredStateKind.Enabled);
        var author = await SeedConnectionAsync(
            team, enrollmentId, "A_TRANSITION_AUTHOR", "U_TRANSITION_AUTHOR", DesiredStateKind.Enabled);

        var transitions = new[]
        {
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Pending),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.InProgress),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Bound),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.ConnectionDeleted),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Conflict),
            (SlackAppLifecycle.Deleting, SlackAgentAppBindingState.InProgress),
        };

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var agentApp = await db.ManagedSlackAgentApps.SingleAsync(row =>
            row.AgentConnectionId == author.Id);

        for (var index = 0; index < transitions.Length; index++)
        {
            agentApp.AppLifecycle = transitions[index].Item1;
            agentApp.BindingState = transitions[index].Item2;
            agentApp.UpdatedAt = _fixture.TimeProvider.GetUtcNow();
            await db.SaveChangesAsync();

            using var response = await PostBotAsync(
                target, author.AppId, author.BotUserId,
                $"1710000000.{index + 200:000000}", isDirectMessage: false,
                threadTs: "1710000000.000100", mentionedUserIds: []);
            response.EnsureSuccessStatusCode();
            Assert.Equal("ignored", (await ReadDataAsync(response)).GetProperty("kind").GetString());
        }

        var rows = await CountConnectionRowsAsync(target, author);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
        Assert.Equal(0, rows.Sessions);
    }

    [Fact]
    public async Task Connection_ignores_managed_bot_when_disabled_without_disabled_audit()
    {
        var team = $"T_CONNECTION_DISABLED_{Guid.NewGuid():N}";
        var enrollmentId = await SetupManagerAsync(
            team, "A_DISABLED_MANAGER", "U_DISABLED_MANAGER");
        var connection = await SeedConnectionAsync(
            team, enrollmentId, "A_DISABLED_AGENT", "U_DISABLED_AGENT", DesiredStateKind.Enabled);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            await db.AgentConnections
                .Where(row => row.Id == connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.DesiredState, DesiredStateKind.Disabled));
        }

        using var response = await PostBotAsync(
            connection, connection.AppId, connection.BotUserId,
            "1710000000.001900", isDirectMessage: true);
        response.EnsureSuccessStatusCode();
        Assert.Equal("ignored", (await ReadDataAsync(response)).GetProperty("kind").GetString());

        var rows = await CountConnectionRowsAsync(connection);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
        Assert.Equal(0, rows.Sessions);
    }

    [Fact]
    public async Task Connection_preserves_unmatched_bot_and_human_ingress_behavior()
    {
        var team = $"T_CONNECTION_COMPAT_{Guid.NewGuid():N}";
        var enrollmentId = await SetupManagerAsync(
            team, "A_COMPAT_MANAGER", "U_COMPAT_MANAGER");
        var connection = await SeedConnectionAsync(
            team, enrollmentId, "A_COMPAT_AGENT", "U_COMPAT_AGENT", DesiredStateKind.Enabled);

        using (var thirdParty = await PostBotAsync(
                   connection, "A_THIRD_PARTY", "U_THIRD_PARTY",
                   "1710000000.002001", isDirectMessage: false,
                   mentionedUserIds: [connection.BotUserId]))
        {
            thirdParty.EnsureSuccessStatusCode();
            Assert.Equal("ignored", (await ReadDataAsync(thirdParty)).GetProperty("kind").GetString());
        }

        using var human = await _fixture.Client.PostAsJsonAsync(
            IngressPath(connection), new
            {
                isDirectMessage = false,
                teamId = connection.Team,
                conversationId = "C_CONNECTION_COMPAT",
                messageTs = "1710000000.002002",
                threadTs = (string?)null,
                mentionedUserIds = new[] { connection.BotUserId },
                senderSlackUserId = "U_HUMAN_OWNER",
                senderKind = "human",
                text = "<@U_COMPAT_AGENT> human task",
                leaseId = connection.LeaseId,
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
        human.EnsureSuccessStatusCode();
        var humanData = await ReadDataAsync(human);
        Assert.NotEqual("ignored", humanData.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Connection_managed_bot_with_invalid_identity_is_rejected_before_admission()
    {
        var team = $"T_CONNECTION_VALIDATION_{Guid.NewGuid():N}";
        var enrollmentId = await SetupManagerAsync(
            team, "A_VALIDATION_MANAGER", "U_VALIDATION_MANAGER");
        var connection = await SeedConnectionAsync(
            team, enrollmentId, "A_VALIDATION_AGENT", "U_VALIDATION_AGENT", DesiredStateKind.Enabled);

        using (var invalidIdentity = await PostBotAsync(
                   connection, connection.AppId, connection.BotUserId, "",
                   isDirectMessage: true))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidIdentity.StatusCode);
            Assert.Equal("invalid_slack_identity", await ReadCodeAsync(invalidIdentity));
        }

        using (var wrongWorkspace = await _fixture.Client.PostAsJsonAsync(
                   IngressPath(connection), new
                   {
                       isDirectMessage = true,
                       teamId = "T_WRONG_CONNECTION_WORKSPACE",
                       conversationId = "D_CONNECTION_VALIDATION",
                       messageTs = "1710000000.002004",
                       senderSlackUserId = (string?)null,
                       senderKind = "bot",
                       authorBot = new
                       {
                           appId = connection.AppId,
                           botId = "B_VALIDATION_AGENT",
                           botUserId = connection.BotUserId,
                           identityConflict = false,
                       },
                       text = "must not be admitted",
                       leaseId = connection.LeaseId,
                       adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongWorkspace.StatusCode);
            Assert.Equal("workspace_mismatch", await ReadCodeAsync(wrongWorkspace));
        }

        var rows = await CountConnectionRowsAsync(connection);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
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
        IReadOnlyList<string>? mentionedUserIds = null) =>
        await _fixture.Client.PostAsJsonAsync(IngressPath(target), new
        {
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
