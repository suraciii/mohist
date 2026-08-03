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
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackAttachmentEntryIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackAttachmentEntryIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Channel_root_mention_binds_slack_file_to_launch_input_without_text()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.FileContentResolver = fileId =>
        {
            Assert.Equal("F-CHANNEL-ATTACHMENT", fileId);
            return new SlackFileContent(
                new MemoryStream("hello"u8.ToArray()),
                "log.txt",
                "text/plain",
                5,
                new MemoryStream());
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId = "C-CHANNEL-ATTACHMENT",
            messageTs = "1710000000.000700",
            mentionedUserIds = new[] { connection.BotUserId },
            senderSlackUserId = "U_OWNER",
            text = $"<@{connection.BotUserId}> ",
            files = new[]
            {
                new { id = "F-CHANNEL-ATTACHMENT", name = "log.txt", mimetype = "text/plain", size = 5 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("sessionId", out var sessionIdElement),
            "channel root launch must surface the SessionInput owner session id.");
        Assert.True(data.TryGetProperty("inputId", out var inputIdElement),
            "channel root launch must surface a SessionInput id so the caller can correlate the file binding.");
        var sessionId = sessionIdElement.GetString();
        var inputId = inputIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.False(string.IsNullOrWhiteSpace(inputId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.SingleAsync(attachment =>
            attachment.ProjectId == connection.ProjectId
            && attachment.OwnerKind == "agent-input"
            && attachment.OwnerId == $"{sessionId}/{inputId}");
        Assert.Equal("slack", row.Source);
        Assert.Contains("F-CHANNEL-ATTACHMENT", _fixture.Slack.FileContentCalls);
    }

    [Fact]
    public async Task Ingress_redelivery_does_not_re_fetch_or_rebind_attachments()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.FileContentResolver = fileId =>
        {
            Assert.Equal("F-DM-REPLAY-ATTACHMENT", fileId);
            return new SlackFileContent(
                new MemoryStream("hello"u8.ToArray()),
                "note.txt",
                "text/plain",
                5,
                new MemoryStream());
        };

        var payload = new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-REPLAY-ATTACHMENT",
            messageTs = "1710000000.000500",
            senderSlackUserId = "U_OWNER",
            text = "have a look",
            files = new[]
            {
                new { id = "F-DM-REPLAY-ATTACHMENT", name = "note.txt", mimetype = "text/plain", size = 5 },
            },
        };

        using var first = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstDocument.RootElement.GetProperty("data");
        var firstSession = firstData.GetProperty("sessionId").GetString();
        Assert.True(firstData.TryGetProperty("inputId", out var firstInputElement),
            "DM launch must surface a SessionInput id so the caller can correlate the file binding.");
        var firstInput = firstInputElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstInput));

        var fetchCountAfterFirst = _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-REPLAY-ATTACHMENT");
        Assert.Equal(1, fetchCountAfterFirst);

        using var replay = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        var replayData = replayDocument.RootElement.GetProperty("data");
        Assert.Equal(firstSession, replayData.GetProperty("sessionId").GetString());
        if (replayData.TryGetProperty("inputId", out var replayInputElement)
            && replayInputElement.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(firstInput, replayInputElement.GetString());
        }
        Assert.Equal(fetchCountAfterFirst, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-REPLAY-ATTACHMENT"));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.Attachments
            .Where(attachment => attachment.ProjectId == connection.ProjectId
                && attachment.OwnerKind == "agent-input"
                && attachment.OwnerId == $"{firstSession}/{firstInput}")
            .CountAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Ingress_launch_with_mixed_oversized_and_valid_file_accepts_valid_and_reports_rejection()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.FileContentResolver = fileId =>
        {
            Assert.Equal("F-DM-VALID", fileId);
            return new SlackFileContent(
                new MemoryStream("hello"u8.ToArray()),
                "valid.txt",
                "text/plain",
                5,
                new MemoryStream());
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-MIXED",
            messageTs = "1710000000.000160",
            senderSlackUserId = "U_OWNER",
            text = "have a look",
            files = new[]
            {
                new { id = "F-DM-OVERSIZE", name = "big.bin", mimetype = "application/octet-stream", size = long.MaxValue },
                new { id = "F-DM-VALID", name = "valid.txt", mimetype = "text/plain", size = 5L },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var sessionId = data.GetProperty("sessionId").GetString();
        var inputId = data.GetProperty("inputId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.False(string.IsNullOrWhiteSpace(inputId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.Attachments
            .Where(attachment => attachment.ProjectId == connection.ProjectId
                && attachment.OwnerKind == "agent-input"
                && attachment.OwnerId == $"{sessionId}/{inputId}")
            .ToListAsync();
        var valid = Assert.Single(rows);
        Assert.Equal("slack", valid.Source);

        Assert.Equal(0, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-OVERSIZE"));
        Assert.Equal(1, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-VALID"));
    }

    [Fact]
    public async Task Ingress_attachment_only_dm_binds_slack_file_to_first_input()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.FileContentResolver = fileId =>
        {
            Assert.Equal("F-DM-ATTACHMENT", fileId);
            return new SlackFileContent(
                new MemoryStream("hello"u8.ToArray()),
                "note.txt",
                "text/plain",
                5,
                new MemoryStream());
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-ATTACHMENT",
            messageTs = "1710000000.000150",
            senderSlackUserId = "U_OWNER",
            text = "",
            files = new[]
            {
                new { id = "F-DM-ATTACHMENT", name = "note.txt", mimetype = "text/plain", size = 5 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("sessionId", out var sessionIdElement),
            "attachment-only DM launch must surface the SessionInput owner session id.");
        var sessionId = sessionIdElement.GetString();
        Assert.True(data.TryGetProperty("inputId", out var inputIdElement),
            "attachment-only DM launch must surface a SessionInput id so the caller can correlate the file binding.");
        var inputId = inputIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.False(string.IsNullOrWhiteSpace(inputId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Attachments.SingleAsync(attachment =>
            attachment.ProjectId == connection.ProjectId
            && attachment.OwnerKind == "agent-input"
            && attachment.OwnerId == $"{sessionId}/{inputId}");
        Assert.Equal("slack", row.Source);
        Assert.Equal(1, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-ATTACHMENT"));
    }

    [Fact]
    public async Task Ingress_followup_with_file_binds_slack_file_to_followup_input()
    {
        var connection = await CreateConnectionAsync();
        var sessionId = $"session-followup-files-{connection.Id}";
        await using (var setupScope = _fixture.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var mapping = setupScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            var now = _fixture.TimeProvider.GetUtcNow();
            var sessionState = """
                {"id":"__SESSION__","metadata":{"labels":{"mohist.io/project-id":"__PROJECT__","mohist.io/source-kind":"agent-connection","mohist.io/connection-id":"__CONNECTION__","mohist.io/slack-user-id":"U_OWNER","mohist.io/slack-conversation-id":"D-DM-FOLLOWUP-FILES","mohist.io/agent-id":"agent-1","mohist.io/agent-name":"Mohist Agent"}},"runtime":{"runnerId":"r1","workDir":null,"runtime":"opencode"},"settings":{"model":"gpt-4o"},"status":{"agentRuntimeSessionId":"runtime-followup-files","activity":"active","createdAt":"__NOW__","lastDataAt":"__NOW__"}}
                """;
            sessionState = sessionState
                .Replace("__SESSION__", sessionId)
                .Replace("__PROJECT__", connection.ProjectId)
                .Replace("__CONNECTION__", connection.Id)
                .Replace("__NOW__", now.UtcDateTime.ToString("O"));
            setupDb.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = "runtime-followup-files",
                RunnerId = "r1",
                Status = "bound",
                State = sessionState,
                CreatedAt = now.UtcDateTime,
            });
            setupDb.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = "runtime-followup-files",
                Sequence = 1,
                PromptText = "initial",
                PromptKind = "task",
                StartedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime,
            });
            await setupDb.SaveChangesAsync();

            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-FOLLOWUP-FILES", sessionId);
        }

        _fixture.Slack.FileContentResolver = fileId =>
        {
            return fileId switch
            {
                "F-DM-FOLLOWUP-FILE" => new SlackFileContent(
                    new MemoryStream("followup"u8.ToArray()),
                    "followup.txt",
                    "text/plain",
                    8,
                    new MemoryStream()),
                _ => throw new InvalidOperationException(fileId),
            };
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-FOLLOWUP-FILES",
            messageTs = "1710000000.000800",
            senderSlackUserId = "U_OWNER",
            text = "",
            files = new[]
            {
                new { id = "F-DM-FOLLOWUP-FILE", name = "followup.txt", mimetype = "text/plain", size = 8 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("followup").GetBoolean(), await response.Content.ReadAsStringAsync());
        var inputId = data.GetProperty("inputId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(inputId));
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await verifyDb.Attachments.SingleAsync(attachment =>
            attachment.ProjectId == connection.ProjectId
            && attachment.OwnerKind == "agent-input"
            && attachment.OwnerId == $"{sessionId}/{inputId}");
        Assert.Equal("slack", row.Source);
        Assert.Equal(1, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-FOLLOWUP-FILE"));

        using var replay = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-FOLLOWUP-FILES",
            messageTs = "1710000000.000800",
            senderSlackUserId = "U_OWNER",
            text = "",
            files = new[]
            {
                new { id = "F-DM-FOLLOWUP-FILE", name = "followup.txt", mimetype = "text/plain", size = 8 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, _fixture.Slack.FileContentCalls.Count(file => file == "F-DM-FOLLOWUP-FILE"));
    }

    [Fact]
    public async Task Ingress_followup_with_oversized_file_reports_rejection_individually()
    {
        var connection = await CreateConnectionAsync();
        var sessionId = $"session-followup-rejection-{connection.Id}";
        await using (var setupScope = _fixture.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var mapping = setupScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            var now = _fixture.TimeProvider.GetUtcNow();
            var sessionState = """
                {"id":"__SESSION__","metadata":{"labels":{"mohist.io/project-id":"__PROJECT__","mohist.io/source-kind":"agent-connection","mohist.io/connection-id":"__CONNECTION__","mohist.io/slack-user-id":"U_OWNER","mohist.io/slack-conversation-id":"D-DM-FOLLOWUP-REJECT","mohist.io/agent-id":"agent-1","mohist.io/agent-name":"Mohist Agent"}},"runtime":{"runnerId":"r1","workDir":null,"runtime":"opencode"},"settings":{"model":"gpt-4o"},"status":{"agentRuntimeSessionId":"runtime-followup-reject","activity":"active","createdAt":"__NOW__","lastDataAt":"__NOW__"}}
                """;
            sessionState = sessionState
                .Replace("__SESSION__", sessionId)
                .Replace("__PROJECT__", connection.ProjectId)
                .Replace("__CONNECTION__", connection.Id)
                .Replace("__NOW__", now.UtcDateTime.ToString("O"));
            setupDb.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = "runtime-followup-reject",
                RunnerId = "r1",
                Status = "bound",
                State = sessionState,
                CreatedAt = now.UtcDateTime,
            });
            await setupDb.SaveChangesAsync();

            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-FOLLOWUP-REJECT", sessionId);
        }

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-FOLLOWUP-REJECT",
            messageTs = "1710000000.000850",
            senderSlackUserId = "U_OWNER",
            text = "",
            files = new[]
            {
                new { id = "F-DM-FOLLOWUP-OVERSIZE", name = "big.bin", mimetype = "application/octet-stream", size = long.MaxValue },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("followup_rejected", data.GetProperty("kind").GetString());
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());

        await using var followupScope = _fixture.Services.CreateAsyncScope();
        var followupDb = followupScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await followupDb.Attachments
            .Where(attachment => attachment.ProjectId == connection.ProjectId && attachment.OwnerKind == "agent-input")
            .CountAsync();
        Assert.Equal(0, rows);
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
            ["im"] = ["chat:write", "im:history", "channels:history", "groups:history", "mpim:history", "reactions:write"],
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
            BotUserId = "U123",
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
