using Mohist.Server.Api;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Runner.Api;

public sealed class ManagerExecutionPollGrantTests
{
    [Fact]
    public async Task Manager_dispatch_gets_a_response_only_grant_without_mutating_dispatch_shape()
    {
        var context = SlackExecutionContextFactory.Create(
            "T_WORKSPACE",
            "D_MANAGER",
            "1710000000.000001",
            "1710000000.000002",
            "U_ACTOR",
            "enrollment-1",
            "session-1",
            "slack:session-1:input-1",
            SlackDeliveryOwnerIds.ManagerProjectId,
            SlackDeliveryOwnerKinds.Manager);
        var dispatch = new WorkDispatch(
            string.Empty,
            "work-1",
            WorkType: "agent-job",
            With: JSON.Serialize(new { slackExecutionContext = context }),
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: "job-1",
            ProjectId: SlackDeliveryOwnerIds.ManagerProjectId,
            AgentSessionId: "session-1");
        var issuer = new ManagerExecutionCapabilityIssuer(
            new ManagerExecutionLeaseStore(),
            new ManagerDeploymentEpoch());

        var response = await RunnerRoutes.ToWorkDispatchResponseAsync(dispatch, (_, _) => Task.FromResult<ParentIssueContext?>(null), issuer);

        Assert.NotNull(response.ManagerExecutionGrant);
        Assert.DoesNotContain(response.ManagerExecutionGrant!.ManagementCredential, response.With, StringComparison.Ordinal);
        Assert.DoesNotContain(response.ManagerExecutionGrant.ReplyCredential, response.With, StringComparison.Ordinal);
        Assert.Equal(dispatch.With, response.With);
        Assert.Equal(2, issuer.RevokeExecution(response.ManagerExecutionGrant.ExecutionId));
    }
}
