using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Spec tests for the first-use onboarding reply the Agent bot sends after a
/// successful owner claim (docs/slack.md「绑定 Owner 并验证」). The owner sends
/// the claim code in the Bot DM; the Bot confirms and gives a self-contained
/// first-use guide without echoing the code, and does not repeat the guide on
/// an owner transfer.
/// </summary>
[Collection("MohistIntegration")]
public sealed class SlackOwnerClaimOnboardingSpecs
{
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);
    private readonly MohistIntegrationFixture _fixture;

    public SlackOwnerClaimOnboardingSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Successful_claim_replies_with_a_self_contained_first_use_guide()
    {
        var connection = await SeedConnectionAsync(SetupProgressKind.ClaimOwner, ownerSlackUserId: null);
        var code = await GenerateCodeAsync(connection, "claim-owner");

        using var response = await PostIngressAsync(connection, "D-DM-CLAIM", "1710000000.000300", code, "U_NEW_OWNER");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("claimed", document.RootElement.GetProperty("data").GetProperty("kind").GetString());

        var text = await ReadReplyTextAsync(connection, "D-DM-CLAIM");
        Assert.Equal(
            "Owner claimed successfully. Here's how to get started:\n" +
            "• Send me a task right here in this DM.\n" +
            "• Invite me to a channel and @ me there to assign work.\n" +
            "• Reply in the thread of my message to follow up on a task.",
            text);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_transfer_confirms_without_repeating_the_first_use_guide()
    {
        var connection = await SeedConnectionAsync(SetupProgressKind.Complete, ownerSlackUserId: "U_OWNER");
        var code = await GenerateCodeAsync(connection, "transfer-owner");

        using var response = await PostIngressAsync(connection, "D-DM-TRANSFER", "1710000000.000400", code, "U_NEW_OWNER");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transferred", document.RootElement.GetProperty("data").GetProperty("kind").GetString());

        var text = await ReadReplyTextAsync(connection, "D-DM-TRANSFER");
        Assert.Equal("Owner transferred successfully.", text);
        Assert.DoesNotContain("get started", text, StringComparison.Ordinal);
    }

    private async Task<string> GenerateCodeAsync(AgentConnection connection, string action)
    {
        using var response = await _fixture.Client.PostAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/{action}", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("code").GetString()!;
    }

    private async Task<HttpResponseMessage> PostIngressAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text,
        string senderSlackUserId) =>
        await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress", new
            {
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId,
                messageTs,
                senderSlackUserId,
                text,
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });

    private async Task<string> ReadReplyTextAsync(AgentConnection connection, string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var payload = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == conversationId)
            .OrderBy(row => row.Id)
            .Select(row => row.PayloadJson)
            .LastAsync();
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("text").GetString()!;
    }

    private async Task<AgentConnection> SeedConnectionAsync(string setupProgress, string? ownerSlackUserId)
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
                AgentConfig = JsonSerializer.SerializeToElement(new
                {
                    model = "openai/gpt-4o",
                    runtime = "opencode",
                }),
            }),
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
            SetupProgress = setupProgress,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = ownerSlackUserId,
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
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
        };
    }
}
