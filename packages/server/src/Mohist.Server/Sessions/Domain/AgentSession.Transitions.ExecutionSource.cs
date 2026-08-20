using Mohist.Server.Contracts;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    private static string ExecutionSourceFor(AgentSessionInputProvenance? provenance) =>
        provenance is not null
            && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            ? AgentExecutionSources.Slack
            : AgentExecutionSources.NonSlack;

    private static string EffectiveExecutionSource(AgentSessionInputRecord input) =>
        input.Provenance is { } provenance
            && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            ? AgentExecutionSources.Slack
            : input.ExecutionSource;

    /// <summary>
    /// Resolve the follow-up turn an incoming input should be
    /// assigned to. Returns the existing queued turn whose delivery
    /// payload has not been claimed (joins the new input in submission
    /// order), or <c>null</c> to signal that the caller must create a
    /// new queued turn. A dispatching or executing turn does NOT match.
    /// Inputs from a different execution source also start a new turn,
    /// because one dispatch must carry one valid source/context pair.
    /// </summary>
    private static AgentTurnRecord? ChooseFollowupTurnForAssignment(
        IReadOnlyList<AgentTurnRecord> turns,
        IReadOnlyList<AgentSessionFollowupLease> leases,
        IReadOnlyList<AgentSessionInputRecord> inputs,
        bool incomingHasAttachments,
        string incomingExecutionSource)
    {
        if (incomingHasAttachments)
            return null;

        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var candidate = turns[i];
            if (!string.IsNullOrEmpty(candidate.JobId))
                continue;
            if (candidate.Status != AgentTurnStatus.Queued)
                continue;
            if (leases.Any(lease => string.Equals(lease.TurnId, candidate.Id, StringComparison.Ordinal)
                && lease.PayloadSealed))
                continue;
            if (candidate.InputIds.Any(inputId => inputs.Any(input => input.Id == inputId
                && input.Attachments is { Count: > 0 })))
                continue;
            if (!candidate.InputIds.All(inputId => inputs.FirstOrDefault(input => input.Id == inputId) is { } input
                && string.Equals(EffectiveExecutionSource(input), incomingExecutionSource, StringComparison.Ordinal)))
                continue;
            return candidate;
        }
        return null;
    }
}
