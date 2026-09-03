using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
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

/// <summary>
/// Covers the issue-526 access-policy substrate. The Owner-only
/// access decision is the substrate that the wider policy work in
/// T-002/T-003 layers on top of; this file pins the behavior under
/// the default <c>owner_only</c> policy so a future widening
/// (allowlist, anyone) cannot silently regress the Owner path.
/// </summary>
[Collection("SlackApiSurface")]
public sealed partial class SlackAccessPolicySpecs : IAsyncLifetime
{
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);
    private readonly MohistIntegrationFixture _fixture;

    public SlackAccessPolicySpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    private SlackApiTestScript SlackApi =>
        _fixture.Services.GetRequiredService<SlackApiTestScript>();

    public ValueTask InitializeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SlackApi.Clear();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Non_owner_root_mention_under_default_owner_only_is_rejected_with_no_resources()
    {
        var connection = await CreateConnectionAsync();
        var data = await PostChannelAsync(
            connection,
            conversationId: "C-access-non-owner-reject",
            messageTs: "1710000000.100110",
            threadTs: null,
            mentions: new[] { connection.BotUserId },
            senderSlackUserId: "U_OTHER",
            text: "<@U123> I should not be able to invoke");

        Assert.Equal("rejected", data.GetProperty("kind").GetString());
        Assert.Contains("owner", data.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "C-access-non-owner-reject")
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connection.Id)
            .ToListAsync());
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
            apiAppId = "A123",
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId,
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private async Task<AgentConnection> CreateConnectionAsync(
        string? accessPolicy = null,
        IReadOnlyList<string>? allowMembers = null)
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
            AccessPolicy = accessPolicy ?? AccessPolicyKind.OwnerOnly,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        if (allowMembers is { Count: > 0 })
        {
            foreach (var member in allowMembers)
            {
                db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
                {
                    Id = $"slkalm_{Guid.NewGuid():N}",
                    ProjectId = projectId,
                    ConnectionId = id,
                    SlackUserId = member,
                    WorkspaceTeamId = "T123",
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync();
        }

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
        // The legacy connection-scoped addresses are dead seams: the lease
        // core and the access decider resolve the AgentApp addresses only.
        // Distinct values prove the live identity gate uses the verified
        // Agent App Bot token, never the old project/connection secret.
        await secrets.StoreAtomicallyAsync([
            new SecretStoreWrite(
                new SecretStoreAddress(projectId, id, SecretKind.AppToken),
                Encoding.UTF8.GetBytes("xapp-legacy")),
            new SecretStoreWrite(
                new SecretStoreAddress(projectId, id, SecretKind.BotToken),
                Encoding.UTF8.GetBytes("xoxb-legacy")),
            new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
                Encoding.UTF8.GetBytes("xapp")),
            new SecretStoreWrite(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken),
                Encoding.UTF8.GetBytes("xoxb-verified")),
        ]);
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
        };
    }
}
