using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Slack;

[Trait("level", "L0")]
public sealed class SlackChannelAdmissionNudgeSpecs
{
    [Fact]
    public async Task Unavailable_channel_root_persists_a_thread_anchored_server_owned_nudge()
    {
        await using var context = SlackAdmissionTestFactory.Create();
        var connection = context.Connection(ConnectionHealthKind.Unhealthy);
        connection.HealthReason = "Slack service is offline.";
        var identity = new SlackMessageIdentity("T1", "C-channel-unavailable", "1710000000.000001");

        var result = await context.Service.AdmitNewWorkAsync(
            context.ProjectId,
            connection,
            context.Agent(configured: true),
            identity,
            threadTs: "1710000000.000000");

        Assert.False(result.Admitted);
        Assert.Equal("connection_unavailable", result.Kind);
        Assert.Equal(SlackIngressResponseOwners.Server, result.ResponseOwner);

        await using var db = context.Factory.CreateDbContext();
        var row = await db.SlackOutboxRows.SingleAsync(item => item.ConnectionId == connection.Id);
        Assert.Equal("1710000000.000000", row.ThreadTs);
        Assert.Equal(SlackAdmissionMessages.ConnectionUnavailable, SlackDeliveryPayload.Parse(row.PayloadJson).Text);
    }

    [Fact]
    public void Bound_followup_continues_without_a_new_work_admission_decision()
    {
        var decision = SlackChannelIngressPolicy.Decide(
            currentConnectionId: "connection-1",
            ownBotUserId: "bot-1",
            senderAuthorized: true,
            accessReason: null,
            isRootMessage: false,
            hasThread: true,
            hasPrompt: true,
            hasFiles: false,
            mentionedWorkspaceBots: [],
            threadBindings: [new SlackThreadBinding("connection-1", "session-1", "root-ts")]);

        Assert.Equal(SlackChannelIngressDisposition.Continue, decision.Disposition);
    }

    [Fact]
    public async Task Concurrent_unavailable_roots_persist_one_server_owned_nudge()
    {
        await using var context = SlackAdmissionTestFactory.Create();
        var connection = context.Connection(ConnectionHealthKind.Unhealthy);
        connection.HealthReason = "Slack service is offline.";
        var identity = new SlackMessageIdentity("T1", "C-channel-concurrent", "1710000000.000002");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => context.Service.AdmitNewWorkAsync(
                context.ProjectId,
                connection,
                context.Agent(configured: true),
                identity,
                threadTs: identity.MessageTs)));

        Assert.All(results, result =>
        {
            Assert.False(result.Admitted);
            Assert.Equal("connection_unavailable", result.Kind);
            Assert.Equal(SlackIngressResponseOwners.Server, result.ResponseOwner);
        });

        await using var db = context.Factory.CreateDbContext();
        Assert.Single(await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DispatchRef == SlackAdmissionService.DispatchRef(connection, identity))
            .ToListAsync());
    }
}
