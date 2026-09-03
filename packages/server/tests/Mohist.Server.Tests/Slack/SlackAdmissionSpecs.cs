using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Tests.Support;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackAdmissionSpecs
{
    [Fact]
    public void DispatchRef_is_bounded_and_changes_with_connection_or_event_identity()
    {
        var first = new AgentConnection { Id = "connection-a" };
        var second = new AgentConnection { Id = "connection-b" };
        var identity = new SlackMessageIdentity("T1", "D1", "1710000000.000001");

        var firstRef = SlackAdmissionService.DispatchRef(first, identity);

        Assert.StartsWith("slack-admission-nudge:", firstRef, StringComparison.Ordinal);
        Assert.True(firstRef.Length <= 256);
        Assert.NotEqual(firstRef, SlackAdmissionService.DispatchRef(second, identity));
        Assert.NotEqual(firstRef, SlackAdmissionService.DispatchRef(first, identity with { MessageTs = "1710000000.000002" }));
    }

    [Fact]
    public async Task Unconfigured_agent_persists_one_server_owned_nudge_and_redelivery_reuses_it()
    {
        await using var context = SlackAdmissionTestFactory.Create();
        var connection = context.Connection(ConnectionHealthKind.Healthy);
        var identity = new SlackMessageIdentity("T1", "D1", "1710000000.000001");

        var first = await context.Service.AdmitNewWorkAsync(
            context.ProjectId, connection, context.Agent(configured: false), identity, null);
        var replay = await context.Service.FindExistingNudgeAsync(
            context.ProjectId, connection, identity);

        Assert.False(first.Admitted);
        Assert.Equal("agent_not_configured", first.Kind);
        Assert.Equal(SlackIngressResponseOwners.Server, first.ResponseOwner);
        Assert.NotNull(replay);
        Assert.Equal(first.Reason, replay!.Reason);
        await using var db = context.Factory.CreateDbContext();
        var rows = await db.SlackOutboxRows
            .Where(row => row.DispatchRef == SlackAdmissionService.DispatchRef(connection, identity))
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("D1", rows[0].ConversationId);
        Assert.Null(rows[0].ThreadTs);
    }

    [Fact]
    public void Caller_messages_are_fixed_safe_summaries()
    {
        var messages = new[]
        {
            SlackAdmissionMessages.AgentNotReady,
            SlackAdmissionMessages.ConnectionUnavailable,
            SlackAdmissionMessages.Backpressured,
        };

        foreach (var message in messages)
        {
            Assert.DoesNotContain("xoxb-", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("xapp-", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mo ", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HealthReason", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
