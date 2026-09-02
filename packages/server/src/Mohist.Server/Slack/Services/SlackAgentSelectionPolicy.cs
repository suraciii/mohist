using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Owns the deterministic parts of a signed Agent selection. Persistence,
/// authorization, and dispatch remain application concerns; this component
/// only compares the durable candidate snapshot and classifies its lineage.
/// </summary>
internal static class SlackAgentSelectionPolicy
{
    public static bool CandidateSnapshotsContain(
        string durableJson,
        string projectId,
        string connectionId)
    {
        try
        {
            return DeserializeCandidates(durableJson).Any(candidate =>
                candidate is not null
                && string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(candidate.ConnectionId, connectionId, StringComparison.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static bool CandidateSnapshotsEqual(
        string durableJson,
        IReadOnlyList<SlackSelectionCandidateReference> signed)
    {
        try
        {
            var durable = DeserializeCandidates(durableJson);
            return durable.All(candidate => candidate is not null)
                && signed.All(candidate => candidate is not null)
                && durable.SequenceEqual(signed);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static bool MatchesSelectedCandidate(
        AgentConnection selected,
        SlackSelectionCandidateReference candidate,
        string workspaceTeamId) =>
        candidate is not null
        && AgentConnectionStore.HasBoundIdentity(selected)
        && string.Equals(selected.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(candidate.BotUserId)
            || string.Equals(selected.BotUserId, candidate.BotUserId, StringComparison.Ordinal));

    public static string? DispatchKindFor(string ambiguityKind, bool hasBoundSession) =>
        ambiguityKind switch
        {
            SlackAmbiguityKinds.RootMultiMention => SlackSelectionDispatchKinds.RootLaunch,
            SlackAmbiguityKinds.ThreadMultiMention => hasBoundSession
                ? SlackSelectionDispatchKinds.ThreadFollowup
                : SlackSelectionDispatchKinds.ThreadLaunch,
            SlackAmbiguityKinds.MultiBoundThreadReply when hasBoundSession =>
                SlackSelectionDispatchKinds.ThreadFollowup,
            _ => null,
        };

    public static bool IsSelectedSessionTarget(
        CanonicalFollowupTarget? target,
        AgentConnection selected) =>
        target is not null
        && string.Equals(target.AgentId, selected.AgentId, StringComparison.Ordinal)
        && string.Equals(target.ConnectionId, selected.Id, StringComparison.Ordinal);

    private static IReadOnlyList<SlackSelectionCandidateReference> DeserializeCandidates(string json) =>
        JSON.Deserialize<List<SlackSelectionCandidateReference>>(json) ?? [];
}
