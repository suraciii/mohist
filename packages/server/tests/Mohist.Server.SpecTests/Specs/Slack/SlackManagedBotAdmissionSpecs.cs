using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagedBotAdmissionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagedBotAdmissionSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_ignores_self_and_all_current_agent_transition_identities_without_work_side_effects()
    {
        var team = $"T_MANAGED_BOT_{Guid.NewGuid():N}";
        const string managerAppId = "A_MANAGER_MANAGED_BOT";
        const string managerBotUserId = "U_MANAGER_MANAGED_BOT";
        var (enrollmentId, leaseId) = await SetupManagerAsync(team, managerAppId, managerBotUserId);
        var transitionIdentities = await SeedAgentTransitionIdentitiesAsync(enrollmentId, team);

        var before = await CountManagerRowsAsync(enrollmentId);
        foreach (var identity in transitionIdentities)
        {
            using var response = await PostManagerBotAsync(
                team,
                managerAppId,
                leaseId,
                identity.AppId,
                identity.BotUserId,
                identity.MessageTs);
            response.EnsureSuccessStatusCode();
            await AssertIgnoredAsync(response);
        }

        using (var self = await PostManagerBotAsync(
                   team,
                   managerAppId,
                   leaseId,
                   managerAppId,
                   managerBotUserId,
                   "1710000000.000000"))
        {
            self.EnsureSuccessStatusCode();
            await AssertIgnoredAsync(self);
        }

        // Redelivery is re-evaluated and ignored without a durable duplicate
        // or an ignored-event record.
        using (var redelivery = await PostManagerBotAsync(
                   team,
                   managerAppId,
                   leaseId,
                   transitionIdentities[0].AppId,
                   transitionIdentities[0].BotUserId,
                   transitionIdentities[0].MessageTs))
        {
            redelivery.EnsureSuccessStatusCode();
            await AssertIgnoredAsync(redelivery);
        }

        var after = await CountManagerRowsAsync(enrollmentId);
        Assert.Equal(before, after);
        Assert.Equal(0, after.Inbox);
        Assert.Equal(0, after.Outbox);
    }

    [Fact]
    public async Task Manager_unmatched_third_party_bot_retains_existing_actor_rejection_path()
    {
        var team = $"T_THIRD_PARTY_BOT_{Guid.NewGuid():N}";
        const string managerAppId = "A_MANAGER_THIRD_PARTY_BOT";
        const string managerBotUserId = "U_MANAGER_THIRD_PARTY_BOT";
        var (enrollmentId, leaseId) = await SetupManagerAsync(team, managerAppId, managerBotUserId);

        using var response = await PostManagerBotAsync(
            team,
            managerAppId,
            leaseId,
            "A_THIRD_PARTY_BOT",
            "U_THIRD_PARTY_BOT",
            "1710000000.000001",
            isDirectMessage: true,
            senderSlackUserId: "U_THIRD_PARTY_SENDER");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Equal("manager_actor_not_authorized", data.GetProperty("reason").GetString());

        var rows = await CountManagerRowsAsync(enrollmentId);
        Assert.Equal(1, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
    }

    [Fact]
    public async Task Manager_explicit_unknown_sender_is_rejected_before_durable_admission()
    {
        var team = $"T_UNKNOWN_MANAGER_{Guid.NewGuid():N}";
        const string managerAppId = "A_UNKNOWN_MANAGER";
        const string managerBotUserId = "U_UNKNOWN_MANAGER";
        var (enrollmentId, leaseId) = await SetupManagerAsync(team, managerAppId, managerBotUserId);

        using var response = await PostManagerBotAsync(
            team,
            managerAppId,
            leaseId,
            authorAppId: null,
            authorBotUserId: null,
            messageTs: "1710000000.000010",
            isDirectMessage: true,
            senderSlackUserId: "U_UNKNOWN_SENDER",
            senderKind: "unknown");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Equal("manager_sender_required", data.GetProperty("reason").GetString());

        var rows = await CountManagerRowsAsync(enrollmentId);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
    }

    [Fact]
    public async Task Manager_managed_admission_fails_closed_for_receiver_only_conflicts_and_deleted_identities()
    {
        var team = $"T_MANAGED_BOT_REJECT_{Guid.NewGuid():N}";
        const string managerAppId = "A_MANAGER_MANAGED_BOT_REJECT";
        const string managerBotUserId = "U_MANAGER_MANAGED_BOT_REJECT";
        var (enrollmentId, leaseId) = await SetupManagerAsync(team, managerAppId, managerBotUserId);
        await SeedAgentAppAsync(
            enrollmentId,
            team,
            "A_DELETED_MANAGED_BOT",
            "U_DELETED_MANAGED_BOT",
            SlackAppLifecycle.Deleted,
            SlackAgentAppBindingState.Bound);
        await SeedAgentAppAsync(
            enrollmentId,
            team,
            "A_TOMBSTONED_MANAGED_BOT",
            "U_TOMBSTONED_MANAGED_BOT",
            SlackAppLifecycle.Created,
            SlackAgentAppBindingState.Bound,
            deletedAt: _fixture.TimeProvider.GetUtcNow());
        await SeedAgentAppAsync(
            enrollmentId,
            team,
            string.Empty,
            string.Empty,
            SlackAppLifecycle.Created,
            SlackAgentAppBindingState.Pending);
        await SeedAgentAppAsync(
            enrollmentId,
            $"T_OTHER_{Guid.NewGuid():N}",
            "A_WORKSPACE_MISMATCH",
            "U_WORKSPACE_MISMATCH",
            SlackAppLifecycle.Created,
            SlackAgentAppBindingState.Bound);

        var cases = new[]
        {
            new BotIdentity(null, null, "receiver-only"),
            new BotIdentity(managerAppId, "U_OTHER_BOT", "app-bot-conflict"),
            new BotIdentity("A_DELETED_MANAGED_BOT", "U_DELETED_MANAGED_BOT", "deleted"),
            new BotIdentity("A_TOMBSTONED_MANAGED_BOT", "U_TOMBSTONED_MANAGED_BOT", "tombstoned"),
            new BotIdentity("A_WORKSPACE_MISMATCH", "U_WORKSPACE_MISMATCH", "workspace-mismatch"),
            new BotIdentity(managerAppId, managerBotUserId, "source-conflict", IdentityConflict: true),
            new BotIdentity("A_UNKNOWN_MANAGED_BOT", "U_UNKNOWN_MANAGED_BOT", "unmatched"),
        };

        foreach (var identity in cases)
        {
            using var response = await PostManagerBotAsync(
                team,
                managerAppId,
                leaseId,
                identity.AppId,
                identity.BotUserId,
                identity.MessageTs,
                isDirectMessage: true,
                identityConflict: identity.IdentityConflict);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("rejected", data.GetProperty("kind").GetString());
            Assert.Equal("manager_sender_required", data.GetProperty("reason").GetString());
        }

        var rows = await CountManagerRowsAsync(enrollmentId);
        Assert.Equal(0, rows.Inbox);
        Assert.Equal(0, rows.Outbox);
    }

    private async Task<(string EnrollmentId, string LeaseId)> SetupManagerAsync(
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
        var enrollmentId = await SlackRuntimeLeaseTestSupport.ProvisionVerifiedManagerAsync(
            _fixture,
            team,
            $"xapp-{team}",
            $"xoxb-{team}");
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture,
            enrollmentId,
            team);
        return (enrollmentId, leaseId);
    }

    private async Task<HttpResponseMessage> PostManagerBotAsync(
        string team,
        string managerAppId,
        string leaseId,
        string? authorAppId,
        string? authorBotUserId,
        string messageTs,
        bool isDirectMessage = false,
        string? senderSlackUserId = null,
        bool identityConflict = false,
        string senderKind = "bot") =>
        await _fixture.Client.PostAsJsonAsync("/api/slack-manager/ingress", new
        {
            appId = managerAppId,
            workspaceTeamId = team,
            conversationId = "D_MANAGED_BOT",
            messageTs,
            senderKind,
            senderSlackUserId,
            authorBot = authorAppId is null && authorBotUserId is null
                ? null
                : new
                {
                    appId = authorAppId,
                    botId = "B_MANAGED_BOT",
                    botUserId = authorBotUserId,
                    identityConflict,
                },
            text = "managed bot text must not become work input",
            isDirectMessage,
            leaseId,
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

    private async Task<IReadOnlyList<BotIdentity>> SeedAgentTransitionIdentitiesAsync(
        string enrollmentId,
        string team)
    {
        var transitions = new[]
        {
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Pending),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.InProgress),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Bound),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.ConnectionDeleted),
            (SlackAppLifecycle.Created, SlackAgentAppBindingState.Conflict),
            (SlackAppLifecycle.Deleting, SlackAgentAppBindingState.InProgress),
        };
        var result = new List<BotIdentity>(transitions.Length);
        for (var index = 0; index < transitions.Length; index++)
        {
            var appId = $"A_AGENT_TRANSITION_{index}";
            var botUserId = $"U_AGENT_TRANSITION_{index}";
            var messageTs = $"1710000000.{index + 1:000000}";
            await SeedAgentAppAsync(
                enrollmentId,
                team,
                appId,
                botUserId,
                transitions[index].Item1,
                transitions[index].Item2);
            result.Add(new BotIdentity(appId, botUserId, messageTs));
        }

        return result;
    }

    private async Task SeedAgentAppAsync(
        string enrollmentId,
        string team,
        string appId,
        string botUserId,
        string appLifecycle,
        string bindingState,
        DateTimeOffset? deletedAt = null)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var connectionId = $"connection-managed-bot-{Guid.NewGuid():N}";
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = connectionId,
            ProjectId = $"project-managed-bot-{Guid.NewGuid():N}",
            AgentId = $"agent-managed-bot-{Guid.NewGuid():N}",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = team,
            AppId = string.IsNullOrEmpty(appId) ? string.Empty : appId,
            BotUserId = string.IsNullOrEmpty(botUserId) ? string.Empty : botUserId,
            BotName = "managed-bot-test",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = $"managed-agent-app-{Guid.NewGuid():N}",
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = team,
            AgentConnectionId = connectionId,
            AppId = appId,
            BotUserId = botUserId,
            AppLifecycle = appLifecycle,
            Authorization = SlackAuthorizationState.Authorized,
            DesiredManifestVersion = 1,
            DesiredManifestHash = "test-manifest",
            VerifiedScopesJson = "[]",
            InstallUrl = string.Empty,
            RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
            BindingState = bindingState,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int Inbox, int Outbox)> CountManagerRowsAsync(string enrollmentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var inbox = await db.SlackProviderInboxRows.CountAsync(row =>
            row.ProjectId == SlackDeliveryOwnerIds.ManagerProjectId
            && row.ConnectionId == enrollmentId);
        var outbox = await db.SlackOutboxRows.CountAsync(row =>
            row.OwnerKind == SlackDeliveryOwnerKinds.Manager
            && row.ConnectionId == enrollmentId);
        return (inbox, outbox);
    }

    private static async Task AssertIgnoredAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("ignored", data.GetProperty("kind").GetString());
        Assert.Equal("ignored", data.GetProperty("decision").GetString());
    }

    private sealed record BotIdentity(
        string? AppId,
        string? BotUserId,
        string MessageTs,
        bool IdentityConflict = false);
}
