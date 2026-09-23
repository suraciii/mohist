using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public Task<AgentSessionFollowupDispatch?> BeginNextFollowupDispatchAsync() =>
        BeginFollowupDispatchAsync(targetTurnId: null);

    public Task<AgentSessionFollowupDispatch?> BeginFollowupDispatchForTurnAsync(string turnId) =>
        string.IsNullOrWhiteSpace(turnId)
            ? Task.FromResult<AgentSessionFollowupDispatch?>(null)
            : BeginFollowupDispatchAsync(turnId);

    private async Task<AgentSessionFollowupDispatch?> BeginFollowupDispatchAsync(string? targetTurnId)
    {
        var session = await GetRequiredAsync();
        // One shared local-order rule: the earliest current queued Turn -
        // Job-owned or ordinary - is the only Turn that may advance, and a
        // merely later queued Turn never vetoes it. A Job-owned head is
        // dispatched by its own Job path, so it holds this dispatcher back;
        // a targeted retry that names a non-head Turn dispatches nothing and
        // leaves the ordinary scheduler to select the queue in order.
        var turn = AgentSessionLocalOrder.FirstDeliverableTurn(session, AgentCapacityFacts.IsManagerRecovery);
        if (turn is null
            || !string.IsNullOrEmpty(turn.JobId)
            || targetTurnId is not null
                && !string.Equals(turn.Id, targetTurnId, StringComparison.Ordinal))
            return null;
        var leases = GetPendingFollowups(session).ToList();
        var index = leases.FindIndex(lease => string.Equals(lease.TurnId, turn.Id, StringComparison.Ordinal));
        if (index < 0 || leases[index].Dispatching) return null;

        var lease = leases[index];
        var inputs = (session.Status.Inputs ?? []).ToDictionary(input => input.Id, StringComparer.Ordinal);
        if (turn.InputIds.Count == 0)
            throw new InvalidOperationException($"AgentSession {SessionId} follow-up turn '{turn.Id}' has no inputs.");

        var turnInputs = turn.InputIds.Select(id => inputs.TryGetValue(id, out var input)
                ? input
                : throw new InvalidOperationException(
                    $"AgentSession {SessionId} follow-up turn '{turn.Id}' references missing input '{id}'."))
            .ToArray();
        var representative = turnInputs[0];
        var executionSource = EffectiveExecutionSource(representative);
        if (turnInputs.Any(input => EffectiveExecutionSource(input) != executionSource))
            throw new InvalidOperationException($"AgentSession {SessionId} follow-up turn '{turn.Id}' mixes execution sources.");
        var provenance = ResolveFollowupProvenance(session, turnInputs, executionSource);
        var texts = turnInputs.Select(input => input.Text).ToArray();
        var attachments = CollectAttachmentsForDispatch(inputs, turn.InputIds);

        // The derived capacity store is the admission authority for a queued
        // follow-up: inside this same serialized turn the Session flushes its
        // pending obligations, claims the Turn against the exact persisted
        // document, and installs the complete committed Session before anything
        // is saved, timed, or dispatched. No claim means no dispatch, and a
        // claimed queued head is still dispatched here without re-entering the
        // cross-Session queue.
        if (!await ClaimQueuedFollowupTurnAsync(session, turn.Id))
            return null;

        var claimed = _session
            ?? throw new InvalidOperationException($"AgentSession {SessionId} lost its state while claiming turn '{turn.Id}'.");
        leases = GetPendingFollowups(claimed).ToList();
        index = leases.FindIndex(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
        if (index < 0)
            return null;
        lease = leases[index];

        leases[index] = lease with
        {
            Dispatching = true,
            PayloadSealed = true,
        };
        SetPendingFollowups(claimed, leases);
        await CommitAsync(claimed, []);
        return new AgentSessionFollowupDispatch(
            turn.Id,
            leases[index].OperationId,
            texts,
            attachments,
            representative.Id,
            provenance,
            leases[index].ConcurrencyDispatchId ?? $"followup:{claimed.Id}:{leases[index].OperationId}",
            executionSource,
            claimed.Metadata?.Label(AgentSessionQueryMetadataKeys.OriginMarker));
    }

    private static string EffectiveExecutionSource(AgentSessionInputRecord input)
    {
        if (input.Provenance is { } provenance
            && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal))
            return AgentExecutionSources.Slack;
        if (string.Equals(input.ExecutionSource, AgentExecutionSources.NonSlack, StringComparison.Ordinal))
            return AgentExecutionSources.NonSlack;
        if (string.Equals(input.ExecutionSource, AgentExecutionSources.Slack, StringComparison.Ordinal))
            throw new InvalidOperationException($"Slack execution input '{input.Id}' is missing Slack provenance.");
        throw new InvalidOperationException($"Input '{input.Id}' has unknown execution source '{input.ExecutionSource}'.");
    }

    private static AgentSessionInputProvenance? ResolveFollowupProvenance(
        AgentSession session,
        IReadOnlyList<AgentSessionInputRecord> turnInputs,
        string executionSource)
    {
        if (!string.Equals(executionSource, AgentExecutionSources.Slack, StringComparison.Ordinal))
            return turnInputs[0].Provenance;

        var representative = turnInputs[0].Provenance
            ?? throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up representative has no provenance.");
        if (string.IsNullOrWhiteSpace(representative.WorkspaceId)
            || string.IsNullOrWhiteSpace(representative.ConversationId)
            || string.IsNullOrWhiteSpace(representative.MessageId)
            || string.IsNullOrWhiteSpace(representative.MemberId)
            || string.IsNullOrWhiteSpace(representative.ConnectionId))
            throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up representative provenance is incomplete.");

        // Channel-thread replies inherit the session's durable bound root so
        // every delivery lands in the bound thread. DM inputs carry no channel
        // binding: anchor each follow-up to its own triggering message so its
        // Session card and independent Agent reply do not drift into the
        // session's original message thread.
        if (string.IsNullOrWhiteSpace(representative.ThreadId))
            return representative with { BoundThreadRootMessageId = representative.MessageId };

        if (string.IsNullOrWhiteSpace(representative.BoundThreadRootMessageId))
            throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up has no durable bound thread root.");
        return representative;
    }
}
