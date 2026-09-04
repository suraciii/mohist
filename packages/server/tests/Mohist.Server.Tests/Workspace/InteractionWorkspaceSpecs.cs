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
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Slack;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Services;
using Xunit;

namespace Mohist.Server.Tests.Workspace;

[Trait("level", "L1")]
public sealed class InteractionWorkspaceSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public InteractionWorkspaceSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    // --- Slack channel: acceptance 1 / 2 ---

    [Fact]
    public async Task SlackChannel_FirstTrigger_CreatesWorkspaceAndBindsSession()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-first";

        var response = await PostChannelAsync(connection, conversationId, "1710000000.010001", $"<@{connection.BotUserId}> first task");
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

    // --- Slack channel archive: acceptance 3 ---

    [Fact]
    public async Task SlackChannel_Archive_Then_NextTrigger_CreatesFreshWorkspace()
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "C-interaction-archive";

        var firstWorkspaceName = await EnsureSlackWorkspaceAsync(connection, conversationId);
        Assert.Equal($"slack-{conversationId}", firstWorkspaceName);

        var archive = await ArchiveChannelAsync(connection, conversationId);
        Assert.True(archive.GetProperty("archived").GetBoolean());

        var archivedWs = await FindWorkspaceAsync(connection.ProjectId, firstWorkspaceName!);
        Assert.Equal(WorkspaceStatus.Archived, archivedWs!.Status);
        Assert.NotNull(archivedWs.ArchivedAt);
        var firstEvents = await WorkspaceEventsAsync(connection.ProjectId, firstWorkspaceName!);
        Assert.Contains(firstEvents, row => row.Type == EventCatalog.ReverseDns.WorkspaceArchived);

        var second = await PostChannelAsync(connection, conversationId, "1710000000.010302", $"<@{connection.BotUserId}> second task");
        var secondWorkspaceName = await SessionWorkspaceNameAsync(second.GetProperty("sessionId").GetString()!);

        Assert.NotEqual(firstWorkspaceName, secondWorkspaceName);
        Assert.Equal($"slack-{conversationId}-2", secondWorkspaceName);
        var secondWs = await FindWorkspaceAsync(connection.ProjectId, secondWorkspaceName!);
        Assert.Equal(WorkspaceStatus.Active, secondWs!.Status);
        Assert.Equal(new WorkspaceOrigin.Slack(connection.WorkspaceTeamId, conversationId), secondWs.Origin);
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

    // --- Helpers ---

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var projectId = $"project_{Guid.NewGuid():N}";
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture, new SlackSeedOptions
        {
            ProjectId = projectId,
            WorkspaceTeamId = $"T-interaction-{projectId}",
            // Preserve this file's agent persona byte-exact in the seeded
            // Agent State.
            AgentInstructions = "Handle workspace interactions.",
        });
        _connectionLeases[seeded.Connection.Id] = seeded.LeaseId;
        return seeded.Connection;
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            apiAppId = "A123",
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
            apiAppId = "A123",
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

    private async Task<string> EnsureSlackWorkspaceAsync(AgentConnection connection, string conversationId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InteractionWorkspaceProvisioner>()
            .EnsureSlackWorkspaceAsync(
                connection.ProjectId,
                connection.WorkspaceTeamId,
                conversationId,
                _fixture.TimeProvider.GetUtcNow());
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
