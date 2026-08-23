using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

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
    public async Task Selection_obligation_worker_recovers_cross_project_root_once()
    {
        var promptOwner = await CreateConnectionAsync("recovery-owner", "T-recovery", "U_RECOVERY", "A_RECOVERY");
        var selected = await CreateConnectionAsync("recovery-selected", "T-recovery", "U_SELECTED", "B_RECOVERY");
        var identity = new SlackMessageIdentity("T-recovery", "C-recovery", "1710000000.020100");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                threadTs: null,
                promptOwner.Id,
                candidates,
                "U_RECOVERY",
                "recover in the selected project",
                "[]",
                SlackAmbiguityKinds.RootMultiMention);
            var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.RootLaunch,
                ids.SessionId,
                ids.InputId,
                ids.TurnId);
        }

        var worker = new SlackAgentSelectionObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            Options.Create(new SlackProviderOptions()),
            NullLogger<SlackAgentSelectionObligationWorker>.Instance);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var promptsAfter = verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await promptsAfter.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(selected.ProjectId, claim.ChosenProjectId);
        Assert.Equal(selected.Id, claim.ChosenConnectionId);
        Assert.Equal(
            SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity).SessionId,
            claim.SelectionSessionId);

        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.Id == claim.SelectionSessionId)
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == promptOwner.Id)
            .ToListAsync());
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
