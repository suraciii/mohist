using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private Task PublishCanonicalRefreshAsync(AgentSession session)
    {
        var sequence = ++_realtimeSequence;
        // No physical identity: this invalidates the saved canonical snapshot,
        // not the Runtime detail stream or the persisted transcript.
        return FanOutRealtimeAsync(session,
        [
            new RuntimeEventEnvelope
            {
                Id = -sequence,
                SessionId = session.Id,
                AgentSessionId = null,
                Sequence = sequence,
                Type = RuntimeEventTypes.SessionActivity,
                PayloadJson = "{}",
                CreatedAt = Now(),
            },
        ], []);
    }

    private async Task<bool> FlushAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return true;
        var hasPendingPersistence = _stateDirty
            || _transcript.HasPending
            || session.Status.PendingTranscriptEvidence?.Count > 0;
        if (!hasPendingPersistence)
            return true;

        // A prior event-aware save on this activation failed and quarantined
        // it; do not attempt another flush from the same dirty state.
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");

        if (!await FlushPendingTranscriptEvidenceAsync(session, ct))
            return false;

        var now = Now();
        var transcript = _transcript.BuildFlush(session, now);

        if (_stateDirty)
        {
            var pendingEvents = _pendingDomainEvents.Count == 0
                ? Array.Empty<AgentSessionEvent>()
                : _pendingDomainEvents.ToArray();
            try
            {
                await _stateStore.SaveAsync(SessionId, session, pendingEvents, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save state for {SessionId}",
                    SessionId);
                // The state/event transaction rolled back, but the live
                // session and _pendingDomainEvents already absorbed the
                // runtime activity. Quarantine the activation so a later
                // command cannot persist the dirty state without the matching
                // AgentSessionEvents rows. See CommitAsync for the same defense.
                _sessionReloadRequired = true;
                DeactivateOnIdle();
                throw;
            }
            // The state/event transaction committed atomically. The domain
            // events are now durable rows; clear them so a subsequent
            // transcript-only retry cannot re-append them. Splitting the two
            // retry states means a transcript failure no longer duplicates
            // already-committed lifecycle events on the next flush.
            _pendingDomainEvents.Clear();
            _stateDirty = false;
        }

        if (transcript is not null)
        {
            try
            {
                await _transcriptStore.SaveAsync(transcript, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save transcript for {SessionId}; parts={PartCount}",
                    SessionId, transcript.Parts.Count);
                // State/events already committed; only the transcript needs
                // retry. _transcript keeps the un-committed flush, so the next
                // PersistCallback re-attempts just the transcript.
                return false;
            }
            _transcript.CommitFlush();
        }

        await PublishCanonicalRefreshAsync(session);
        return true;
    }

    private async Task FanOutRealtimeAsync(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> entries,
        IReadOnlyList<AgentSessionEvent> domainEvents)
    {
        if (entries.Count == 0) return;

        var projectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogError(
                "AgentSessionGrain cannot publish transcript events for {SessionId}: Project metadata is missing",
                session.Id);
            return;
        }

        foreach (var row in entries)
        {
            if (!TranscriptAccumulator.EventTypes.Contains(row.Type))
                continue;

            JsonElement payload;
            try
            {
                payload = JSON.DeserializeElement(row.PayloadJson);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain failed to deserialise transcript payload for {Type} on {SessionId}",
                    row.Type, session.Id);
                continue;
            }

            var envelope = new TranscriptEnvelope(
                Id: row.Id.ToString(),
                SessionId: row.SessionId,
                RuntimeSessionId: row.AgentSessionId,
                Runtime: session.Runtime.Runtime,
                Sequence: row.Sequence,
                Type: row.Type,
                Payload: payload,
                CreatedAt: row.CreatedAt.ToString("o"));

            try
            {
                await _transcriptPublisher.PublishAsync(projectId, envelope, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain transcript publish failed for {Type} on {SessionId}",
                    row.Type, session.Id);
            }
        }
    }
}
