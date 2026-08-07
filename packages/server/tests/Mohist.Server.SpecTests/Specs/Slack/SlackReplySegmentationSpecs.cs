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
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackReplySegmentationSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackReplySegmentationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

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
            new { conversationId = "D-segment-long", text = body });
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
            new { conversationId = "D-segment-short", text = "Done. The task is complete." });
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
            new { conversationId = "D-segment-merge", text = part });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var second = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new { conversationId = "D-segment-merge", text = part });
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
        // Both sends survive the in-place merge and re-segmentation.
        var markerOccurrences = payload.Segments!
            .Sum(segment => segment.Split("Merge 00000:", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, markerOccurrences);
    }

    private async Task CreateDmMappingAsync(AgentConnection connection, string dmConversationId)
    {
        var now = _fixture.TimeProvider.GetUtcNow();
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
            CurrentSessionId = $"session_{Guid.NewGuid():N}",
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

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
            WorkspaceTeamId = "T123",
        };
    }
}
