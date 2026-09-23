using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

// Follow-up acceptance and the follow-up Turn lifecycle, extracted from
// AgentSession.Transitions so that partial stays within the file-size ratchet.
public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        /// <summary>
        /// Synchronous follow-up accept transition. Persists a new
        /// <see cref="AgentSessionInputRecord"/> (no JobId), assigns
        /// it to an <see cref="AgentTurnRecord"/> per the turn-
        /// assignment rule (idle/queued-turn joins, executing-turn
        /// creates a new queued turn), and records an accepted
        /// <see cref="AgentSessionFollowupLease"/> carrying the input
        /// and turn ids. The transition is the source of truth for
        /// three-valued follow-up availability: persistence is
        /// synchronous so the caller can rely on the returned
        /// <see cref="AgentSessionFollowupAcceptResult"/> identity
        /// before dispatching to the runner.
        /// </summary>
        /// <param name="text">
        /// Follow-up text. May be empty when the input carries at
        /// least one accepted attachment — the spec's
        /// "non-empty text OR at least one accepted attachment"
        /// constraint is enforced here. Validation already rejects
        /// inputs with neither text nor attachments upstream of the
        /// grain.
        /// </param>
        /// <param name="attachments">
        /// Ordered attachment child record carried by the accepted
        /// input. Stored alongside the text so the accepted set
        /// survives a grain reload and is queryable via the input
        /// surface. Replays with the same idempotency key must
        /// supply an equivalent (id + name + content-type + size)
        /// ordered set; a mismatch raises a conflict.
        /// </param>
        public AgentSessionFollowupAcceptResult AcceptFollowup(
            string inputId,
            string turnId,
            string operationId,
            string text,
            string source,
            string idempotencyKey,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null,
            bool forceNewTurn = false)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation id is required.", nameof(operationId));
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Source is required.", nameof(source));

            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);
            var hasText = !string.IsNullOrEmpty(text);
            var hasAttachments = normalizedAttachments is { Count: > 0 };
            if (!hasText && !hasAttachments)
            {
                throw new ArgumentException(
                    "Follow-up input requires non-empty text or at least one accepted attachment.",
                    nameof(text));
            }

            var inputs = (session.Status.Inputs ?? []).ToList();
            var turns = (session.Status.Turns ?? []).ToList();
            var leases = (session.Status.PendingFollowups ?? []).ToList();

            var existing = inputs
                .Where(candidate => candidate.JobId is null
                    && string.Equals(candidate.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                .FirstOrDefault();

            if (existing is not null)
            {
                // Idempotent retry: cannot mutate an already-accepted
                // input's identity (text, source, attachment set must match).
                var expectedText = hasText ? text : string.Empty;
                var existingText = existing.Text ?? string.Empty;
                if (!string.Equals(existingText, expectedText, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already accepts idempotency key '{idempotencyKey}' with different content.");
                }

                var existingTurn = turns.FirstOrDefault(candidate =>
                    candidate.InputIds.Contains(existing.Id, StringComparer.Ordinal));
                if (existingTurn is null)
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} accepted input '{existing.Id}' has no assigned turn.");
                }

                var turnStillQueued = existingTurn.Status == AgentTurnStatus.Queued;
                var existingLease = leases.FirstOrDefault(candidate =>
                    string.Equals(candidate.TurnId, existingTurn.Id, StringComparison.Ordinal));
                return new AgentSessionFollowupAcceptResult(
                    InputId: existing.Id,
                    TurnId: existingTurn.Id,
                    OperationId: existingLease?.OperationId ?? operationId,
                    AlreadyAccepted: true,
                    ShouldRedeliver: turnStillQueued,
                    InputAcceptance: existing.Acceptance,
                    TurnStatus: existingTurn.Status, FailureCategory: existingTurn.Result?.FailureCategory);
            }

            var executionSource = ExecutionSourceFor(provenance);
            var candidateTurn = forceNewTurn
                ? null
                : ChooseFollowupTurnForAssignment(
                    turns,
                    leases,
                    inputs,
                    hasAttachments,
                    executionSource);

            var newInput = new AgentSessionInputRecord(
                Id: inputId,
                Sequence: inputs.Count + 1,
                Text: text ?? string.Empty,
                Source: source,
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now,
                JobId: null,
                IdempotencyKey: idempotencyKey,
                Attachments: normalizedAttachments,
                Provenance: provenance,
                ExecutionSource: executionSource,
                ContextGeneration: session.Status.ContextGeneration);

            AgentTurnRecord updatedTurn;
            var createdNewTurn = false;
            if (candidateTurn is null)
            {
                updatedTurn = new AgentTurnRecord(
                    Id: turnId,
                    Sequence: turns.Count + 1,
                    InputIds: new[] { inputId },
                    Status: AgentTurnStatus.Queued,
                    JobId: null,
                    Result: null,
                    RecordedAt: now,
                    UpdatedAt: now,
                    ContextGeneration: session.Status.ContextGeneration);
                createdNewTurn = true;
            }
            else
            {
                var inputIds = candidateTurn.InputIds.ToList();
                inputIds.Add(inputId);
                updatedTurn = candidateTurn with
                {
                    InputIds = inputIds,
                    UpdatedAt = now,
                };
            }

            inputs.Add(newInput);

            var turnOperationId = operationId;
            if (createdNewTurn)
            {
                leases.Add(new AgentSessionFollowupLease(
                    OperationId: operationId,
                    RuntimeSessionId: session.Status.AgentRuntimeSessionId ?? string.Empty,
                    Accepted: true,
                    AcceptedAt: now,
                    StartedAt: now,
                    InputId: inputId,
                    TurnId: updatedTurn.Id));
            }
            else
            {
                turnOperationId = leases.First(candidate =>
                    string.Equals(candidate.TurnId, updatedTurn.Id, StringComparison.Ordinal)).OperationId;
            }
            updatedTurn = updatedTurn with { OperationId = turnOperationId };
            var turnIndex = turns.FindIndex(candidate => candidate.Id == updatedTurn.Id);
            if (turnIndex < 0) turns.Add(updatedTurn);
            else turns[turnIndex] = updatedTurn;

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                PendingFollowup = null,
                PendingFollowups = leases,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
            };

            return new AgentSessionFollowupAcceptResult(
                InputId: inputId,
                TurnId: updatedTurn.Id,
                OperationId: turnOperationId,
                AlreadyAccepted: false,
                ShouldRedeliver: true,
                InputAcceptance: newInput.Acceptance,
                TurnStatus: updatedTurn.Status,
                Attachments: normalizedAttachments);
        }

        /// <summary>
        /// Mark the follow-up turn linked to the supplied
        /// operationId as <see cref="AgentTurnStatus.Executing"/>.
        /// No-op when no matching turn is found, when the turn is
        /// already past executing, or when the lease is missing.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkFollowupTurnExecuting(
            string operationId,
            DateTime now)
        {
            var leases = session.Status.PendingFollowups ?? [];
            var lease = leases.FirstOrDefault(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            if (lease is null || string.IsNullOrEmpty(lease.TurnId))
                return [];

            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal));
            if (index < 0)
                return [];

            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled)
            {
                return [];
            }

            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Executing,
                UpdatedAt = now,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                Activity = session.Status.Activity == AgentSessionActivity.Unknown
                    ? AgentSessionActivity.Active
                    : session.Status.Activity,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
                IdleSince = session.Status.Activity == AgentSessionActivity.Unknown
                    ? null
                    : session.Status.IdleSince,
            };
            return [];
        }

        /// <summary>
        /// Apply a terminal status to the follow-up turn linked to
        /// the supplied operationId. The turn moves to Completed,
        /// Failed, Unknown, or Cancelled; the matching lease is
        /// cleared as part of the same transition so the per-turn
        /// lease count drops to reflect the turn's terminal state.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkFollowupTurnTerminal(
            string operationId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            var leases = (session.Status.PendingFollowups ?? []).ToList();
            var leaseIndex = leases.FindIndex(candidate =>
                string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
            if (leaseIndex < 0)
                return [];

            var lease = leases[leaseIndex];
            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = string.IsNullOrEmpty(lease.TurnId)
                ? -1
                : turns.FindIndex(candidate =>
                    string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal));

            if (turnIndex >= 0)
            {
                var turn = turns[turnIndex];
                if (turn.Status is not (AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled))
                {
                    turns[turnIndex] = turn with
                    {
                        Status = status,
                        Result = result,
                        UpdatedAt = now,
                        OperationId = operationId,
                    };
                }
            }

            var remainingLeases = leases
                .Where((candidate, index) => index != leaseIndex)
                .ToArray();
            var remainingFollowupTurns = turns.Count(turn => IsNonTerminalFollowupTurn(session, turn));

            session.Status = session.Status with
            {
                Turns = turnIndex >= 0 ? turns : session.Status.Turns,
                PendingFollowup = remainingLeases.Length == 0 ? null : session.Status.PendingFollowup,
                PendingFollowups = remainingLeases,
                LastDataAt = now,
                Activity = status switch
                {
                    AgentTurnStatus.Unknown => AgentSessionActivity.Unknown,
                    _ => remainingFollowupTurns == 0
                        ? AgentSessionActivity.Idle
                        : session.Status.Activity,
                },
                IdleSince = status == AgentTurnStatus.Unknown
                    ? null
                    : remainingFollowupTurns == 0
                        ? now
                        : session.Status.IdleSince,
                CurrentTurnEndedAt = status is AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled
                    or AgentTurnStatus.Unknown
                    ? now
                    : session.Status.CurrentTurnEndedAt,
                ConfirmedExecutionOwnership = session.Status.ConfirmedExecutionOwnership is { } ownership
                    && !string.IsNullOrWhiteSpace(lease.TurnId)
                    && ownership.TurnIds.Contains(lease.TurnId, StringComparer.Ordinal)
                    ? null
                    : session.Status.ConfirmedExecutionOwnership,
            };
            return [];
        }
    }
}
