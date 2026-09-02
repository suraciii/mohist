using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Signed value carried by every chooser button. Candidate ordering is part
/// of the signed canonical form; changing a pair or its position invalidates
/// the whole action.
/// </summary>
public sealed record SlackSelectionActionPayload(
    string Version,
    string Action,
    string ProjectId,
    string ConnectionId,
    string WorkspaceTeamId,
    string ConversationId,
    string OriginalMessageTs,
    string? ThreadTs,
    string AmbiguityKind,
    string ActorSlackUserId,
    IReadOnlyList<SlackSelectionCandidateReference> CandidateReferences,
    string ChosenProjectId,
    string ChosenConnectionId,
    string Nonce,
    DateTimeOffset ExpiresAt,
    string? Signature)
{
    public const string ActionId = "mohist_select_agent";
    public const string ActionName = "select_agent";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static string Canonical(SlackSelectionActionPayload payload)
    {
        var candidates = string.Join("|", payload.CandidateReferences.Select(candidate =>
            $"{candidate.ProjectId.Length}:{candidate.ProjectId}{candidate.ConnectionId.Length}:{candidate.ConnectionId}"));
        return string.Join("\n",
            payload.Version,
            payload.Action,
            payload.ProjectId,
            payload.ConnectionId,
            payload.WorkspaceTeamId,
            payload.ConversationId,
            payload.OriginalMessageTs,
            payload.ThreadTs ?? string.Empty,
            payload.AmbiguityKind,
            payload.ActorSlackUserId,
            candidates,
            payload.ChosenProjectId,
            payload.ChosenConnectionId,
            payload.Nonce,
            payload.ExpiresAt.ToUnixTimeMilliseconds());
    }

    public static bool IsStructurallyValid(SlackSelectionActionPayload? payload) =>
        payload is not null
        && payload.Version == "v1"
        && payload.Action == ActionName
        && !string.IsNullOrWhiteSpace(payload.ProjectId)
        && !string.IsNullOrWhiteSpace(payload.ConnectionId)
        && !string.IsNullOrWhiteSpace(payload.WorkspaceTeamId)
        && !string.IsNullOrWhiteSpace(payload.ConversationId)
        && !string.IsNullOrWhiteSpace(payload.OriginalMessageTs)
        && !string.IsNullOrWhiteSpace(payload.AmbiguityKind)
        && !string.IsNullOrWhiteSpace(payload.ActorSlackUserId)
        && !string.IsNullOrWhiteSpace(payload.Nonce)
        && !string.IsNullOrWhiteSpace(payload.ChosenProjectId)
        && !string.IsNullOrWhiteSpace(payload.ChosenConnectionId)
        && payload.CandidateReferences is { Count: >= 2 }
        && payload.CandidateReferences.All(candidate =>
            candidate is not null
            && !string.IsNullOrWhiteSpace(candidate.ProjectId)
            && !string.IsNullOrWhiteSpace(candidate.ConnectionId))
        && !string.IsNullOrWhiteSpace(payload.Signature);
}

internal static class SlackSelectionChooserRenderer
{
    public static async Task<JsonElement?> BuildBlocksAsync(
        ISlackActionSigner signer,
        AgentConnection postingConnection,
        string workspaceTeamId,
        string conversationId,
        string originalMessageTs,
        string? threadTs,
        string actorSlackUserId,
        string ambiguityKind,
        IReadOnlyList<SlackSelectionCandidateReference> candidates,
        IReadOnlyList<string> labels,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        if (candidates.Count is < 2 or > 5
            || candidates.Count != labels.Count
            || candidates.Any(candidate => candidate is null))
            return null;

        var buttons = new List<object>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var unsigned = new SlackSelectionActionPayload(
                "v1",
                SlackSelectionActionPayload.ActionName,
                postingConnection.ProjectId,
                postingConnection.Id,
                workspaceTeamId,
                conversationId,
                originalMessageTs,
                threadTs,
                ambiguityKind,
                actorSlackUserId,
                candidates,
                candidate.ProjectId,
                candidate.ConnectionId,
                Guid.NewGuid().ToString("N"),
                expiresAt,
                null);
            var signature = await signer.TrySignAsync(
                postingConnection,
                SlackSelectionActionPayload.Canonical(unsigned),
                ct);
            if (signature is null)
                return null;
            var value = JSON.Serialize(unsigned with { Signature = signature });
            buttons.Add(new
            {
                type = "button",
                text = new { type = "plain_text", text = labels[index] },
                action_id = SlackSelectionActionPayload.ActionId,
                value,
            });
        }

        return JSON.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                block_id = "mohist-agent-selection",
                elements = buttons,
            },
        });
    }
}
