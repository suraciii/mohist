using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
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
public sealed class SlackReplySegmentationSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, ReplyAnchor> _replyAnchors = new(StringComparer.Ordinal);

    public SlackReplySegmentationSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Overlong_agent_reply_is_split_into_ordered_segments_with_no_content_loss()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-segment-long");
        var lines = Enumerable.Range(0, 1_500)
            .Select(index => $"Section {index:D5}: persistent detail line kept verbatim across the long report.")
            .ToList();
        var body = string.Join('\n', lines);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-segment-long", body));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-segment-long");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);

        // The over-long body is delivered as more than one segment, each within
        // Slack's single-message limit, so nothing is truncated or rejected.
        Assert.NotNull(payload.Segments);
        Assert.True(payload.Segments!.Count > 1);
        Assert.All(payload.Segments, segment =>
            Assert.InRange(segment.EnumerateRunes().Count(), 1, SlackFinalReplyRenderer.DefaultReplySegmentLength));
        var rejoined = string.Join('\n', payload.Segments);
        Assert.Contains(lines[0], rejoined, StringComparison.Ordinal);
        Assert.Contains(lines[^1], rejoined, StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Contains(line, rejoined, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Short_agent_reply_stays_a_single_message_without_segments()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-segment-short");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-segment-short", "Done. The task is complete."));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-segment-short");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);

        Assert.Null(payload.Segments);
        Assert.Equal("Done. The task is complete.", payload.Text);
    }

    [Fact]
    public async Task Repeated_overlong_sends_re_segment_the_merged_answer_without_loss()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-segment-merge");
        var part = string.Join('\n', Enumerable.Range(0, 1_500)
            .Select(index => $"Merge {index:D5}: repeated long content for the merged answer."));

        var first = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-segment-merge", part));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var second = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            ReplyBody("D-segment-merge", part));
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.SlackOutboxRows.AsNoTracking()
            .SingleAsync(r => r.ProjectId == connection.ProjectId && r.ConversationId == "D-segment-merge");
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);

        Assert.NotNull(payload.Segments);
        Assert.All(payload.Segments!, segment =>
            Assert.InRange(segment.EnumerateRunes().Count(), 1, SlackFinalReplyRenderer.DefaultReplySegmentLength));
        var rejoined = string.Join('\n', payload.Segments);
        // The second anchored send is an idempotent retry of the same turn.
        var markerOccurrences = payload.Segments!
            .Sum(segment => segment.Split("Merge 00000:", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, markerOccurrences);
    }

    [Fact]
    public async Task Delivered_reply_rejects_an_extension_that_cannot_converge_in_one_message()
    {
        var connection = await CreateConnectionAsync();
        await CreateDmMappingAsync(connection, "D-segment-update");
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var firstBody = new string('a', 20_000);
        var secondBody = new string('b', 20_000);
        const string dispatchRef = "agent-session-followup:segment-update:turn-1";

        var first = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-segment-update",
            "1710000000.000699",
            firstBody,
            connectionId: connection.Id,
            replyDispatchRef: dispatchRef);
        await outbox.MarkDeliveredAsync(
            connection.ProjectId,
            first.DeliveryId!,
            new SlackProviderMessageIdentity("D-segment-update", "1710000000.000700"));
        var rejected = await outbox.EnqueueAgentReplyAsync(
            connection.ProjectId,
            "D-segment-update",
            "1710000000.000699",
            secondBody,
            connectionId: connection.Id,
            replyDispatchRef: dispatchRef);

        Assert.False(rejected.Accepted);
        Assert.True(rejected.ConflictingDuplicate);
        var row = (await outbox.ListAsync(connection.ProjectId, connection.Id)).Entries.Single();
        var payload = SlackDeliveryPayload.Parse(row.PayloadJson);
        Assert.Equal(SlackDeliveryOperations.PostMessage, payload.Operation);
        Assert.Null(payload.Segments);
        Assert.Equal(firstBody, payload.Text);
        Assert.Equal("1710000000.000700", payload.ProviderMessageIdentity?.MessageTs);
    }

    private async Task CreateDmMappingAsync(AgentConnection connection, string dmConversationId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
        var sessionId = $"reply-route-session_{Guid.NewGuid():N}";
        var inputId = $"reply-route-input_{Guid.NewGuid():N}";
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
                inputId, $"reply-route-turn_{Guid.NewGuid():N}", "seed reply anchor",
                "agent-connection", $"reply-route-job_{Guid.NewGuid():N}", metadata,
                Runtime: "opencode", Provenance: provenance));
        _replyAnchors[dmConversationId] = new ReplyAnchor(
            connection.WorkspaceTeamId, dmConversationId, threadTs, threadTs,
            connection.Id, sessionId, $"slack:{sessionId}:{inputId}");
    }

    private object ReplyBody(string conversationId, string? text = null)
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

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
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
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            AgentId = "agent-1",
            WorkspaceTeamId = "T123",
        };
    }
}
