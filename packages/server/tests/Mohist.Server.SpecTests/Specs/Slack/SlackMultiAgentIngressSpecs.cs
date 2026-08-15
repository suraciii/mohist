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

[Collection("MohistIntegration")]
public sealed partial class SlackMultiAgentIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackMultiAgentIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Multi_bot_mention_starts_no_work_and_prompts_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi", "U_BOT_A", "A_BOT_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi", "U_BOT_B", "A_BOT_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot",
            messageTs = "1710000000.010100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> who answers?",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot")
            .ToListAsync());
        Assert.Empty(await db.SlackThreadSessionMappings
            .Where(row => row.ConversationId == "C-multi-bot")
            .ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_message_is_not_rejected_by_a_non_claiming_owner()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-owner-routing", "U_OWNER_A", "A_OWNER_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-owner-routing", "U_OWNER_B", "A_OWNER_B");
        var messageTs = "1710000000.010150";
        var text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose";

        var prompt = await PostChannelAsync(
            connectionA,
            "C-multi-owner-routing",
            messageTs,
            null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            text,
            connectionA.OwnerSlackUserId);
        var otherDelivery = await PostChannelAsync(
            connectionB,
            "C-multi-owner-routing",
            messageTs,
            null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            text,
            connectionA.OwnerSlackUserId);

        Assert.Equal("ambiguous", prompt.GetProperty("kind").GetString());
        Assert.Equal("ignored", otherDelivery.GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionA.Id
                && row.ConversationId == "C-multi-owner-routing")
            .ToListAsync());
        Assert.Empty(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionB.Id
                && row.ConversationId == "C-multi-owner-routing")
            .ToListAsync());
    }

    [Fact]
    public async Task Two_connections_mentioning_same_multi_bot_prompt_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-2", "U_BOT_A2", "A_BOT_A2");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-2", "U_BOT_B2", "A_BOT_B2");

        var bodyA = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot-2",
            messageTs = "1710000000.010200",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var responseA = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), bodyA);
        responseA.EnsureSuccessStatusCode();
        using var docA = JsonDocument.Parse(await responseA.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", docA.RootElement.GetProperty("data").GetProperty("kind").GetString());

        var bodyB = new
        {
            isDirectMessage = false,
            teamId = connectionB.WorkspaceTeamId,
            conversationId = "C-multi-bot-2",
            messageTs = "1710000000.010200",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionB.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
            leaseId = _connectionLeases[connectionB.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var responseB = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionB), bodyB);
        responseB.EnsureSuccessStatusCode();
        using var docB = JsonDocument.Parse(await responseB.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", docB.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot-2"
                && row.Kind == SlackOutboxKinds.UserAction)
            .ToListAsync();
        var promptMessages = promptRows
            .Select(row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("text").GetString() ?? string.Empty)
            .ToList();
        Assert.Single(promptRows);
        Assert.Contains(promptMessages, text => text.Contains("Multiple Agents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Redelivered_ambiguous_message_does_not_repeat_prompt()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-3", "U_BOT_A3", "A_BOT_A3");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-3", "U_BOT_B3", "A_BOT_B3");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot-3",
            messageTs = "1710000000.010300",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> task",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        for (var i = 0; i < 3; i++)
        {
            using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
        }

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot-3"
                && row.Kind == SlackOutboxKinds.UserAction)
            .ToListAsync();
        Assert.Single(promptRows);
    }

    [Fact]
    public async Task Ambiguous_prompt_retries_after_claim_without_delivery()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-retry", "U_OWNER", "A_BOT_RETRY_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-retry", "U_OWNER", "A_BOT_RETRY_B");
        const string conversationId = "C-multi-retry";
        const string messageTs = "1710000000.010350";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                connectionA.ProjectId, connectionA.WorkspaceTeamId, conversationId, messageTs,
                null, connectionA.Id, new[] { connectionA.Id, connectionB.Id });
        }

        var replay = await PostChannelAsync(connectionA, conversationId,
            messageTs, null,
            new[] { connectionA.BotUserId, connectionB.BotUserId },
            $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose");
        Assert.Equal("ambiguous", replay.GetProperty("kind").GetString());

        await using var verify = _fixture.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var promptRows = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connectionA.Id
                && row.DispatchRef == SlackAmbiguousPromptStore.PromptDispatchRef(
                    connectionA.WorkspaceTeamId, conversationId, messageTs))
            .ToListAsync();
        Assert.Single(promptRows);
    }

    [Fact]
    public async Task Non_owner_ambiguous_message_is_rejected_without_prompt()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi-owner", "U_OWNER", "A_BOT_OWNER_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi-owner", "U_OWNER_B", "A_BOT_OWNER_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-owner",
            messageTs = "1710000000.010360",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());
        Assert.Contains("owner", doc.RootElement.GetProperty("data").GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        var bodyB = new
        {
            isDirectMessage = false,
            teamId = connectionB.WorkspaceTeamId,
            conversationId = "C-multi-owner",
            messageTs = "1710000000.010360",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = "U_OTHER",
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose",
            leaseId = _connectionLeases[connectionB.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var otherResponse = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionB), bodyB);
        otherResponse.EnsureSuccessStatusCode();
        using var otherDoc = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync());
        Assert.Equal("ignored", otherDoc.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var result = await PostChannelAttemptAsync(
            connection,
            conversationId,
            messageTs,
            threadTs,
            mentions,
            text,
            senderSlackUserId);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        return result.Data!.Value;
    }

    private async Task<(HttpStatusCode Status, JsonElement? Data)> PostChannelAttemptAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = senderSlackUserId ?? connection.OwnerSlackUserId ?? "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return (response.StatusCode, null);
        var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.GetProperty("data").Clone());
    }

    private async Task<AgentConnection> CreateConnectionAsync(
        string agentNameSuffix,
        string workspaceTeamId,
        string ownerSlackUserId,
        string appId,
        string? projectId = null)
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var resolvedProjectId = projectId ?? $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existingProject = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == resolvedProjectId);
        if (existingProject is null)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = resolvedProjectId,
                Name = resolvedProjectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var botUserId = $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = resolvedProjectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = resolvedProjectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                Instructions = "Handle Slack requests.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
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
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, workspaceTeamId);
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = workspaceTeamId,
            AgentConnectionId = id,
            AppId = appId,
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
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, resolvedProjectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = resolvedProjectId,
            WorkspaceTeamId = workspaceTeamId,
            BotUserId = botUserId,
            OwnerSlackUserId = ownerSlackUserId,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
