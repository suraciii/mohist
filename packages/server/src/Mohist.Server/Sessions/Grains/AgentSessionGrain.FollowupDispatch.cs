using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<AgentSessionFollowupDispatch?> BeginNextFollowupDispatchAsync()
    {
        var session = await GetRequiredAsync();
        var turns = session.Status.Turns ?? [];
        if (turns.Any(turn => turn.Status == AgentTurnStatus.Executing)) return null;
        var leases = GetPendingFollowups(session).ToList();
        var turn = turns.FirstOrDefault(turn => string.IsNullOrEmpty(turn.JobId) && turn.Status == AgentTurnStatus.Queued);
        if (turn is null) return null;
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

        if (!await AcquireFollowupDispatchPermitAsync(session, lease))
            return null;

        leases = GetPendingFollowups(session).ToList();
        index = leases.FindIndex(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
        if (index < 0)
            return null;
        lease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal)) ?? lease;

        leases[index] = lease with
        {
            Dispatching = true,
            PayloadSealed = true,
            ConcurrencyGateStatus = lease.ConcurrencyPermitId is null ? lease.ConcurrencyGateStatus : "dispatch-pending",
        };
        SetPendingFollowups(session, leases);
        await CommitAsync(session, []);
        if (leases[index].ConcurrencyPermitId is not null
            && leases[index].ConcurrencyToken is not null
            && leases[index].ConcurrencyAgentId is not null
            && leases[index].ConcurrencyDispatchId is not null)
        {
            var dispatchProjectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
            var dispatchAgentId = leases[index].ConcurrencyAgentId;
            var dispatchToken = leases[index].ConcurrencyToken;
            var dispatchPermitId = leases[index].ConcurrencyPermitId;
            var dispatchId = leases[index].ConcurrencyDispatchId;
            if (string.IsNullOrWhiteSpace(dispatchProjectId)
                || string.IsNullOrWhiteSpace(dispatchAgentId)
                || string.IsNullOrWhiteSpace(dispatchToken)
                || string.IsNullOrWhiteSpace(dispatchPermitId)
                || string.IsNullOrWhiteSpace(dispatchId))
                return null;
            await _grains
                .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(dispatchProjectId, dispatchAgentId))
                .MarkDispatchedAsync(
                    dispatchProjectId,
                    dispatchAgentId,
                    dispatchToken,
                    dispatchPermitId,
                    dispatchId);
            leases[index] = leases[index] with { ConcurrencyGateStatus = "dispatched" };
            SetPendingFollowups(session, leases);
            await CommitAsync(session, []);
        }
        return new AgentSessionFollowupDispatch(
            turn.Id,
            leases[index].OperationId,
            texts,
            attachments,
            representative.Id,
            provenance,
            leases[index].ConcurrencyDispatchId ?? $"followup:{session.Id}:{leases[index].OperationId}",
            executionSource);
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

        var initial = (session.Status.Inputs ?? [])
            .Where(input => EffectiveExecutionSource(input) == AgentExecutionSources.Slack)
            .OrderBy(input => input.Sequence)
            .FirstOrDefault(input => !string.IsNullOrWhiteSpace(input.JobId));
        var initialProvenance = initial?.Provenance;
        var root = initialProvenance?.BoundThreadRootMessageId;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up has no durable bound thread root.");

        var representative = turnInputs[0].Provenance
            ?? throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up representative has no provenance.");
        if (string.IsNullOrWhiteSpace(representative.WorkspaceId)
            || string.IsNullOrWhiteSpace(representative.ConversationId)
            || string.IsNullOrWhiteSpace(representative.MessageId)
            || string.IsNullOrWhiteSpace(representative.MemberId)
            || string.IsNullOrWhiteSpace(representative.ConnectionId))
            throw new InvalidOperationException($"AgentSession {session.Id} Slack follow-up representative provenance is incomplete.");

        return representative with { BoundThreadRootMessageId = root };
    }
}
