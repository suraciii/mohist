using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackConnectionApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackConnectionApiSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RotateCredentials_rejects_an_invalid_App_token_before_replacing_secrets()
    {
        var connection = await CreateConnectionAsync();
        _fixture.Slack.AppsConnectionOpen = new(false, "invalid_auth", null);

        using var response = await _fixture.Client.PostAsJsonAsync(
            Path(connection, "/rotate-credentials"),
            new { appToken = "xapp-invalid", botToken = "xoxb-new" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorCodeAsync(response, "credential_verification_failed");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var app = await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.AppToken));
        var bot = await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken));
        Assert.Equal("xapp-old", Encoding.UTF8.GetString(app!));
        Assert.Equal("xoxb-old", Encoding.UTF8.GetString(bot!));
    }

    [Fact]
    public async Task RotateCredentials_replaces_both_secrets_only_after_the_bound_tokens_verify()
    {
        var connection = await CreateConnectionAsync();

        using var response = await _fixture.Client.PostAsJsonAsync(
            Path(connection, "/rotate-credentials"),
            new { appToken = "xapp-new", botToken = "xoxb-new" });

        response.EnsureSuccessStatusCode();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var app = await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.AppToken));
        var bot = await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken));
        Assert.Equal("xapp-new", Encoding.UTF8.GetString(app!));
        Assert.Equal("xoxb-new", Encoding.UTF8.GetString(bot!));
    }

    [Fact]
    public async Task Disabled_connection_rejects_ingress_and_withholds_pending_delivery()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            await outbox.EnqueueAsync(new SlackOutboxDraft(
                connection.ProjectId,
                connection.Id,
                connection.WorkspaceTeamId,
                "D123",
                SlackOutboxKinds.UserAction,
                null,
                "{\"text\":\"queued reply\"}"));
        }

        using var disable = await _fixture.Client.PostAsync(Path(connection, "/disable"), null);
        disable.EnsureSuccessStatusCode();

        using var ingress = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D123",
            messageTs = "1710000000.000001",
            senderSlackUserId = "U_OWNER",
            text = "do work",
        });
        Assert.Equal(HttpStatusCode.OK, ingress.StatusCode);
        using (var ingressDocument = JsonDocument.Parse(await ingress.Content.ReadAsStringAsync()))
        {
            Assert.Equal("rejected", ingressDocument.RootElement.GetProperty("data").GetProperty("kind").GetString());
            Assert.Equal("This Connection is disabled.", ingressDocument.RootElement.GetProperty("data").GetProperty("reason").GetString());
        }

        using var claim = await _fixture.Client.PostAsJsonAsync(Path(connection, "/deliveries/claim"), new { adapterId = "adapter-1" });
        claim.EnsureSuccessStatusCode();
        using var claimDocument = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        Assert.False(claimDocument.RootElement.TryGetProperty("data", out _));

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(0, await db.SlackProviderInboxRows.CountAsync(row => row.ConnectionId == connection.Id));
        Assert.Equal(SlackOutboxStates.Pending, await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id)
            .Select(row => row.State)
            .SingleAsync());
    }

    [Fact]
    public async Task Disabled_connection_rejects_adapter_session_renewal()
    {
        var connection = await CreateConnectionAsync();
        using var disable = await _fixture.Client.PostAsync(Path(connection, "/disable"), null);
        disable.EnsureSuccessStatusCode();

        using var renewal = await _fixture.Client.PostAsJsonAsync(Path(connection, "/adapter-session"), new { adapterId = "adapter-1" });

        Assert.Equal(HttpStatusCode.Conflict, renewal.StatusCode);
        await AssertErrorCodeAsync(renewal, "connection_disabled");
    }

    [Fact]
    public async Task Enable_restores_the_desired_state()
    {
        var connection = await CreateConnectionAsync();
        using var disable = await _fixture.Client.PostAsync(Path(connection, "/disable"), null);
        disable.EnsureSuccessStatusCode();

        using var enable = await _fixture.Client.PostAsync(Path(connection, "/enable"), null);
        enable.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await enable.Content.ReadAsStringAsync());
        Assert.Equal("enabled", document.RootElement.GetProperty("data").GetProperty("desiredState").GetString());
    }

    [Fact]
    public async Task Enable_does_not_replay_a_terminal_delivery_created_while_disabled()
    {
        var connection = await CreateConnectionAsync();
        using var disable = await _fixture.Client.PostAsync(Path(connection, "/disable"), null);
        disable.EnsureSuccessStatusCode();

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var handler = new SlackTerminalDeliveryHandler(
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<SlackTerminalDeliveryHandler>.Instance);
            var evt = new CloudEvent(
                "terminal-delivery",
                new Uri("/mohist/agent-job/job-1", UriKind.Relative),
                EventCatalog.ReverseDns.AgentJobTerminalDelivery,
                _fixture.TimeProvider.GetUtcNow(),
                JsonSerializer.SerializeToElement(new
                {
                    jobKey = "job-1",
                    workLabel = "queued work",
                    connectionId = connection.Id,
                    workspaceTeamId = connection.WorkspaceTeamId,
                    dmConversationId = "D123",
                    status = "completed",
                    message = "completed",
                    failureReason = (string?)null,
                    failureCategory = (string?)null,
                    artifactCount = 0,
                    exitCode = (int?)null,
                }),
                subject: "job-1",
                extensions: new Dictionary<string, string> { [EventCatalog.Lineage.ProjectId] = connection.ProjectId });
            await handler.HandleAsync(evt, CancellationToken.None);
        }

        using var enable = await _fixture.Client.PostAsync(Path(connection, "/enable"), null);
        enable.EnsureSuccessStatusCode();
        using var claim = await _fixture.Client.PostAsJsonAsync(Path(connection, "/deliveries/claim"), new { adapterId = "adapter-1" });
        claim.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Delete_reports_that_the_Slack_App_remains_installed()
    {
        var connection = await CreateConnectionAsync();

        using var response = await _fixture.Client.DeleteAsync(Path(connection, string.Empty));

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("Slack App remains installed", document.RootElement.GetProperty("data").GetProperty("slackAppRemovalNote").GetString(), StringComparison.Ordinal);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        Assert.Null(await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.AppToken)));
        Assert.Null(await secrets.LoadAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken)));
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    public async Task Lifecycle_operations_preserve_accepted_work_records(string operation)
    {
        var connection = await CreateConnectionAsync();
        var work = await SeedAcceptedWorkAsync(connection);

        using var response = operation == "disable"
            ? await _fixture.Client.PostAsync(Path(connection, "/disable"), null)
            : await _fixture.Client.DeleteAsync(Path(connection, string.Empty));
        response.EnsureSuccessStatusCode();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal("executing", await db.AgentJobs
            .Where(row => row.JobKey == work.JobKey)
            .Select(row => row.Status)
            .SingleAsync());
        var session = await db.AgentSessions.SingleAsync(row => row.Id == work.SessionId);
        Assert.Equal("bound", session.Status);
        Assert.Equal(work.SessionState, session.State);
        Assert.Equal("input prompt", await db.AgentSessionTranscriptTurns
            .Where(row => row.SessionId == session.Id)
            .Select(row => row.PromptText)
            .SingleAsync());
        Assert.Equal(work.AttachmentId, await db.Attachments
            .Where(row => row.Id == work.AttachmentId)
            .Select(row => row.Id)
            .SingleAsync());
    }

    [Fact]
    public async Task Diagnostic_route_returns_the_computed_summary()
    {
        var connection = await CreateConnectionAsync();

        using var response = await _fixture.Client.GetAsync(Path(connection, "/diagnostic"));

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("healthy", data.GetProperty("primaryState").GetString());
        Assert.Equal("No action needed.", data.GetProperty("nextAction").GetString());
        Assert.Equal("available", data.GetProperty("facts").GetProperty("ownerAvailability").GetString());
    }

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
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
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = "agent-1",
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

    private async Task<AcceptedWork> SeedAcceptedWorkAsync(AgentConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var now = _fixture.TimeProvider.GetUtcNow();
        var jobKey = $"job-{connection.Id}";
        var sessionId = $"session-{connection.Id}";
        var attachmentId = $"attachment-{connection.Id}";
        var sessionState = $"{{\"inputs\":[{{\"id\":\"input-{connection.Id}\"}}],\"turns\":[{{\"id\":\"turn-{connection.Id}\"}}]}}";
        db.AgentJobs.Add(new AgentJobRow
        {
            JobKey = jobKey,
            State = $"{{\"input\":{{\"projectId\":\"{connection.ProjectId}\",\"agentId\":\"agent-1\"}},\"status\":\"executing\",\"submittedAt\":\"{now:O}\"}}",
        });
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            AgentSessionId = sessionId,
            Status = "bound",
            State = sessionState,
            CreatedAt = now.UtcDateTime,
        });
        db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = "runtime-accepted",
            Sequence = 1,
            PromptText = "input prompt",
            PromptKind = "task",
            StartedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        });
        db.Attachments.Add(new AttachmentRow
        {
            Id = attachmentId,
            ProjectId = connection.ProjectId,
            OwnerKind = "session",
            OwnerId = sessionId,
            OriginalFileName = "evidence.txt",
            ContentType = "text/plain",
            Size = 8,
            StoragePath = "/mohist-tests/artifacts/evidence.txt",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new AcceptedWork(jobKey, sessionId, sessionState, attachmentId);
    }

    private sealed record AcceptedWork(string JobKey, string SessionId, string SessionState, string AttachmentId);

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expectedCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }
}
