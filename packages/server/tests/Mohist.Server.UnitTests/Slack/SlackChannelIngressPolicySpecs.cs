using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackChannelIngressPolicySpecs
{
    [Fact]
    public void Root_message_without_a_bot_mention_is_ignored_before_inbox_acceptance()
    {
        var decision = Decide(hasThread: false, isRootMessage: true);

        Assert.Equal(SlackChannelIngressDisposition.Ignore, decision.Disposition);
    }

    [Fact]
    public void Unmentioned_reply_to_another_connection_is_ignored()
    {
        var decision = Decide(
            hasThread: true,
            isRootMessage: false,
            bindings: [Binding("connection-b")]);

        Assert.Equal(SlackChannelIngressDisposition.Ignore, decision.Disposition);
    }

    [Fact]
    public void Unbound_thread_reply_continues_for_session_reconciliation()
    {
        var decision = Decide(hasThread: true, isRootMessage: false);

        Assert.Equal(SlackChannelIngressDisposition.Continue, decision.Disposition);
    }

    [Fact]
    public void Mentioning_another_connection_is_ignored()
    {
        var decision = Decide(mentionedBots: [Bot("connection-b", "bot-b")]);

        Assert.Equal(SlackChannelIngressDisposition.Ignore, decision.Disposition);
    }

    [Fact]
    public void Non_owner_mention_is_rejected_without_starting_work()
    {
        var decision = Decide(senderAuthorized: false, accessReason: SlackChannelIngressPolicy.NonOwnerReason, mentionedBots: [OwnBot()]);

        Assert.Equal(SlackChannelIngressDisposition.Reject, decision.Disposition);
        Assert.Equal(SlackChannelIngressPolicy.NonOwnerReason, decision.Reason);
    }

    [Fact]
    public void Empty_root_mention_is_rejected_without_starting_work()
    {
        var decision = Decide(mentionedBots: [OwnBot()]);

        Assert.Equal(SlackChannelIngressDisposition.Reject, decision.Disposition);
        Assert.Equal(SlackChannelIngressPolicy.EmptyTaskReason, decision.Reason);
    }

    [Fact]
    public void Root_mention_with_prompt_continues_to_launch()
    {
        var decision = Decide(mentionedBots: [OwnBot()], hasPrompt: true);

        Assert.Equal(SlackChannelIngressDisposition.Continue, decision.Disposition);
    }

    [Fact]
    public void Bound_followup_without_mention_continues_to_followup_route()
    {
        var decision = Decide(
            hasThread: true,
            isRootMessage: false,
            bindings: [Binding("connection-a")]);

        Assert.Equal(SlackChannelIngressDisposition.Continue, decision.Disposition);
    }

    [Theory]
    [InlineData(null, "Human")]
    [InlineData("human", "Human")]
    [InlineData("BOT", "Bot")]
    [InlineData(" unknown ", "Unknown")]
    public void NormalizeSenderKind_uses_the_adapter_sender_contract(string? rawKind, string expected)
    {
        Assert.Equal(Enum.Parse<SlackSenderKind>(expected), SlackChannelIngressPolicy.NormalizeSenderKind(rawKind));
    }

    private static SlackChannelIngressDecision Decide(
        bool senderAuthorized = true,
        string? accessReason = null,
        bool isRootMessage = true,
        bool hasThread = false,
        bool hasPrompt = false,
        bool hasFiles = false,
        IReadOnlyList<WorkspaceBoundBot>? mentionedBots = null,
        IReadOnlyList<SlackThreadBinding>? bindings = null) =>
        SlackChannelIngressPolicy.Decide(
            currentConnectionId: "connection-a",
            ownBotUserId: "bot-a",
            senderAuthorized,
            accessReason,
            isRootMessage,
            hasThread,
            hasPrompt,
            hasFiles,
            mentionedBots ?? [],
            bindings ?? []);

    private static WorkspaceBoundBot OwnBot() => Bot("connection-a", "bot-a");

    private static WorkspaceBoundBot Bot(string connectionId, string botUserId) =>
        new("project-a", connectionId, $"agent-{connectionId}", botUserId, "U_OWNER");

    private static SlackThreadBinding Binding(string connectionId) =>
        new(connectionId, $"session-{connectionId}", "root-ts");
}
