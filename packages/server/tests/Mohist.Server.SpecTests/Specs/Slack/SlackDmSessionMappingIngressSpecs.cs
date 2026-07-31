using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Spec tests for the DM current-session mapping wired into the Slack DM
/// ingress. Lives in its own file so the parent
/// <c>SlackConnectionApiSpecs</c> stays under the C# test-file size ratchet
/// (design/testing.md). Companion to the unit-level
/// <c>SlackDmSessionMappingStoreTests</c> (provider-side round-trip) and the
/// spec-level <c>SlackDmSessionMappingMigrationSpecs</c> (schema surface).
/// </summary>
[Collection("MohistIntegration")]
public sealed class SlackDmSessionMappingIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackDmSessionMappingIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Ingress_records_dm_session_mapping_for_the_first_dm()
    {
        var connection = await CreateConnectionAsync();

        using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-MAP",
            messageTs = "1710000000.000100",
            senderSlackUserId = "U_OWNER",
            text = "first task",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        var sessionId = await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-MAP");

        Assert.False(string.IsNullOrEmpty(sessionId));
    }

    [Fact]
    public async Task Ingress_redelivery_collapses_to_already_accepted()
    {
        var connection = await CreateConnectionAsync();

        using var first = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-REPLAY",
            messageTs = "1710000000.000200",
            senderSlackUserId = "U_OWNER",
            text = "do this",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var replay = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-REPLAY",
            messageTs = "1710000000.000200",
            senderSlackUserId = "U_OWNER",
            text = "do this",
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var uniqueIdentities = await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DmConversationId == "D-DM-REPLAY")
            .CountAsync();
        Assert.Equal(1, uniqueIdentities);
    }

    [Fact]
    public async Task Ingress_followup_path_records_kinds_when_session_is_bound()
    {
        var connection = await CreateConnectionAsync();
        var sessionId = $"session-followup-{connection.Id}";
        var jobKey = $"job-followup-{connection.Id}";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            var now = _fixture.TimeProvider.GetUtcNow();
            var sessionState = """
                {"id":"__SESSION__","metadata":{"labels":{"mohist.io/project-id":"__PROJECT__","mohist.io/source-kind":"agent-connection","mohist.io/connection-id":"__CONNECTION__","mohist.io/slack-user-id":"U_OWNER","mohist.io/slack-conversation-id":"D-DM-FOLLOWUP","mohist.io/agent-id":"agent-1","mohist.io/agent-name":"Mohist Agent"}},"runtime":{"runnerId":"r1","workDir":null,"runtime":"opencode"},"settings":{"model":"gpt-4o"},"status":{"agentRuntimeSessionId":"runtime-followup","activity":"active","createdAt":"__NOW__","lastDataAt":"__NOW__"}}
                """;
            sessionState = sessionState
                .Replace("__SESSION__", sessionId)
                .Replace("__PROJECT__", connection.ProjectId)
                .Replace("__CONNECTION__", connection.Id)
                .Replace("__NOW__", now.UtcDateTime.ToString("O"));
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = "runtime-followup",
                RunnerId = "r1",
                Status = "bound",
                State = sessionState,
                CreatedAt = now.UtcDateTime,
            });
            db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = "runtime-followup",
                Sequence = 1,
                PromptText = "initial",
                PromptKind = "task",
                StartedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime,
            });
            db.AgentJobs.Add(new AgentJobRow
            {
                JobKey = jobKey,
                State = $"{{\"input\":{{\"projectId\":\"{connection.ProjectId}\",\"agentId\":\"agent-1\"}},\"status\":\"executing\",\"submittedAt\":\"{now:O}\"}}",
            });
            await db.SaveChangesAsync();

            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-FOLLOWUP", sessionId);
        }

        using var followup = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-FOLLOWUP",
            messageTs = "1710000000.000300",
            senderSlackUserId = "U_OWNER",
            text = "more details",
        });

        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
        using var document = JsonDocument.Parse(await followup.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("sessionId", out _),
            "follow-up path must surface the same session id without launching a new AgentJob");
        Assert.True(data.TryGetProperty("inputId", out _),
            "follow-up path must surface the SessionInput id");
        Assert.True(data.GetProperty("followup").GetBoolean());
    }

    [Fact]
    public async Task Delete_connection_cascades_dm_session_mappings()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-CASCADE", $"session-{connection.Id}");
        }

        await using (var verifyScope = _fixture.Services.CreateAsyncScope())
        {
            var mappingStore = verifyScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            Assert.Equal($"session-{connection.Id}",
                await mappingStore.GetCurrentSessionIdAsync(
                    connection.ProjectId, connection.Id, "D-DM-CASCADE"));
        }

        using var delete = await _fixture.Client.DeleteAsync(Path(connection, string.Empty));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        await using (var finalScope = _fixture.Services.CreateAsyncScope())
        {
            var mappingStore = finalScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            Assert.Null(await mappingStore.GetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, "D-DM-CASCADE"));
        }
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        _fixture.Slack.AppsConnectionOpen = new(true, null, "wss://socket.slack.com/?app_id=A123");
        _fixture.Slack.AuthTest = new(true, null, "T123", "Workspace", "U123", "Mohist", "B123", "A123");
        _fixture.Slack.BotsInfo = new(true, null, new("B123", "Mohist", "A123"));
        _fixture.Slack.PermissionsScopesList = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write", "im:history"],
            ["team"] = ["users:read"],
        });
        _fixture.Slack.UsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));

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
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
        };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}