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
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Data.Workspace;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Slack;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workspace;

[Collection("MohistIntegration")]
public sealed class InteractionWorkspaceSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public InteractionWorkspaceSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    // --- Slack channel: acceptance 1 / 2 ---

    [Fact]
    public async Task SlackChannel_FirstTrigger_CreatesWorkspaceAndBindsSession()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-first";

        var response = await PostChannelAsync(connection, conversationId, "1710000000.010001", "<@U123> first task");
        var sessionId = response.GetProperty("sessionId").GetString()!;

        var ws = await FindWorkspaceAsync(connection.ProjectId, $"slack-{conversationId}");
        Assert.NotNull(ws);
        Assert.Equal(WorkspaceStatus.Active, ws!.Status);
        Assert.Equal(new WorkspaceOrigin.Slack(connection.WorkspaceTeamId, conversationId), ws.Origin);
        Assert.Empty(ws.RepositoryNames);
        Assert.Equal(ws.Name, await SessionWorkspaceNameAsync(sessionId));

        var created = await SingleWorkspaceEventAsync(connection.ProjectId, ws.Name);
        Assert.Equal(EventCatalog.ReverseDns.WorkspaceCreated, created.Type);
        Assert.Equal("slack", Lineage(created, EventCatalog.Lineage.WorkspaceOriginKind));
    }

    [Fact]
    public async Task SlackChannel_SecondSession_ReusesSameWorkspace()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-reuse";

        var first = await PostChannelAsync(connection, conversationId, "1710000000.010101", "<@U123> first task");
        var firstSessionId = first.GetProperty("sessionId").GetString()!;
        var firstWorkspace = await SessionWorkspaceNameAsync(firstSessionId);

        var second = await PostChannelAsync(connection, conversationId, "1710000000.010102", "<@U123> second task");
        var secondSessionId = second.GetProperty("sessionId").GetString()!;

        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.Equal(firstWorkspace, await SessionWorkspaceNameAsync(secondSessionId));
        Assert.Single(await WorkspaceEventsAsync(connection.ProjectId, firstWorkspace!));
    }

    [Fact]
    public async Task SlackChannel_SecondAgent_EntersSameWorkspace()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-second-agent";
        var secondConnection = await CreateConnectionAsync(projectId: connection.ProjectId, agentNameSuffix: "second");

        var first = await PostChannelAsync(connection, conversationId, "1710000000.010201", "<@U123> first task");
        var firstWorkspace = await SessionWorkspaceNameAsync(first.GetProperty("sessionId").GetString()!);

        var second = await PostChannelAsync(secondConnection, conversationId, "1710000000.010202", "<@U123> second agent task");

        Assert.Equal(firstWorkspace, await SessionWorkspaceNameAsync(second.GetProperty("sessionId").GetString()!));
        Assert.Equal(firstWorkspace, await SessionWorkspaceNameAsync(first.GetProperty("sessionId").GetString()!));
    }

    // --- Slack channel archive: acceptance 3 ---

    [Fact]
    public async Task SlackChannel_Archive_Then_NextTrigger_CreatesFreshWorkspace()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-archive";

        var first = await PostChannelAsync(connection, conversationId, "1710000000.010301", "<@U123> first task");
        var firstWorkspaceName = await SessionWorkspaceNameAsync(first.GetProperty("sessionId").GetString()!);
        Assert.Equal($"slack-{conversationId}", firstWorkspaceName);

        var archive = await ArchiveChannelAsync(connection, conversationId);
        Assert.True(archive.GetProperty("archived").GetBoolean());

        var archivedWs = await FindWorkspaceAsync(connection.ProjectId, firstWorkspaceName!);
        Assert.Equal(WorkspaceStatus.Archived, archivedWs!.Status);
        Assert.NotNull(archivedWs.ArchivedAt);
        var firstEvents = await WorkspaceEventsAsync(connection.ProjectId, firstWorkspaceName!);
        Assert.Contains(firstEvents, row => row.Type == EventCatalog.ReverseDns.WorkspaceArchived);

        var second = await PostChannelAsync(connection, conversationId, "1710000000.010302", "<@U123> second task");
        var secondWorkspaceName = await SessionWorkspaceNameAsync(second.GetProperty("sessionId").GetString()!);

        Assert.NotEqual(firstWorkspaceName, secondWorkspaceName);
        Assert.Equal($"slack-{conversationId}-2", secondWorkspaceName);
        var secondWs = await FindWorkspaceAsync(connection.ProjectId, secondWorkspaceName!);
        Assert.Equal(WorkspaceStatus.Active, secondWs!.Status);
        Assert.Equal(new WorkspaceOrigin.Slack(connection.WorkspaceTeamId, conversationId), secondWs.Origin);
    }

    [Fact]
    public async Task SlackChannel_Archive_IsIdempotentAndIgnoresActiveSessions()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-archive-guard";

        var first = await PostChannelAsync(connection, conversationId, "1710000000.010401", "<@U123> first task");
        var workspaceName = await SessionWorkspaceNameAsync(first.GetProperty("sessionId").GetString()!);

        Assert.True((await ArchiveChannelAsync(connection, conversationId)).GetProperty("archived").GetBoolean());
        Assert.False((await ArchiveChannelAsync(connection, conversationId)).GetProperty("archived").GetBoolean());
        Assert.Equal(WorkspaceStatus.Archived, (await FindWorkspaceAsync(connection.ProjectId, workspaceName!))!.Status);
    }

    // --- Slack channel across projects: acceptance 4 ---

    [Fact]
    public async Task SlackChannel_TwoProjects_HaveIndependentWorkspaces()
    {
        var connectionA = await CreateConnectionAsync();
        var connectionB = await CreateConnectionAsync();
        const string conversationId = "C-interaction-two-projects";
        Assert.NotEqual(connectionA.ProjectId, connectionB.ProjectId);

        await PostChannelAsync(connectionA, conversationId, "1710000000.010501", "<@U123> project A task");
        await PostChannelAsync(connectionB, conversationId, "1710000000.010502", "<@U123> project B task");

        var wsA = await FindWorkspaceAsync(connectionA.ProjectId, $"slack-{conversationId}");
        var wsB = await FindWorkspaceAsync(connectionB.ProjectId, $"slack-{conversationId}");
        Assert.NotNull(wsA);
        Assert.NotNull(wsB);
        Assert.Equal(connectionA.ProjectId, wsA!.ProjectId);
        Assert.Equal(connectionB.ProjectId, wsB!.ProjectId);
        Assert.Equal(WorkspaceStatus.Active, wsA.Status);
        Assert.Equal(WorkspaceStatus.Active, wsB.Status);
        Assert.Equal(wsA.Name, wsB.Name);
        Assert.Equal(new WorkspaceOrigin.Slack("T123", conversationId), wsA.Origin);
        Assert.Equal(new WorkspaceOrigin.Slack("T123", conversationId), wsB.Origin);
    }

    // --- Slack DM ---

    [Fact]
    public async Task SlackDM_FirstTrigger_CreatesWorkspaceWithImChannelOrigin_AndFollowupReusesSession()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "D-interaction-dm";

        var first = await PostDmAsync(connection, conversationId, "1710000000.010601", "first task");
        var sessionId = first.GetProperty("sessionId").GetString()!;

        var ws = await FindWorkspaceAsync(connection.ProjectId, $"slack-{conversationId}");
        Assert.NotNull(ws);
        Assert.Equal(new WorkspaceOrigin.Slack(connection.WorkspaceTeamId, conversationId), ws!.Origin);
        Assert.Equal(ws.Name, await SessionWorkspaceNameAsync(sessionId));

        var followup = await PostDmAsync(connection, conversationId, "1710000000.010602", "follow-up question");
        Assert.Equal(sessionId, followup.GetProperty("sessionId").GetString());
        Assert.True(followup.GetProperty("followup").GetBoolean());
    }

    // --- Concurrency ---

    [Fact]
    public async Task SlackChannel_ConcurrentFirstCreate_ResolvesToOneWorkspace()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-concurrent";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<InteractionWorkspaceProvisioner>();
        var now = _fixture.TimeProvider.GetUtcNow();
        var results = await Task.WhenAll(
            provisioner.EnsureSlackWorkspaceAsync(connection.ProjectId, "T123", conversationId, now),
            provisioner.EnsureSlackWorkspaceAsync(connection.ProjectId, "T123", conversationId, now));

        Assert.Equal(results[0], results[1]);
        Assert.Equal($"slack-{conversationId}", results[0]);
        var active = await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>()
            .FindActiveByOriginAsync(
                connection.ProjectId,
                WorkspaceRowJson.OriginKind(new WorkspaceOrigin.Slack("T123", conversationId)),
                WorkspaceRowJson.OriginPayload(new WorkspaceOrigin.Slack("T123", conversationId)));
        Assert.NotNull(active);
        Assert.Equal(results[0], active!.Name);
        Assert.Single(await WorkspaceEventsAsync(connection.ProjectId, results[0]));
    }

    // --- Helpers ---

    private async Task<AgentConnection> CreateConnectionAsync(string? projectId = null, string agentNameSuffix = "")
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var createsProject = projectId is null;
        projectId ??= $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        if (createsProject)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, "T123");
        var botUserId = string.IsNullOrEmpty(agentNameSuffix) ? "U123" : $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = $"Mohist Agent {agentNameSuffix}",
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
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = "T123",
            AgentConnectionId = id,
            AppId = $"A_SPEC_{Guid.NewGuid():N}",
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
            WorkspaceTeamId = "T123",
            BotUserId = botUserId,
        };
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs = (string?)null,
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> PostDmAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs = (string?)null,
            mentionedUserIds = Array.Empty<string>(),
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> ArchiveChannelAsync(AgentConnection connection, string conversationId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(ArchivePath(connection), new
        {
            teamId = connection.WorkspaceTeamId,
            conversationId,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<WorkspaceState?> FindWorkspaceAsync(string projectId, string name)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>()
            .FindAsync(projectId, name);
    }

    private async Task<string?> SessionWorkspaceNameAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return (await db.AgentSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId))
            .LabelWorkspaceName;
    }

    private async Task<List<WorkspaceEventRow>> WorkspaceEventsAsync(string projectId, string name)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var source = WorkspaceEventPersistence.WorkspaceSource(projectId, name);
        return await db.WorkspaceEvents.AsNoTracking()
            .Where(row => row.Source == source)
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    private async Task<WorkspaceEventRow> SingleWorkspaceEventAsync(string projectId, string name) =>
        Assert.Single(await WorkspaceEventsAsync(projectId, name));

    private static string Lineage(WorkspaceEventRow row, string key) =>
        Extensions(row)[key];

    private static IReadOnlyDictionary<string, string> Extensions(WorkspaceEventRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.ExtensionsJson)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";

    private static string ArchivePath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/channel-archive";
}
