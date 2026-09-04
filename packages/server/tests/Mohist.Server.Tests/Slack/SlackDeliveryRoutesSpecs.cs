using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L1")]
public sealed class SlackDeliveryRoutesSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, ReplyAnchor> _replyAnchors = new(StringComparer.Ordinal);

    public SlackDeliveryRoutesSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task List_deliveries_returns_all_rows_with_state_and_reason()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var uncertain = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_uncertain",
            "{\"text\":\"uncertain\"}"));
        await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, uncertain.Id, "claim timeout");

        using var response = await _fixture.Client.GetAsync(Path(connection, "/deliveries"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("data").GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        var entry = entries[0];
        Assert.Equal(uncertain.Id, entry.GetProperty("id").GetString());
        Assert.Equal(SlackOutboxStates.DeliveryUncertain, entry.GetProperty("state").GetString());
        Assert.Equal("claim timeout", entry.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Resend_endpoint_transitions_uncertain_to_pending_without_touching_execution_result()
    {
        var connection = await CreateConnectionAsync();
        var dispatchRef = "agentjob_42";
        string queuedDeliveryId;
        await using (var seedScope = _fixture.Services.CreateAsyncScope())
        {
            var outbox = seedScope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            var queued = await outbox.EnqueueAsync(new SlackOutboxDraft(
                connection.ProjectId,
                connection.Id,
                connection.WorkspaceTeamId,
                "D1",
                SlackOutboxKinds.TerminalResult,
                dispatchRef,
                "{\"text\":\"reply\"}"));
            await outbox.MarkDeliveryUncertainAsync(connection.ProjectId, queued.Id, "claim timeout");
            queuedDeliveryId = queued.Id;

            var db = seedScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var stateJson = $"{{\"input\":{{\"projectId\":\"{connection.ProjectId}\",\"agentId\":\"agent-1\"}},\"status\":\"{nameof(AgentJobStatus.Completed)}\",\"submittedAt\":\"{_fixture.TimeProvider.GetUtcNow():O}\"}}";
            var jobRow = new AgentJobRow
            {
                JobKey = dispatchRef,
                State = stateJson,
                Status = nameof(AgentJobStatus.Completed),
            };
            db.AgentJobs.Add(jobRow);
            await db.SaveChangesAsync();
        }

        using var resend = await _fixture.Client.PostAsync(
            Path(connection, $"/deliveries/{queuedDeliveryId}/resend"), content: null);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await verifyDb.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.Id == queuedDeliveryId);
        Assert.Equal(SlackOutboxStates.Pending, row.State);

        var job = await verifyDb.AgentJobs.AsNoTracking()
            .SingleAsync(j => j.JobKey == dispatchRef);
        Assert.Equal(nameof(AgentJobStatus.Completed), job.Status);
    }

    [Fact]
    public async Task Resend_endpoint_rejects_non_uncertain_row_with_409()
    {
        var connection = await CreateConnectionAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var pending = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.Id,
            connection.WorkspaceTeamId,
            "D1",
            SlackOutboxKinds.TerminalResult,
            "agentjob_pending",
            "{\"text\":\"pending\"}"));

        using var resend = await _fixture.Client.PostAsync(
            Path(connection, $"/deliveries/{pending.Id}/resend"), content: null);

        Assert.Equal(HttpStatusCode.Conflict, resend.StatusCode);
    }

    [Fact]
    public async Task Agent_reply_route_requires_the_complete_anchor_for_every_dispatch()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-route-anchor");
        var anchor = _replyAnchors["D-route-anchor"];

        using var missingConnection = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                workspaceTeamId = anchor.WorkspaceTeamId,
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadTs,
                triggeringMessageId = anchor.TriggeringMessageId,
                sessionId = anchor.SessionId,
                dispatchRef = anchor.DispatchRef,
                text = "answer",
            });
        using var missingTrigger = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                workspaceTeamId = anchor.WorkspaceTeamId,
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadTs,
                connectionId = anchor.ConnectionId,
                sessionId = anchor.SessionId,
                dispatchRef = anchor.DispatchRef,
                text = "answer",
            });
        using var legacy = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new { conversationId = "D-route-anchor", text = "legacy answer" });
        using var anchored = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-route-anchor", "anchored answer"));

        Assert.Equal(HttpStatusCode.BadRequest, missingConnection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingTrigger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        Assert.True(anchored.IsSuccessStatusCode, await anchored.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Agent_reply_with_invalid_base64_file_is_rejected_before_enqueue()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-render-invalid");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-render-invalid", fileName: "x.png", fileContentBase64: "not-base64!!"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackOutboxRows.AsNoTracking()
            .Where(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-render-invalid")
            .ToListAsync());
    }

    private async Task CreateDmMappingAsync(AgentConnection connection, string dmConversationId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        var sessionId = $"reply-route-session_{Guid.NewGuid():N}";
        var inputId = $"reply-route-input_{Guid.NewGuid():N}";
        var turnId = $"reply-route-turn_{Guid.NewGuid():N}";
        var threadTs = "1710000000.000001";
        var metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = connection.AgentId,
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId] = connection.WorkspaceTeamId,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = dmConversationId,
        });
        var provenance = new AgentSessionInputProvenance(
            "slack", connection.WorkspaceTeamId, dmConversationId, null,
            "U_OWNER", threadTs, connection.Id, BoundThreadRootMessageId: null);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
        {
            Id = $"dm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            SlackUserId = "U_OWNER",
            DmConversationId = dmConversationId,
            CurrentSessionId = sessionId,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).EnsureInitialLaunchAsync(
            new EnsureInitialLaunchCommand(
                inputId, turnId, "seed reply anchor", "agent-connection",
                $"reply-route-job_{Guid.NewGuid():N}", metadata,
                Runtime: "opencode", Provenance: provenance));
        _replyAnchors[dmConversationId] = new ReplyAnchor(
            connection.WorkspaceTeamId, dmConversationId, threadTs, threadTs,
            connection.Id, sessionId, $"slack:{sessionId}:{inputId}");
    }

    private object ReplyBody(
        string conversationId,
        string? text = null,
        string? imageUrl = null,
        string? fileName = null,
        string? fileContentBase64 = null)
    {
        var anchor = _replyAnchors[conversationId];
        return new
        {
            workspaceTeamId = anchor.WorkspaceTeamId,
            conversationId = anchor.ConversationId,
            threadTs = anchor.ThreadTs,
            connectionId = anchor.ConnectionId,
            triggeringMessageId = anchor.TriggeringMessageId,
            sessionId = anchor.SessionId,
            dispatchRef = anchor.DispatchRef,
            text,
            imageUrl,
            fileName,
            fileContentBase64,
        };
    }

    private sealed record ReplyAnchor(
        string WorkspaceTeamId,
        string ConversationId,
        string ThreadTs,
        string TriggeringMessageId,
        string ConnectionId,
        string SessionId,
        string DispatchRef);

    private async Task CreateThreadMappingAsync(
        AgentConnection connection,
        string conversationId,
        string threadTs)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.SlackThreadSessionMappings.Add(new SlackThreadSessionMappingRow
        {
            Id = $"thread_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            ConversationId = conversationId,
            ThreadTs = threadTs,
            SlackUserId = "U_OWNER",
            SessionId = $"session_{Guid.NewGuid():N}",
            RootMessageTs = threadTs,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentConnection> CreateConnectionAsync(string? projectId = null)
    {
        var seeded = await SlackManagedConnectionSeed.CreateAsync(_fixture, new SlackSeedOptions
        {
            ProjectId = projectId,
            WithAgent = false,
            WithManagedApp = false,
            WriteConnectionSecrets = false,
            WithRuntimeLease = false,
        });
        return seeded.Connection;
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
