using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Slack;

public sealed class SlackAdmissionServiceTests
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
