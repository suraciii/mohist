using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    /// <summary>
    /// Target cap for the retained context-usage history. Picked small
    /// so a trend mini-chart still gets a "lifetime" view
    /// while the grain state and downstream activity payloads
    /// stay bounded.
    /// </summary>
    public const int ContextUsageHistoryCap = 24;

    /// <summary>
    /// Time-bucket size for context-usage history time-thinning. Within a
    /// bucket only the latest sample is kept (last-wins), so back-to-back
    /// usage updates don't drown out the long-run trend.
    /// </summary>
    public static readonly TimeSpan ContextUsageHistoryBucket = TimeSpan.FromSeconds(30);

    extension(AgentSession session)
    {
        public IReadOnlyList<AgentSessionEvent> MergeMetadata(AgentSessionMetadata? metadata)
        {
            session.Metadata = session.Metadata.Merge(metadata);
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> AttachPhysicalSession(
            string agentSessionId,
            string? model,
            string? workDir,
            string? changeDir,
            int? processPid,
            DateTime now,
            string? runtime = null,
            string? expectedRuntime = null,
            string? expectedAgentSessionId = null,
            string? expectedRunnerId = null)
        {
            _ = changeDir;
            _ = processPid;
            var oldModel = session.Settings.Model;
            var existingAgentSessionId = session.Status.AgentRuntimeSessionId;
            var existingRuntime = NormalizeRuntime(session.Runtime.Runtime);
            var nextRuntime = NormalizeRuntime(runtime) ?? existingRuntime ?? "opencode";
            if (string.IsNullOrWhiteSpace(nextRuntime))
                throw new InvalidOperationException("AgentSession attach requires a registered runtime.");
            if (expectedRuntime is not null || expectedAgentSessionId is not null || expectedRunnerId is not null)
            {
                var expected = new AgentRuntimeBinding(
                    expectedRunnerId ?? session.Runtime.RunnerId,
                    expectedRuntime,
                    expectedAgentSessionId);
                EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            }
            if (!string.IsNullOrWhiteSpace(session.Runtime.WorkDir)
                && !string.IsNullOrWhiteSpace(workDir)
                && !string.Equals(session.Runtime.WorkDir, workDir, StringComparison.Ordinal))
                throw new InvalidOperationException($"AgentSession {session.Id} is bound to work directory '{session.Runtime.WorkDir}', not '{workDir}'.");
            // attach is a normal operation that reuses the current binding; it is
            // not a binding-replacement entry point. Replacing a bound physical
            // session under the same runtime must go through Reset / recover-missing
            // (idle-only CAS), otherwise a stray attach would silently swap the
            // runtime session the AgentSession is committed to.
            if (!string.IsNullOrWhiteSpace(existingAgentSessionId)
                && string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} is already bound to runtime session {existingAgentSessionId}; use Reset to replace the binding.");
            }

            var isNewRuntimeBinding = !string.Equals(existingAgentSessionId, agentSessionId, StringComparison.Ordinal)
                || !string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase);

            if (isNewRuntimeBinding && HasHeldBindingUse(session))
                throw new InvalidOperationException("binding_attach_in_progress");

            // A runtime change (e.g. runner restart on a different backend) replaces
            // the binding through the idle-only CAS path so the context window is
            // cleared while cumulative usage is preserved. Same-runtime rebind was
            // already rejected above.
            if (isNewRuntimeBinding && !string.IsNullOrWhiteSpace(existingAgentSessionId)
                && !string.Equals(existingRuntime, nextRuntime, StringComparison.OrdinalIgnoreCase))
            {
                var replacementEvents = session.RebindRuntimeSession(
                    session.CurrentRuntimeBinding(),
                    new AgentRuntimeBinding(session.Runtime.RunnerId, nextRuntime, agentSessionId),
                    "runtime-change",
                    now: now,
                    expectedBindingEpoch: session.BindingEpoch).ToList();
                session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
                if (!string.Equals(oldModel, model ?? oldModel, StringComparison.Ordinal))
                    replacementEvents.Add(new AgentSessionModelChanged(model ?? oldModel));
                return replacementEvents;
            }

            session.Runtime = session.Runtime with
            {
                WorkDir = string.IsNullOrWhiteSpace(session.Runtime.WorkDir) ? workDir : session.Runtime.WorkDir,
                Runtime = nextRuntime,
            };
            session.Settings = session.Settings with { Model = model ?? session.Settings.Model };
            session.Status = session.Status with
            {
                AgentRuntimeSessionId = isNewRuntimeBinding ? agentSessionId : existingAgentSessionId,
                BoundAt = isNewRuntimeBinding ? now : session.Status.BoundAt ?? now,
                LastDataAt = now,
            };
            if (isNewRuntimeBinding)
                session.BindingEpoch = checked(session.BindingEpoch + 1);
            var events = new List<AgentSessionEvent>();
            if (isNewRuntimeBinding)
                events.Add(new AgentSessionRuntimeBound(agentSessionId, session.Runtime.Runtime));
            if (!string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal))
                events.Add(new AgentSessionModelChanged(session.Settings.Model));
            return events;
        }

        public IReadOnlyList<AgentSessionEvent> ResolveModel(string? model, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(model)) return [];
            var oldModel = session.Settings.Model;
            session.Settings = session.Settings with { Model = model };
            session.Status = session.Status with { LastDataAt = now };
            return !string.Equals(oldModel, session.Settings.Model, StringComparison.Ordinal)
                ? [new AgentSessionModelChanged(session.Settings.Model)]
                : [];
        }

        public IReadOnlyList<AgentSessionEvent> RecordActivity(DateTime now)
        {
            session.Status = session.Status with { LastDataAt = now, CurrentTurnEndedAt = null };
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> SetActivity(AgentSessionActivity activity, DateTime now)
        {
            session.Status = session.Status with
            {
                Activity = activity,
                LastDataAt = now,
                CurrentTurnEndedAt = activity == AgentSessionActivity.Idle ? now : session.Status.CurrentTurnEndedAt,
                IdleSince = IdleSinceFor(activity, now),
            };
            return [];
        }

        // Single derivation so every activity transition keeps IdleSince
        // consistent: set on idle, cleared on active and on unknown so an
        // unconfirmable activity can never retain a stale idle time.
        private static DateTime? IdleSinceFor(AgentSessionActivity activity, DateTime now) =>
            activity == AgentSessionActivity.Idle ? now : null;

        public IReadOnlyList<AgentSessionEvent> ApplyUsage(
            long? inputTokens,
            long? outputTokens,
            long? totalTokens,
            long? cachedReadTokens,
            long? thoughtTokens,
            double? costAmount,
            string? costCurrency,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now,
            long? cachedWriteTokens = null)
        {
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            var newUsed = contextWindowUsed ?? usage.ContextWindowUsed;
            var newSize = contextWindowSize ?? usage.ContextWindowSize;
            session.Status = session.Status with
            {
                UsageSummary = usage with
                {
                    InputTokens = AddNonNegative(usage.InputTokens, inputTokens),
                    OutputTokens = AddNonNegative(usage.OutputTokens, outputTokens),
                    TotalTokens = AddNonNegative(usage.TotalTokens, totalTokens),
                    CachedReadTokens = AddNonNegative(usage.CachedReadTokens, cachedReadTokens),
                    CachedWriteTokens = AddNonNegative(usage.CachedWriteTokens, cachedWriteTokens),
                    ThoughtTokens = AddNonNegative(usage.ThoughtTokens, thoughtTokens),
                    CostAmount = AddNonNegative(usage.CostAmount, costAmount),
                    CostCurrency = costCurrency ?? usage.CostCurrency,
                    ContextWindowUsed = newUsed,
                    ContextWindowSize = newSize
                },
                ContextUsageHistory = AppendUsageHistorySample(session.Status.ContextUsageHistory, newUsed, newSize, now)
            };
            return [new AgentSessionUsageRecorded(session.Status.UsageSummary ?? new AgentUsageSummary())];
        }

        public IReadOnlyList<AgentSessionEvent> RebindRuntimeSession(
            AgentRuntimeBinding expected,
            AgentRuntimeBinding replacement,
            string reason,
            DateTime now,
            long expectedBindingEpoch = 0,
            string? queuedTurnIdToRetarget = null)
        {
            if (session.Status.Activity != AgentSessionActivity.Idle)
                throw new InvalidOperationException($"AgentSession {session.Id} is currently {session.Status.Activity}; binding replacement requires idle activity.");
            if (HasHeldBindingUse(session))
                throw new InvalidOperationException("binding_attach_in_progress");
            EnsureExpectedRuntimeBinding(session, expected, session.CurrentRuntimeBinding());
            if (session.BindingEpoch != expectedBindingEpoch)
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} binding epoch changed from {expectedBindingEpoch} to {session.BindingEpoch}.");
            if (string.IsNullOrWhiteSpace(replacement.RunnerId) || string.IsNullOrWhiteSpace(replacement.RuntimeSessionId))
                throw new InvalidOperationException("Binding replacement requires a runner and runtime session.");
            if (reason is not ("reset" or "runtime-change" or "missing-recovery"))
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unsupported binding replacement reason.");
            var queuedTurns = (session.Status.Turns ?? []).Where(turn =>
                turn.SupersededAt is null
                && turn.Status == AgentTurnStatus.Queued).ToArray();
            if (queuedTurns.Length > 0 && string.IsNullOrWhiteSpace(queuedTurnIdToRetarget))
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} binding replacement requires an explicit pre-submission Turn fence.");
            if (!string.IsNullOrWhiteSpace(queuedTurnIdToRetarget))
                EnsureCanRetargetSealedQueuedTurn(session, queuedTurnIdToRetarget);
            EnsureNoCurrentExecutionOrOperations(session, reason, queuedTurnIdToRetarget);
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
                UsageSummary = usage with { ContextWindowUsed = null, ContextWindowSize = null },
            };
            session.PersistedActivitySummary = (session.PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty) with
            {
                LastTerminalStatus = null,
                LatestActivity = null,
            };
            session.BindingEpoch = nextBindingEpoch;
            session.Status = session.Status with
            {
                ContextGeneration = nextGeneration,
                MissingRunnerFact = null,
                PendingActivityObservation = null,
                ConfirmedExecutionOwnership = null,
            };
            if (!string.IsNullOrWhiteSpace(queuedTurnIdToRetarget))
            {
                RetargetSealedQueuedTurn(session, queuedTurnIdToRetarget, replacement, nextGeneration, now);
                session.SetActivity(session.DeriveCurrentActivity(), now);
            }
            return [new AgentSessionRuntimeBound(replacement.RuntimeSessionId, session.Runtime.Runtime)];
        }

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
                if (!SameInitialOperation(existing, operation)
                    || existing.EffectAdmitted
                    || !Equals(session.CurrentRuntimeBinding(), replacement))
                    throw new InvalidOperationException("initial_input_operation_conflict");
                return [];
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
                var admittedTurn = (session.Status.Turns ?? []).SingleOrDefault(turn =>
                    string.Equals(turn.Id, existing.TurnId, StringComparison.Ordinal));
                if (!SameInitialOperation(existing, operation)
                    || existing.BindingEpoch != session.BindingEpoch
                    || existing.ContextGeneration != session.Status.ContextGeneration
                    || admittedTurn is null
                    || admittedTurn.Status != AgentTurnStatus.Executing
                    || admittedTurn.SupersededAt is not null
                    || admittedTurn.ContextGeneration != session.Status.ContextGeneration
                    || !string.Equals(existing.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal))
                    throw new InvalidOperationException("initial_input_start_conflict");
                return [];
            }
            if (existing is not null && !SameInitialOperation(existing, operation))
                throw new InvalidOperationException("initial_input_operation_conflict");

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
                    RecordedAt = existing?.RecordedAt ?? now,
                    EffectAdmitted = true,
                    EffectAdmittedAt = now,
                },
            };
            return [];
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

        private static void EnsureNoCurrentExecutionOrOperations(
            AgentSession current,
            string reason,
            string? queuedTurnIdToRetarget)
        {
            var hasCurrentExecution = (current.Status.Turns ?? []).Any(turn =>
                turn.ContextGeneration == current.Status.ContextGeneration
                && turn.SupersededAt is null
                && turn.Status is AgentTurnStatus.Executing or AgentTurnStatus.Unknown);
            var hasConfirmedExecution = current.Status.ConfirmedExecutionOwnership is { } ownership
                && ownership.ContextGeneration == current.Status.ContextGeneration
                && ownership.TurnIds.Count > 0;
            var followups = current.Status.PendingFollowups is { Count: > 0 } pending
                ? pending
                : current.Status.PendingFollowup is { } legacy
                    ? (IReadOnlyList<AgentSessionFollowupLease>)[legacy]
                    : [];
            var hasOtherFollowup = followups.Any(lease =>
                string.IsNullOrWhiteSpace(queuedTurnIdToRetarget)
                || !string.Equals(lease.TurnId, queuedTurnIdToRetarget, StringComparison.Ordinal));
            // The admitted reset reservation is the operation authorizing a
            // reset rebind; every other active operation remains a conflict.
            var hasActiveReset = reason != "reset"
                && current.Status.PendingReset is { Outcome: null, SupersededAt: null };
            var hasActiveStop = current.Status.PendingStop is { IsActive: true };

            if (hasCurrentExecution || hasConfirmedExecution || hasOtherFollowup || hasActiveReset || hasActiveStop)
            {
                throw new InvalidOperationException(
                    $"AgentSession {current.Id} binding replacement requires settled execution and operations.");
            }
        }

        private static void EnsureCanRetargetSealedQueuedTurn(AgentSession current, string turnId)
        {
            var nonterminal = (current.Status.Turns ?? [])
                .Where(turn => turn.SupersededAt is null
                    && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
                .ToArray();
            var leases = current.Status.PendingFollowups ?? [];
            var lease = leases.SingleOrDefault(candidate =>
                string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal));
            if (nonterminal.Length != 1
                || nonterminal[0].Status != AgentTurnStatus.Queued
                || !string.Equals(nonterminal[0].Id, turnId, StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(nonterminal[0].JobId)
                || leases.Count != 1
                || lease is not { Dispatching: true, PayloadSealed: true })
            {
                throw new InvalidOperationException(
                    $"AgentSession {current.Id} cannot retarget queued Turn '{turnId}' outside the pre-submission recovery fence.");
            }
        }

        private static void RetargetSealedQueuedTurn(
            AgentSession current,
            string turnId,
            AgentRuntimeBinding replacement,
            long nextGeneration,
            DateTime now)
        {
            var turns = (current.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));
            var leases = (current.Status.PendingFollowups ?? []).ToList();
            var leaseIndex = leases.FindIndex(lease => string.Equals(lease.TurnId, turnId, StringComparison.Ordinal));
            var turn = turns[turnIndex];
            turns[turnIndex] = turn with
            {
                ContextGeneration = nextGeneration,
                UpdatedAt = now,
                WorkflowExecution = turn.WorkflowExecution is { } workflow
                    ? workflow with
                    {
                        RunnerId = replacement.RunnerId!,
                        Runtime = NormalizeRuntime(replacement.Runtime)!,
                        RuntimeSessionId = replacement.RuntimeSessionId!,
                    }
                    : null,
            };
            leases[leaseIndex] = leases[leaseIndex] with
            {
                RuntimeSessionId = replacement.RuntimeSessionId!,
            };
            current.Status = current.Status with
            {
                Turns = turns,
                PendingFollowups = leases,
                PendingFollowup = current.Status.PendingFollowup is { } legacy
                    && string.Equals(legacy.TurnId, turnId, StringComparison.Ordinal)
                    ? leases[leaseIndex]
                    : current.Status.PendingFollowup,
            };
        }

        public IReadOnlyList<AgentSessionEvent> RecordCompaction(
            long? contextWindowUsedBefore,
            long? contextWindowUsedAfter,
            long? contextWindowSize,
            string? strategy,
            string? summary,
            DateTime now)
        {
            var usage = session.Status.UsageSummary ?? new AgentUsageSummary();
            session.Status = session.Status with
            {
                LastDataAt = now,
                UsageSummary = usage with
                {
                    ContextWindowUsed = contextWindowUsedAfter ?? usage.ContextWindowUsed,
                    ContextWindowSize = contextWindowSize ?? usage.ContextWindowSize,
                }
            };
            return [new AgentSessionContextCompacted(
                ContextWindowUsedBefore: contextWindowUsedBefore,
                ContextWindowUsedAfter: contextWindowUsedAfter,
                ContextWindowSize: contextWindowSize,
                Strategy: strategy,
                Summary: summary,
                RecordedAt: now)];
        }

        /// <summary>
        /// Records a context-exhaustion classification on the session
        /// after a failed close event. The failureCategory is captured
        /// on the event payload so downstream consumers (UI, retry
        /// guard, analytics) can render a context-exhaustion error
        /// message and decide whether to block retries.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> RecordContextExhaustion(
            string? failureCategory,
            double? contextUsagePercent,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            session.Status = session.Status with { LastDataAt = now };
            return [new AgentSessionContextExhausted(
                FailureCategory: failureCategory,
                ContextUsagePercent: contextUsagePercent,
                ContextWindowUsed: contextWindowUsed,
                ContextWindowSize: contextWindowSize,
                RecordedAt: now)];
        }

        /// <summary>
        /// Records a context-health transition (green/yellow/red
        /// threshold crossing or large percent change). The session
        /// status is updated so subsequent reads of the session
        /// expose the latest known health snapshot, and the bounded
        /// context-usage history is thinned-appended so a freshly
        /// opened Pulse sees the lifetime trend rather than only the
        /// latest snapshot.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> RecordContextHealthUpdate(
            string healthStatus,
            double? contextUsagePercent,
            long? contextWindowUsed,
            long? contextWindowSize,
            DateTime now)
        {
            session.Status = session.Status with
            {
                LastDataAt = now,
                ContextUsageHistory = AppendUsageHistorySample(
                    session.Status.ContextUsageHistory,
                    contextWindowUsed,
                    contextWindowSize,
                    now)
            };
            return [new AgentSessionContextHealthUpdated(
                HealthStatus: healthStatus,
                ContextUsagePercent: contextUsagePercent,
                ContextWindowUsed: contextWindowUsed,
                ContextWindowSize: contextWindowSize,
                RecordedAt: now)];
        }

        private static long? AddNonNegative(long? current, long? delta)
        {
            if (delta is null or < 0) return current;
            return (current ?? 0) + delta.Value;
        }

        private static double? AddNonNegative(double? current, double? delta)
        {
            if (delta is null or < 0) return current;
            return (current ?? 0) + delta.Value;
        }

        private static string? NormalizeRuntime(string? runtime) =>
            string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();

        private static bool IsRegisteredRuntime(string runtime) =>
            string.Equals(runtime, "opencode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runtime, "pi", StringComparison.OrdinalIgnoreCase);

        public bool IsRuntimeSessionMissing(Func<string, bool> isRuntimeRegistered)
        {
            ArgumentNullException.ThrowIfNull(isRuntimeRegistered);
            if (string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId))
                return true;

            var runtime = session.Runtime.Runtime;
            if (string.IsNullOrWhiteSpace(runtime))
                return true;

            return !isRuntimeRegistered(runtime);
        }

        /// <summary>
        /// Initial-launch transition: opens the session if absent,
        /// records the first <see cref="AgentSessionInputRecord"/> as
        /// accepted, records the first <see cref="AgentTurnRecord"/> as
        /// queued, and links both to the supplied AgentJob id. The
        /// session activity is bumped to active so navigation surfaces
        /// reflect the new work. The transition is idempotent for a
        /// replay carrying the same ids; mismatched ids or
        /// pre-existing immutable source metadata raise a conflict.
        /// </summary>
        /// <param name="attachments">
        /// Ordered child record of attachment descriptors carried by
        /// the accepted input. The transition owns the persistence of
        /// this list alongside the text so the accepted set survives a
        /// grain reload and is queryable via the input surface.
        /// Replays with the same input id must supply an equivalent
        /// (id + name + content-type + size) ordered set; a mismatch
        /// raises a conflict (the launch identity is immutable once
        /// accepted).
        /// </param>
        /// <param name="provenance">
        /// Per-input provenance describing the upstream source the
        /// caller attached (provider kind, workspace id,
        /// conversation id, thread id, member id, message id,
        /// connection id). Persisted on the input record so a
        /// later observer can attribute the accepted input back to
        /// its source. Replays must supply an equivalent provenance
        /// record; a mismatch raises a conflict (the launch identity
        /// is immutable once accepted).
        /// </param>
        /// <param name="startupContext">
        /// Optional bounded external discussion the caller attaches as
        /// first-launch-only background. Persisted verbatim on the
        /// input record (including the truncation attestation) so the
        /// audit is inspectable and a recovery replay observes the
        /// same first-accepted snapshot. <c>prompt</c> stays
        /// task-only — the background is composed into the dispatched
        /// agent input at <c>BuildDispatch</c> time, not at the
        /// SessionInput layer. Null when no startup context was
        /// supplied. Replays with the same input id must supply an
        /// equivalent record (value equality); a mismatch raises a
        /// conflict.
        /// </param>
        public IReadOnlyList<AgentSessionEvent> EnsureInitialLaunch(
            string inputId,
            string turnId,
            string prompt,
            string source,
            string jobId,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null,
            AgentStartupContext? startupContext = null)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);
            var hasText = !string.IsNullOrEmpty(prompt);
            var hasAttachments = normalizedAttachments is { Count: > 0 };
            if (!hasText && !hasAttachments)
                throw new ArgumentException(
                    "Prompt is required unless at least one attachment is accepted.",
                    nameof(prompt));
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("Job id is required.", nameof(jobId));

            var inputs = (session.Status.Inputs ?? []).ToList();
            var inputIndex = inputs.FindIndex(candidate =>
                string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
            if (inputIndex >= 0)
            {
                var existing = inputs[inputIndex];
                if (!string.Equals(existing.Text, prompt, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !string.Equals(existing.JobId, jobId, StringComparison.Ordinal)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance)
                    || !Equals(existing.StartupContext, startupContext))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has input '{inputId}' with different content/source/job/attachments.");
                }
            }
            else
            {
                inputs.Add(new AgentSessionInputRecord(
                    Id: inputId,
                    Sequence: inputs.Count + 1,
                    Text: prompt,
                    Source: source,
                    Acceptance: AgentSessionInputAcceptance.Accepted,
                    RecordedAt: now,
                    JobId: jobId,
                    Attachments: normalizedAttachments,
                    Provenance: provenance,
                    StartupContext: startupContext,
                    ExecutionSource: ExecutionSourceFor(provenance),
                    OriginMarker: provenance?.OriginMarker,
                    ContextGeneration: session.Status.ContextGeneration));
            }

            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (turnIndex >= 0)
            {
                var existing = turns[turnIndex];
                if (!string.Equals(existing.JobId, jobId, StringComparison.Ordinal)
                    || !existing.InputIds.Contains(inputId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has turn '{turnId}' with different job/input linkage.");
                }
            }
            else
            {
                turns.Add(new AgentTurnRecord(
                    Id: turnId,
                    Sequence: turns.Count + 1,
                    InputIds: new[] { inputId },
                    Status: AgentTurnStatus.Queued,
                    JobId: jobId,
                    RecordedAt: now,
                    UpdatedAt: now,
                    ContextGeneration: session.Status.ContextGeneration));
            }

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                Activity = AgentSessionActivity.Active,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
                IdleSince = null,
            };

            return [];
        }

        public IReadOnlyList<AgentSessionEvent> RecordFollowupTurn(
            string inputId,
            string turnId,
            string prompt,
            string source,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
            AgentSessionInputProvenance? provenance = null) =>
            session.RecordFollowupTurnCore(
                inputId,
                turnId,
                prompt,
                source,
                now,
                attachments: attachments,
                provenance: provenance);

        public IReadOnlyList<AgentSessionEvent> RecordManagerRecoveryTurn(
            string inputId,
            string turnId,
            string prompt,
            string source,
            DateTime now,
            AgentSessionInputProvenance provenance) =>
            session.RecordFollowupTurnCore(
                inputId,
                turnId,
                prompt,
                source,
                now,
                attachments: null,
                provenance: provenance,
                allowUnknownRecovery: true);

        private IReadOnlyList<AgentSessionEvent> RecordFollowupTurnCore(
            string inputId,
            string turnId,
            string prompt,
            string source,
            DateTime now,
            IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments,
            AgentSessionInputProvenance? provenance,
            bool allowUnknownRecovery = false)
        {
            if (string.IsNullOrWhiteSpace(inputId))
                throw new ArgumentException("Input id is required.", nameof(inputId));
            if (string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Turn id is required.", nameof(turnId));
            if (string.IsNullOrWhiteSpace(prompt)
                && (attachments is null || attachments.Count == 0))
            {
                throw new ArgumentException(
                    "Prompt is required unless at least one attachment is accepted.",
                    nameof(prompt));
            }
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Source is required.", nameof(source));

            var normalizedAttachments = NormalizeAttachmentDescriptors(attachments);

            var inputs = (session.Status.Inputs ?? []).ToList();
            var turns = (session.Status.Turns ?? []).ToList();
            var inputIndex = inputs.FindIndex(candidate =>
                string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));

            if (inputIndex >= 0)
            {
                var existing = inputs[inputIndex];
                if (!string.Equals(existing.Text, prompt, StringComparison.Ordinal)
                    || !string.Equals(existing.Source, source, StringComparison.Ordinal)
                    || !string.IsNullOrWhiteSpace(existing.JobId)
                    || !AttachmentDescriptorsEquivalent(existing.Attachments, normalizedAttachments)
                    || !Equals(existing.Provenance, provenance))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has input '{inputId}' with different content/source/job/attachments linkage.");
                }
            }

            if (turnIndex >= 0)
            {
                var existing = turns[turnIndex];
                if (!string.IsNullOrWhiteSpace(existing.JobId)
                    || !existing.InputIds.Contains(inputId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} already has turn '{turnId}' with different job/input linkage.");
                }
            }

            if (inputIndex >= 0 || turnIndex >= 0)
            {
                if (inputIndex < 0 || turnIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"AgentSession {session.Id} has incomplete input/turn linkage for '{inputId}' and '{turnId}'.");
                }
                return [];
            }

            if (turns.Any(candidate => candidate.InputIds.Contains(inputId, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} already links input '{inputId}' to another turn.");
            }

            var activityAllowsRecovery = allowUnknownRecovery
                && session.Status.Activity == AgentSessionActivity.Unknown;
            var hasActiveTurn = turns.Any(candidate => string.IsNullOrWhiteSpace(candidate.JobId)
                && (candidate.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing
                    || candidate.Status == AgentTurnStatus.Unknown && !activityAllowsRecovery));
            if ((session.Status.Activity != AgentSessionActivity.Idle && !activityAllowsRecovery)
                || hasActiveTurn)
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} cannot start another turn while work is active.");
            }

            inputs.Add(new AgentSessionInputRecord(
                Id: inputId,
                Sequence: inputs.Count + 1,
                Text: prompt,
                Source: source,
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now,
                JobId: null,
                Attachments: normalizedAttachments,
                Provenance: provenance,
                ExecutionSource: ExecutionSourceFor(provenance),
                OriginMarker: provenance?.OriginMarker,
                ContextGeneration: session.Status.ContextGeneration));
            turns.Add(new AgentTurnRecord(
                Id: turnId,
                Sequence: turns.Count + 1,
                InputIds: new[] { inputId },
                Status: AgentTurnStatus.Queued,
                JobId: null,
                RecordedAt: now,
                UpdatedAt: now,
                ContextGeneration: session.Status.ContextGeneration));

            // System-initiated turns (Manager expiry/loss recovery) must be
            // dispatchable by the ordinary follow-up dispatcher, which only
            // claims queued turns carrying a matching lease; without one the
            // recovery turn is recorded but never executed and never receives
            // fresh credentials.
            var leases = (session.Status.PendingFollowups ?? []).ToList();
            if (!leases.Any(candidate => string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal)))
            {
                leases.Add(new AgentSessionFollowupLease(
                    OperationId: $"system-turn:{turnId}",
                    RuntimeSessionId: session.Status.AgentRuntimeSessionId ?? string.Empty,
                    Accepted: true,
                    AcceptedAt: now,
                    StartedAt: now,
                    InputId: inputId,
                    TurnId: turnId));
            }

            session.Status = session.Status with
            {
                Inputs = inputs,
                Turns = turns,
                PendingFollowups = leases,
                Activity = AgentSessionActivity.Active,
                LastDataAt = now,
                CurrentTurnEndedAt = null,
                IdleSince = null,
            };

            return [];
        }

        /// <summary>
        /// Mark the initial turn for the given job id as
        /// <see cref="AgentTurnStatus.Executing"/>. No-op if the turn
        /// is already in a non-queued state. Used by the AgentJob
        /// dispatch observer path so the Session's view of the
        /// running turn stays consistent with Job-side progress.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkInitialTurnExecuting(string jobId, DateTime now)
        {
            var turns = session.Status.Turns ?? [];
            var index = FindTurnIndexByJobId(turns, jobId);
            if (index < 0)
                return [];
            return session.MarkTurnExecuting(turns[index].Id, now);
        }

        public IReadOnlyList<AgentSessionEvent> MarkTurnExecuting(string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled)
            {
                return [];
            }
            // A superseded Turn is settled evidence: a late message can never
            // revive it or the activity it was settled out of.
            if (turns[index].SupersededAt is not null)
                return [];
            var wasUnknown = turns[index].Status == AgentTurnStatus.Unknown;
            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Executing,
                UpdatedAt = now,
            };
            session.Status = session.Status with
            {
                Turns = turns,
                Activity = wasUnknown ? AgentSessionActivity.Active : session.Status.Activity,
                LastDataAt = now,
                CurrentTurnEndedAt = wasUnknown ? null : session.Status.CurrentTurnEndedAt,
                IdleSince = wasUnknown ? null : session.Status.IdleSince,
            };
            return [];
        }

        /// <summary>
        /// Apply a terminal result to the initial turn for the given
        /// job id. The turn moves to Completed, Failed, or Unknown
        /// based on the supplied status. The session remains usable
        /// after a terminal first turn — AgentSession is the
        /// conversation owner, not the work owner.
        /// </summary>
        public IReadOnlyList<AgentSessionEvent> MarkInitialTurnTerminal(
            string jobId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            var turns = session.Status.Turns ?? [];
            var index = FindTurnIndexByJobId(turns, jobId);
            if (index < 0)
                return [];
            return session.MarkTurnTerminal(turns[index].Id, status, result, now);
        }

        public IReadOnlyList<AgentSessionEvent> MarkTurnTerminal(
            string turnId,
            AgentTurnStatus status,
            AgentTurnResult? result,
            DateTime now)
        {
            if (status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
                throw new ArgumentOutOfRangeException(nameof(status), status, "Turn terminal status is required.");

            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                || turns[index].Status == AgentTurnStatus.Unknown && status == AgentTurnStatus.Unknown)
            {
                return [];
            }
            // Terminal evidence for a superseded Turn would rewrite settled
            // history; the supersession stands.
            if (turns[index].SupersededAt is not null)
                return [];

            turns[index] = turns[index] with
            {
                Status = status,
                Result = result,
                UpdatedAt = now,
            };
            var inputIds = turns[index].InputIds;
            var inputs = session.Status.Inputs ?? [];
            var updatedInputs = inputs
                .Select(candidate => inputIds.Contains(candidate.Id, StringComparer.Ordinal)
                    ? candidate with { Acceptance = AgentSessionInputAcceptance.Accepted }
                    : candidate)
                .ToList();
            // Activity is derived from the remaining current facts: another
            // nonterminal or uncertain Turn keeps the session off idle, and a
            // superseded Turn never does. The derivation reads the updated
            // Turn list, so the just-recorded outcome is already visible.
            var nextActivity = DeriveActivityFrom(
                turns,
                session.Status.ContextGeneration,
                session.Status.PendingStop,
                session.Status.PendingReset);
            session.Status = session.Status with
            {
                Turns = turns,
                Inputs = updatedInputs,
                LastDataAt = now,
                Activity = nextActivity,
                CurrentTurnEndedAt = now,
                IdleSince = IdleSinceFor(nextActivity, now),
                ConfirmedExecutionOwnership = session.Status.ConfirmedExecutionOwnership is { } ownership
                    && ownership.TurnIds.Contains(turnId, StringComparer.Ordinal)
                    ? null
                    : session.Status.ConfirmedExecutionOwnership,
            };
            return [];
        }

        public IReadOnlyList<AgentSessionEvent> StopTurn(string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var index = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (index < 0)
                return [];
            if (turns[index].Status is AgentTurnStatus.Executing
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                or AgentTurnStatus.Unknown)
            {
                return [];
            }
            turns[index] = turns[index] with
            {
                Status = AgentTurnStatus.Cancelled,
                UpdatedAt = now,
            };
            // Cancelling one queued Turn must not read the session idle while
            // another current Turn is still live.
            var nextActivity = DeriveActivityFrom(
                turns,
                session.Status.ContextGeneration,
                session.Status.PendingStop,
                session.Status.PendingReset);
            session.Status = session.Status with
            {
                Turns = turns,
                LastDataAt = now,
                Activity = nextActivity,
                CurrentTurnEndedAt = now,
                IdleSince = IdleSinceFor(nextActivity, now),
            };
            return [];
        }

        public AgentTurnStopResult StopQueuedTurn(string turnId, DateTime now)
        {
            var control = session.ResolveTurnControl(turnId);
            if (control?.Classification != AgentTurnControlClassification.Queued || control.IsLaunchTurn)
                return new AgentTurnStopResult(control, false);

            _ = session.StopTurn(turnId, now);
            return new AgentTurnStopResult(session.ResolveTurnControl(turnId), true);
        }

        public AgentTurnStopClaimResult ClaimTurnStop(
            string turnId,
            string? operationId,
            DateTimeOffset now,
            TimeSpan deadline)
        {
            var control = session.ResolveTurnControl(turnId);
            var pending = session.Status.PendingStop;
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                var archived = (session.Status.SupersededStopClaims ?? []).LastOrDefault(claim =>
                    string.Equals(claim.TurnId, turnId, StringComparison.Ordinal)
                    && string.Equals(claim.OperationId, operationId, StringComparison.Ordinal));
                if (archived is not null)
                {
                    return new AgentTurnStopClaimResult(
                        control,
                        CanDispatch: false,
                        archived.OperationId,
                        archived.Disposition,
                        archived.Reason);
                }
                if ((session.Status.SupersededStopClaims ?? []).Any(claim =>
                    string.Equals(claim.OperationId, operationId, StringComparison.Ordinal)))
                    return new AgentTurnStopClaimResult(control, false, null);
            }
            if (control?.Classification == AgentTurnControlClassification.Terminal
                && pending is not null
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
            {
                return new AgentTurnStopClaimResult(
                    control,
                    pending.IsActive,
                    pending.OperationId,
                    pending.Disposition,
                    pending.Reason);
            }

            if (control?.Classification != AgentTurnControlClassification.Executing)
                return new AgentTurnStopClaimResult(control, false, null);

            if (pending is not null
                && pending.IsActive
                && !string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
                return new AgentTurnStopClaimResult(control, false, null);

            if (pending is null || !pending.IsActive)
            {
                pending = new AgentSessionStopClaim(
                    turnId,
                    string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId,
                    DeadlineAt: now.Add(deadline));
                session.Status = session.Status with { PendingStop = pending };
            }

            return new AgentTurnStopClaimResult(
                control,
                true,
                pending.OperationId,
                pending.Disposition,
                pending.Reason);
        }

        public AgentSessionStopClaim? FindStopClaim(string turnId, string operationId)
        {
            if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(operationId))
                return null;
            if (session.Status.PendingStop is { } pending
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
                return pending;
            return (session.Status.SupersededStopClaims ?? []).LastOrDefault(claim =>
                string.Equals(claim.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(claim.OperationId, operationId, StringComparison.Ordinal));
        }

        public void MarkTurnStopDispatched(string turnId, string operationId)
        {
            var pending = session.Status.PendingStop;
            if (pending is not null
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal)
                && pending.IsActive
                && !pending.DispatchStarted)
            {
                session.Status = session.Status with
                {
                    PendingStop = pending with { DispatchStarted = true },
                };
            }
        }

        public void CompleteTurnStop(string turnId, string operationId)
        {
            session.SettleTurnStop(turnId, operationId, AgentSessionStopDisposition.Ended);
        }

        public void SettleTurnStop(
            string turnId,
            string operationId,
            AgentSessionStopDisposition disposition,
            string? reason = null)
        {
            var pending = session.Status.PendingStop;
            if (pending is { IsActive: true }
                && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
                && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
            {
                session.Status = session.Status with
                {
                    PendingStop = pending with
                    {
                        Disposition = disposition,
                        Reason = reason,
                    },
                };
            }
        }

        public IReadOnlyList<AgentSessionEvent> AbandonFollowupTurn(string inputId, string turnId, DateTime now)
        {
            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (turnIndex < 0 || turns[turnIndex].JobId is not null
                || turns[turnIndex].Status != AgentTurnStatus.Queued
                || !turns[turnIndex].InputIds.Contains(inputId, StringComparer.Ordinal))
                return [];

            turns.RemoveAt(turnIndex);
            var inputs = (session.Status.Inputs ?? []).Where(candidate =>
                !string.Equals(candidate.Id, inputId, StringComparison.Ordinal)).ToList();
            session.Status = session.Status with
            {
                Turns = turns,
                Inputs = inputs,
                Activity = AgentSessionActivity.Idle,
                LastDataAt = now,
                CurrentTurnEndedAt = now,
                IdleSince = now,
            };
            return [];
        }

        /// <summary>
        /// Resolves a Turn by id and classifies it for control-plane
        /// targeting. Returns <c>null</c> when no Turn
        /// matches; the caller treats that as <c>turn-not-found</c>.
        /// </summary>
        public AgentTurnControlState? ResolveTurnControl(string turnId)
        {
            var turns = session.Status.Turns ?? [];
            var match = turns.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (match is null)
                return null;
            return new AgentTurnControlState(
                TurnId: match.Id,
                Status: match.Status,
                Classification: ClassifyTurn(match.Status),
                IsLaunchTurn: !string.IsNullOrWhiteSpace(match.JobId),
                JobId: match.JobId);
        }

        public AgentTurnControlState? ResolveCurrentTurnControl()
        {
            var turn = (session.Status.Turns ?? [])
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefault(candidate => ClassifyTurn(candidate.Status) != AgentTurnControlClassification.Terminal);
            return turn is null ? null : session.ResolveTurnControl(turn.Id);
        }

        private static AgentTurnControlClassification ClassifyTurn(AgentTurnStatus status) =>
            status switch
            {
                AgentTurnStatus.Queued => AgentTurnControlClassification.Queued,
                AgentTurnStatus.Executing => AgentTurnControlClassification.Executing,
                AgentTurnStatus.Completed
                    or AgentTurnStatus.Failed
                    or AgentTurnStatus.Cancelled
                    or AgentTurnStatus.Unknown => AgentTurnControlClassification.Terminal,
                _ => AgentTurnControlClassification.Terminal,
            };

        private static int FindTurnIndexByJobId(IReadOnlyList<AgentTurnRecord>? turns, string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || turns is null)
                return -1;
            for (var index = 0; index < turns.Count; index++)
            {
                if (string.Equals(turns[index].JobId, jobId, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }

        /// <summary>
        /// Find the first accepted <see cref="AgentSessionInputRecord"/>
        /// on this session whose stored idempotency key matches the
        /// supplied value exactly. Returns <c>null</c> when no input
        /// matches (the caller should treat the input as new and mint
        /// fresh ids). The lookup does not restrict by acceptance:
        /// any stored key on the agent-owned input list is considered,
        /// so a retry after a terminal turn still resolves to the same
        /// input identity.
        /// </summary>
        public AgentSessionFollowupInputLookup? FindFollowupInputByIdempotencyKey(string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return null;
            var inputs = session.Status.Inputs ?? [];
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (string.Equals(input.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    var turn = (session.Status.Turns ?? [])
                        .FirstOrDefault(candidate => candidate.InputIds.Contains(input.Id, StringComparer.Ordinal));
                    var leases = session.Status.PendingFollowups is { Count: > 0 } pending
                        ? pending
                        : session.Status.PendingFollowup is null ? [] : [session.Status.PendingFollowup];
                    var lease = leases
                        .FirstOrDefault(candidate => string.Equals(candidate.TurnId, turn?.Id, StringComparison.Ordinal));
                    return new AgentSessionFollowupInputLookup(input, turn, lease?.OperationId ?? turn?.OperationId);
                }
            }
            return null;
        }

        /// <summary>
        /// Find the non-terminal (queued or executing) follow-up turn
        /// whose lease carries the supplied operationId, or
        /// <c>null</c> when no such turn exists. Launch turns
        /// (JobId != null) are excluded — those are owned by the
        /// AgentJob and progress via the launch observers.
        /// </summary>
        public AgentTurnRecord? FindFollowupTurnByOperationId(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                return null;
            var lease = (session.Status.PendingFollowups ?? [])
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(candidate.TurnId));
            if (lease is null || string.IsNullOrEmpty(lease.TurnId))
                return null;
            return (session.Status.Turns ?? [])
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, lease.TurnId, StringComparison.Ordinal)
                    && string.IsNullOrEmpty(candidate.JobId)
                    && candidate.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
        }

        /// <summary>
        /// Count the number of non-terminal follow-up turns (queued
        /// or executing) on this session. Launch turns are excluded.
        /// </summary>
        public int CountNonTerminalFollowupTurns()
        {
            return (session.Status.Turns ?? [])
                .Count(turn => IsNonTerminalFollowupTurn(session, turn));
        }

        /// <summary>
        /// True if at least one non-terminal follow-up turn exists
        /// on the session. Used by the recovery-idle guard.
        /// </summary>
        public bool HasNonTerminalFollowupTurn() =>
            (session.Status.Turns ?? []).Any(turn => IsNonTerminalFollowupTurn(session, turn));

        /// <summary>
        /// Count the number of accepted follow-up inputs assigned to a
        /// non-terminal follow-up turn. Used by the capacity bound so
        /// a session that fans many inputs into a small number of
        /// turns still hits the cap (e.g. rapid double-sends that
        /// join the same queued turn).
        /// </summary>
        public int CountQueuedFollowupInputs()
        {
            var nonTerminalTurns = (session.Status.Turns ?? [])
                .Where(turn => IsNonTerminalFollowupTurn(session, turn))
                .Select(turn => turn.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (nonTerminalTurns.Count == 0)
                return 0;

            return (session.Status.Inputs ?? [])
                .Count(input => input.JobId is null
                    && input.Acceptance == AgentSessionInputAcceptance.Accepted
                    && (session.Status.Turns ?? [])
                        .Where(turn => nonTerminalTurns.Contains(turn.Id))
                        .Any(turn => turn.InputIds.Contains(input.Id, StringComparer.Ordinal)));
        }

        private static bool IsNonTerminalFollowupTurn(AgentSession currentSession, AgentTurnRecord turn) =>
            string.IsNullOrEmpty(turn.JobId)
            && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing
            && (currentSession.Status.Inputs ?? [])
                .Any(input => turn.InputIds.Contains(input.Id, StringComparer.Ordinal)
                    && string.Equals(input.Source, "agent-session-followup", StringComparison.Ordinal));




    }
}
