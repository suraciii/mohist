using Mohist.Server.Contracts;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        public IReadOnlyList<AgentSessionEvent> RecoverInitialAgentJobTurn(
            AgentRuntimeBinding expected,
            AgentRuntimeBinding replacement,
            AgentInitialInputOperation operation,
            DateTime now,
            long expectedBindingEpoch)
        {
            var existing = session.Status.InitialInputOperation;
            if (existing is not null)
            {
                if (SameInitialOperation(existing, operation))
                {
                    // The same not-yet-admitted operation replays idempotently
                    // while the candidate binding still matches; an admitted
                    // effect can never be recovered again (post-submission
                    // recovery is prohibited).
                    if (existing.EffectAdmitted
                        || !Equals(session.CurrentRuntimeBinding(), replacement))
                        throw new InvalidOperationException("initial_input_operation_conflict");
                    return [];
                }
                // Parity with admission: a different receipt may be superseded
                // only after its own Turn is definitely terminal and provably
                // owned by that receipt.
                if (!PriorReceiptOwnsDefinitelyTerminalTurn(session, existing))
                    throw new InvalidOperationException("initial_input_operation_conflict");
            }

            EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            if (session.BindingEpoch != expectedBindingEpoch)
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} binding epoch changed from {expectedBindingEpoch} to {session.BindingEpoch}.");
            if (session.Status.Activity != AgentSessionActivity.Active
                || session.Status.PendingStop is { IsActive: true }
                || session.Status.PendingReset is { Outcome: null, SupersededAt: null }
                || session.Status.ConfirmedExecutionOwnership is not null
                || (session.Status.PendingFollowups?.Count ?? 0) != 0
                || session.Status.PendingFollowup is not null)
                throw new InvalidOperationException("initial_input_recovery_not_safe");

            var liveTurns = (session.Status.Turns ?? []).Where(turn =>
                turn.SupersededAt is null
                && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing or AgentTurnStatus.Unknown).ToArray();
            var turn = liveTurns.SingleOrDefault();
            var input = (session.Status.Inputs ?? []).SingleOrDefault(candidate =>
                string.Equals(candidate.Id, operation.InputId, StringComparison.Ordinal));
            if (liveTurns.Length != 1
                || turn is null
                || turn.Status != AgentTurnStatus.Queued
                || !string.Equals(turn.Id, operation.TurnId, StringComparison.Ordinal)
                || !string.Equals(turn.JobId, operation.JobId, StringComparison.Ordinal)
                || !turn.InputIds.Contains(operation.InputId, StringComparer.Ordinal)
                || input is null
                || !string.Equals(input.JobId, operation.JobId, StringComparison.Ordinal)
                || input.ContextGeneration != turn.ContextGeneration)
                throw new InvalidOperationException("initial_input_recovery_fence_mismatch");

            if (string.IsNullOrWhiteSpace(replacement.RunnerId)
                || string.IsNullOrWhiteSpace(replacement.Runtime)
                || string.IsNullOrWhiteSpace(replacement.RuntimeSessionId))
                throw new InvalidOperationException("initial_input_replacement_incomplete");

            var nextBindingEpoch = checked(session.BindingEpoch + 1);
            var nextGeneration = checked(session.Status.ContextGeneration + 1);
            session.Runtime = session.Runtime with
            {
                RunnerId = replacement.RunnerId,
                Runtime = NormalizeRuntime(replacement.Runtime),
            };
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            session.Status = session.Status with
            {
                AgentRuntimeSessionId = replacement.RuntimeSessionId,
                BoundAt = now,
                LastDataAt = now,
                ContextGeneration = nextGeneration,
                MissingRunnerFact = null,
                PendingActivityObservation = null,
                ConfirmedExecutionOwnership = null,
                UsageSummary = usage with { ContextWindowUsed = null, ContextWindowSize = null },
            };
            session.PersistedActivitySummary = (session.PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty) with
            {
                LastTerminalStatus = null,
                LatestActivity = null,
            };
            session.BindingEpoch = nextBindingEpoch;
            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate => string.Equals(candidate.Id, operation.TurnId, StringComparison.Ordinal));
            turns[index] = turns[index] with
            {
                ContextGeneration = nextGeneration,
                UpdatedAt = now,
                WorkflowExecution = turns[index].WorkflowExecution is { } workflow
                    ? workflow with
                    {
                        RunnerId = replacement.RunnerId,
                        Runtime = NormalizeRuntime(replacement.Runtime)!,
                        RuntimeSessionId = replacement.RuntimeSessionId!,
                    }
                    : null,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                InitialInputOperation = operation with
                {
                    Runtime = NormalizeRuntime(replacement.Runtime)!,
                    RuntimeSessionId = replacement.RuntimeSessionId!,
                    BindingEpoch = nextBindingEpoch,
                    ContextGeneration = nextGeneration,
                    RecordedAt = now,
                },
            };
            return [new AgentSessionRuntimeBound(replacement.RuntimeSessionId!, session.Runtime.Runtime)];
        }

        public IReadOnlyList<AgentSessionEvent> AdmitInitialAgentJobInputEffect(
            AgentInitialInputOperation operation,
            DateTime now)
        {
            var existing = session.Status.InitialInputOperation;
            if (existing is { EffectAdmitted: true })
            {
                if (SameInitialOperation(existing, operation))
                {
                    var admittedTurn = (session.Status.Turns ?? []).SingleOrDefault(turn =>
                        string.Equals(turn.Id, existing.TurnId, StringComparison.Ordinal));
                    if (existing.BindingEpoch != session.BindingEpoch
                        || existing.ContextGeneration != session.Status.ContextGeneration
                        || admittedTurn is null
                        || admittedTurn.Status != AgentTurnStatus.Executing
                        || admittedTurn.SupersededAt is not null
                        || admittedTurn.ContextGeneration != session.Status.ContextGeneration
                        || !string.Equals(existing.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal))
                        throw new InvalidOperationException("initial_input_start_conflict");
                    return [];
                }
                // A later Job may take over the provider receipt only when the
                // prior receipt's own Turn is definitely terminal. Settled
                // execution is history; a Queued, Executing, Unknown, or absent
                // Turn keeps that receipt current and this start fail-closed.
                if (!PriorReceiptOwnsDefinitelyTerminalTurn(session, existing))
                    throw new InvalidOperationException("initial_input_start_conflict");
            }
            else if (existing is not null && !SameInitialOperation(existing, operation))
            {
                throw new InvalidOperationException("initial_input_operation_conflict");
            }

            var turns = (session.Status.Turns ?? []).ToList();
            var live = turns.Where(turn => turn.SupersededAt is null
                && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing or AgentTurnStatus.Unknown).ToArray();
            var index = turns.FindIndex(turn => string.Equals(turn.Id, operation.TurnId, StringComparison.Ordinal));
            var input = (session.Status.Inputs ?? []).SingleOrDefault(candidate =>
                string.Equals(candidate.Id, operation.InputId, StringComparison.Ordinal));
            if (session.Status.Activity != AgentSessionActivity.Active
                || session.Status.PendingStop is { IsActive: true }
                || session.Status.PendingReset is { Outcome: null, SupersededAt: null }
                || live.Length != 1
                || index < 0
                || turns[index].Status != AgentTurnStatus.Queued
                || turns[index].SupersededAt is not null
                || !string.Equals(turns[index].JobId, operation.JobId, StringComparison.Ordinal)
                || !turns[index].InputIds.Contains(operation.InputId, StringComparer.Ordinal)
                || input is null
                || !string.Equals(input.JobId, operation.JobId, StringComparison.Ordinal)
                || operation.BindingEpoch != session.BindingEpoch
                || operation.ContextGeneration != session.Status.ContextGeneration
                || !string.Equals(operation.RunnerId, session.Runtime.RunnerId, StringComparison.Ordinal)
                || !string.Equals(NormalizeRuntime(operation.Runtime), session.Runtime.Runtime, StringComparison.Ordinal)
                || !string.Equals(operation.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("initial_input_start_fence_mismatch");

            turns[index] = turns[index] with { Status = AgentTurnStatus.Executing, UpdatedAt = now };
            session.Status = session.Status with
            {
                Turns = turns,
                InitialInputOperation = operation with
                {
                    // A retry of the same recorded operation keeps its original
                    // record time; a fresh receipt — first admission or the
                    // terminal-prior replacement — records its own.
                    RecordedAt = existing is not null && SameInitialOperation(existing, operation)
                        ? existing.RecordedAt
                        : now,
                    EffectAdmitted = true,
                    EffectAdmittedAt = now,
                },
            };
            return [];
        }

        // One evidence rule shared by admission and recovery: the historical
        // receipt releases its authorization only when its own Turn is
        // definitely terminal AND the receipt still proves it owned that Turn
        // and Input. A terminal but unrelated or malformed Turn must never
        // release an unresolved receipt.
        private static bool PriorReceiptOwnsDefinitelyTerminalTurn(
            AgentSession current,
            AgentInitialInputOperation receipt)
        {
            var turn = (current.Status.Turns ?? []).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, receipt.TurnId, StringComparison.Ordinal));
            if (turn is not { Status: AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled })
                return false;
            if (!string.Equals(turn.JobId, receipt.JobId, StringComparison.Ordinal)
                || !turn.InputIds.Contains(receipt.InputId, StringComparer.Ordinal))
                return false;
            var input = (current.Status.Inputs ?? []).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, receipt.InputId, StringComparison.Ordinal));
            return input is not null
                && string.Equals(input.JobId, receipt.JobId, StringComparison.Ordinal);
        }

        private static bool SameInitialOperation(AgentInitialInputOperation left, AgentInitialInputOperation right) =>
            string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && string.Equals(left.JobId, right.JobId, StringComparison.Ordinal)
            && string.Equals(left.WorkId, right.WorkId, StringComparison.Ordinal)
            && string.Equals(left.ProcessGeneration, right.ProcessGeneration, StringComparison.Ordinal)
            && string.Equals(left.RunnerId, right.RunnerId, StringComparison.Ordinal)
            && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
            && string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal)
            && string.Equals(left.Runtime, right.Runtime, StringComparison.Ordinal)
            && string.Equals(left.RuntimeSessionId, right.RuntimeSessionId, StringComparison.Ordinal);

    }
}
