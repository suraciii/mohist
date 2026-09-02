using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Slack;

/// <summary>
/// L0 contract for the deterministic selection kernel. Application routing
/// remains covered by the two retained L1 chooser route Specs; this matrix
/// owns the immutable candidate and lineage decisions used by that route.
/// </summary>
public sealed class SlackAgentSelectionSpecs
{
    [Fact]
    public void Candidate_snapshot_requires_the_same_ordered_project_and_connection_pairs()
    {
        var candidates = new[]
        {
            new SlackSelectionCandidateReference("project-a", "connection-a", "U_A"),
            new SlackSelectionCandidateReference("project-b", "connection-b", "U_B"),
        };
        var durable = JSON.Serialize(candidates);

        Assert.True(SlackAgentSelectionPolicy.CandidateSnapshotsEqual(durable, candidates));
        Assert.False(SlackAgentSelectionPolicy.CandidateSnapshotsEqual(durable, candidates.Reverse().ToArray()));
        Assert.False(SlackAgentSelectionPolicy.CandidateSnapshotsEqual(
            durable,
            [candidates[0], candidates[1] with { BotUserId = "U_CHANGED" }]));
    }

    [Fact]
    public void Candidate_snapshot_contains_only_the_exact_cross_project_choice()
    {
        var durable = JSON.Serialize(new[]
        {
            new SlackSelectionCandidateReference("project-a", "connection-a", "U_A"),
            new SlackSelectionCandidateReference("project-b", "connection-b", "U_B"),
        });

        Assert.True(SlackAgentSelectionPolicy.CandidateSnapshotsContain(
            durable, "project-b", "connection-b"));
        Assert.False(SlackAgentSelectionPolicy.CandidateSnapshotsContain(
            durable, "project-a", "connection-b"));
        Assert.False(SlackAgentSelectionPolicy.CandidateSnapshotsContain(
            "not-json", "project-a", "connection-a"));
    }

    [Fact]
    public void Selected_candidate_rejects_workspace_binding_or_bot_identity_drift()
    {
        var selected = new AgentConnection
        {
            Id = "connection-b",
            ProjectId = "project-b",
            AppId = "A_B",
            BotUserId = "U_B",
            WorkspaceTeamId = "T123",
        };
        var candidate = new SlackSelectionCandidateReference("project-b", "connection-b", "U_B");

        Assert.True(SlackAgentSelectionPolicy.MatchesSelectedCandidate(selected, candidate, "T123"));
        Assert.False(SlackAgentSelectionPolicy.MatchesSelectedCandidate(selected, candidate, "T_OTHER"));
        Assert.False(SlackAgentSelectionPolicy.MatchesSelectedCandidate(
            selected, candidate with { BotUserId = "U_DRIFTED" }, "T123"));
        Assert.False(SlackAgentSelectionPolicy.MatchesSelectedCandidate(
            new AgentConnection
            {
                Id = selected.Id,
                ProjectId = selected.ProjectId,
                WorkspaceTeamId = selected.WorkspaceTeamId,
                BotUserId = selected.BotUserId,
            },
            candidate,
            "T123"));
    }

    [Theory]
    [InlineData(SlackAmbiguityKinds.RootMultiMention, false, SlackSelectionDispatchKinds.RootLaunch)]
    [InlineData(SlackAmbiguityKinds.ThreadMultiMention, false, SlackSelectionDispatchKinds.ThreadLaunch)]
    [InlineData(SlackAmbiguityKinds.ThreadMultiMention, true, SlackSelectionDispatchKinds.ThreadFollowup)]
    [InlineData(SlackAmbiguityKinds.MultiBoundThreadReply, true, SlackSelectionDispatchKinds.ThreadFollowup)]
    [InlineData(SlackAmbiguityKinds.MultiBoundThreadReply, false, null)]
    public void Dispatch_kind_preserves_root_launch_and_bound_followup_lineage(
        string ambiguityKind,
        bool hasBoundSession,
        string? expected)
    {
        Assert.Equal(
            expected,
            SlackAgentSelectionPolicy.DispatchKindFor(ambiguityKind, hasBoundSession));
    }

    [Fact]
    public void Selected_session_target_requires_the_selected_agent_and_connection()
    {
        var selected = new AgentConnection { Id = "connection-b", AgentId = "agent-b" };
        var matching = new CanonicalFollowupTarget(
            "runner", "session", "source", null, null, null, null, null,
            ProjectId: "project-b", AgentId: "agent-b", ConnectionId: "connection-b");

        Assert.True(SlackAgentSelectionPolicy.IsSelectedSessionTarget(matching, selected));
        Assert.False(SlackAgentSelectionPolicy.IsSelectedSessionTarget(
            matching with { AgentId = "agent-other" }, selected));
        Assert.False(SlackAgentSelectionPolicy.IsSelectedSessionTarget(
            matching with { ConnectionId = "connection-other" }, selected));
        Assert.False(SlackAgentSelectionPolicy.IsSelectedSessionTarget(null, selected));
    }
}
