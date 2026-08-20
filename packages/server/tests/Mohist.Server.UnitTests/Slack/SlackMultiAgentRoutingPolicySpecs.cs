using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackMultiAgentRoutingPolicySpecs
{
    private const string CurrentConnection = "connection-a";
    private const string OtherConnection = "connection-b";
    private const string Sender = "user-1";

    [Fact]
    public void Authorized_mentioned_connection_prompts_with_all_candidates()
    {
        var decision = Decide(
            allowed: true,
            Candidate(CurrentConnection, "bot-a", owner: "owner-a"),
            Candidate(OtherConnection, "bot-b", owner: "owner-b"));

        Assert.NotNull(decision);
        Assert.Equal(SlackMultiAgentRoutingDisposition.Prompt, decision.Disposition);
        Assert.Equal([CurrentConnection, OtherConnection], decision.ConnectionIds);
        Assert.Equal(["bot-a", "bot-b"], decision.BotLabels);
    }

    [Fact]
    public void Unauthorized_mentioned_connection_rejects_when_no_candidate_claims_sender()
    {
        var decision = Decide(
            allowed: false,
            Candidate(CurrentConnection, "bot-a", owner: "owner-a"),
            Candidate(OtherConnection, "bot-b", owner: "owner-b"));

        Assert.NotNull(decision);
        Assert.Equal(SlackMultiAgentRoutingDisposition.RejectNonOwner, decision.Disposition);
    }

    [Fact]
    public void Unmentioned_connection_is_ignored_even_when_sender_is_authorized()
    {
        var decision = Decide(
            currentConnection: "connection-c",
            allowed: true,
            Candidate(CurrentConnection, "bot-a", owner: "owner-a"),
            Candidate(OtherConnection, "bot-b", owner: "owner-b"));

        Assert.NotNull(decision);
        Assert.Equal(SlackMultiAgentRoutingDisposition.Ignore, decision.Disposition);
    }

    [Fact]
    public void Non_claiming_owner_does_not_reject_another_connection()
    {
        var decision = Decide(
            allowed: false,
            Candidate(CurrentConnection, "bot-a", owner: "owner-a"),
            Candidate(OtherConnection, "bot-b", owner: Sender));

        Assert.NotNull(decision);
        Assert.Equal(SlackMultiAgentRoutingDisposition.Ignore, decision.Disposition);
    }

    [Fact]
    public void Current_owner_can_prompt_even_when_access_decision_is_authorized()
    {
        var decision = Decide(
            allowed: true,
            Candidate(CurrentConnection, "bot-a", owner: Sender),
            Candidate(OtherConnection, "bot-b", owner: "owner-b"));

        Assert.NotNull(decision);
        Assert.Equal(SlackMultiAgentRoutingDisposition.Prompt, decision.Disposition);
    }

    [Fact]
    public void Fewer_than_two_candidates_have_no_multi_agent_decision()
    {
        var decision = SlackMultiAgentRoutingPolicy.Decide(
            CurrentConnection,
            Sender,
            senderAuthorizedForCurrentConnection: true,
            [Candidate(CurrentConnection, "bot-a", owner: "owner-a")]);

        Assert.Null(decision);
    }

    private static SlackMultiAgentRoutingDecision? Decide(
        bool allowed,
        params SlackMultiAgentRoutingCandidate[] candidates) =>
        Decide(CurrentConnection, allowed, candidates);

    private static SlackMultiAgentRoutingDecision? Decide(
        string currentConnection,
        bool allowed,
        params SlackMultiAgentRoutingCandidate[] candidates) =>
        SlackMultiAgentRoutingPolicy.Decide(
            currentConnection,
            Sender,
            allowed,
            candidates);

    private static SlackMultiAgentRoutingCandidate Candidate(
        string connectionId,
        string botUserId,
        string owner) =>
        new(connectionId, botUserId, owner);
}
