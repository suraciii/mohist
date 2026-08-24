using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackChannelLaunchServiceTests
{
    [Fact]
    public void PreMintSlackLaunchIds_are_deterministic_and_project_scoped()
    {
        var identity = new SlackMessageIdentity("T123", "C-channel", "1710000000.000100");

        var first = SlackChannelLaunchService.PreMintSlackLaunchIds("project-a", identity);
        var replay = SlackChannelLaunchService.PreMintSlackLaunchIds("project-a", identity);
        var otherProject = SlackChannelLaunchService.PreMintSlackLaunchIds("project-b", identity);

        Assert.Equal(first, replay);
        Assert.NotEqual(first.SessionId, otherProject.SessionId);
        Assert.NotEqual(first.InputId, otherProject.InputId);
        Assert.NotEqual(first.TurnId, otherProject.TurnId);
    }

    [Fact]
    public void Selection_expiry_detects_only_payloads_with_selection_actions()
    {
        var selectionPayload = JsonSerializer.Serialize(new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            Text: "choose",
            Blocks: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    type = "actions",
                    elements = new[]
                    {
                        new { type = "button", action_id = SlackSelectionActionPayload.ActionId, value = "signed" },
                    },
                },
            })));
        var fallbackPayload = JsonSerializer.Serialize(new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            Text: "re-mention one Bot"));
        var unrelatedActionPayload = JsonSerializer.Serialize(new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            Text: "stop",
            Blocks: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    type = "actions",
                    elements = new[]
                    {
                        new { type = "button", action_id = "mohist_stop", value = "signed" },
                    },
                },
            })));

        Assert.True(SlackAgentSelectionObligationWorker.HasSelectionAction(selectionPayload));
        Assert.False(SlackAgentSelectionObligationWorker.HasSelectionAction(fallbackPayload));
        Assert.False(SlackAgentSelectionObligationWorker.HasSelectionAction(unrelatedActionPayload));
    }

    [Fact]
    public void Thread_launch_bound_race_is_not_a_success_without_followup_dispatch()
    {
        var bound = new SlackChannelLaunchResult("bound", BoundSessionId: "session-bound");

        Assert.Equal("session-bound", bound.BoundSessionId);
        Assert.NotEqual("accepted", bound.Kind);
        Assert.NotEqual("queued", bound.Kind);
    }

    [Fact]
    public void Followup_target_must_resolve_to_the_selected_agent()
    {
        var selected = new AgentConnection { AgentId = "agent-selected" };
        var matching = new CanonicalFollowupTarget(
            "runner", "session", "agent-launch", null, null, "runtime", "runtime-session", "/tmp",
            ProjectId: "project", AgentId: "agent-selected", ConnectionId: "connection-selected");
        selected.Id = "connection-selected";
        var wrongAgent = matching with { AgentId = "agent-other" };
        var wrongConnection = matching with { ConnectionId = "connection-other" };

        Assert.True(SlackAgentSelectionService.IsSelectedSessionTarget(matching, selected));
        Assert.False(SlackAgentSelectionService.IsSelectedSessionTarget(wrongAgent, selected));
        Assert.False(SlackAgentSelectionService.IsSelectedSessionTarget(wrongConnection, selected));
        Assert.False(SlackAgentSelectionService.IsSelectedSessionTarget(null, selected));
    }
}
