using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task RecordManagerRecoveryTurnAsync(RecordFollowupTurnCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await GetRequiredAsync();
        await RecordManagerRecoveryTurnAsync(session, command);
    }

    private async Task RecordManagerRecoveryTurnAsync(
        AgentSession session,
        RecordFollowupTurnCommand command)
    {
        if (!string.Equals(
                session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId),
                SlackDeliveryOwnerIds.ManagerProjectId,
                StringComparison.Ordinal)
            || command.Provenance is null
            || !string.Equals(command.Provenance.ProviderKind, "slack", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.Provenance.WorkspaceId)
            || string.IsNullOrWhiteSpace(command.Provenance.ConversationId)
            || string.IsNullOrWhiteSpace(command.Provenance.BoundThreadRootMessageId)
            || string.IsNullOrWhiteSpace(command.Provenance.MemberId)
            || string.IsNullOrWhiteSpace(command.Provenance.MessageId)
            || string.IsNullOrWhiteSpace(command.Provenance.ConnectionId))
            return;

        var events = session.RecordManagerRecoveryTurn(
            inputId: command.InputId,
            turnId: command.TurnId,
            prompt: command.Prompt,
            source: command.Source,
            now: Now(),
            provenance: command.Provenance);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            _stateDirty = true;
            EnsurePersistenceTimer();
            return;
        }
        await CommitAsync(session, events);
    }

    public async Task EnsureManagerCredentialExpiryRecoveryAsync()
    {
        var session = await GetRequiredAsync();
        if (!string.Equals(
                session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId),
                SlackDeliveryOwnerIds.ManagerProjectId,
                StringComparison.Ordinal))
            return;

        var provenance = (session.Status.Inputs ?? [])
            .Where(input => !string.IsNullOrWhiteSpace(input.JobId))
            .OrderBy(input => input.Sequence)
            .Select(input => input.Provenance)
            .FirstOrDefault(candidate => candidate is not null);
        if (provenance is null
            || !string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(provenance.WorkspaceId)
            || string.IsNullOrWhiteSpace(provenance.ConversationId)
            || string.IsNullOrWhiteSpace(provenance.BoundThreadRootMessageId)
            || string.IsNullOrWhiteSpace(provenance.MemberId)
            || string.IsNullOrWhiteSpace(provenance.MessageId)
            || string.IsNullOrWhiteSpace(provenance.ConnectionId))
            return;

        await RecordManagerRecoveryTurnAsync(session, new RecordFollowupTurnCommand(
            InputId: $"manager-recovery-input:{SessionId}",
            TurnId: $"manager-recovery-turn:{SessionId}",
            Prompt: "The previous Manager execution ended before its outcome was confirmed. Inspect the current resource state before taking any action; do not repeat the interrupted operation automatically.",
            Source: "manager-recovery:manager-credential-expired",
            Provenance: provenance));
    }
}
