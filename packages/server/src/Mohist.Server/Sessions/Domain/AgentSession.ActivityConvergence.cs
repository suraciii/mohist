using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

/// <summary>
/// Local capture of one outstanding activity observation. Primitive phase
/// snapshots fence existing work crossing an external-effect boundary without
/// relying on list reference equality or a global mutation counter.
/// </summary>
public sealed record AgentSessionActivityObservation(
    string ObservationId,
    string RunnerId,
    string Runtime,
    string RuntimeSessionId,
    string WorkDir,
    long BindingEpoch,
    long ContextGeneration,
    AgentSessionActivity CapturedActivity,
    long InputSequence,
    IReadOnlyList<AgentSessionActivityTurnSnapshot> Turns,
    IReadOnlyList<AgentSessionActivityFollowupSnapshot> Followups,
    AgentSessionActivityResetSnapshot? Reset,
    AgentSessionActivityStopSnapshot? Stop,
    bool RunnerRemoved,
    DateTime CapturedAt);

public sealed record AgentSessionActivityTurnSnapshot(
    string TurnId,
    AgentTurnStatus Status,
    long ContextGeneration,
    DateTime? SupersededAt);

public sealed record AgentSessionActivityFollowupSnapshot(
    string OperationId,
    string? TurnId,
    bool Accepted,
    bool Dispatching,
    bool PayloadSealed,
    string? ConcurrencyGateStatus);

public sealed record AgentSessionActivityResetSnapshot(
    string OperationId,
    bool EffectAdmitted,
    bool HasOutcome,
    DateTime? SupersededAt);

public sealed record AgentSessionActivityStopSnapshot(
    string OperationId,
    string TurnId,
    bool DispatchStarted,
    AgentSessionStopDisposition Disposition,
    DateTime? SupersededAt);

public sealed record AgentSessionExecutionOwnership(
    string ObservationId,
    long ContextGeneration,
    IReadOnlyList<string> TurnIds);

/// <summary>
/// Durable write-side fact that the bound Runner cannot account for the
/// current binding. Recorded only from an <c>unknown-to-runner</c>
/// observation, which is deterministic missing evidence: the next accepted
/// input takes the fallback replacement while that Runner is live, otherwise
/// Runtime Session missing recovery.
/// </summary>
public sealed record AgentSessionRunnerMissingFact(
    string RunnerId,
    long BindingEpoch,
    long ContextGeneration,
    DateTime ObservedAt);

/// <summary>
/// Outcome of applying one activity observation.
/// <see cref="Applied"/> is false for every invalid, stale or superseded
/// answer: those are no-ops, never partial settlements.
/// </summary>
public sealed record AgentSessionActivitySettlement(
    bool Applied,
    string Observation,
    bool RestoredActive,
    IReadOnlyList<string> SettledTurnIds,
    IReadOnlyList<string> CancelledTurnIds,
    IReadOnlyList<string> SupersededOperationIds,
    IReadOnlyList<AgentSessionFollowupLease> ReleasedLeases,
    IReadOnlyList<AgentSessionEvent> Events)
{
    public static readonly AgentSessionActivitySettlement NotApplied =
        new(false, string.Empty, false, [], [], [], [], []);
}

public static partial class AgentSessionExtensions
{
    extension(AgentSession session)
    {
        /// <summary>
        /// Captures the pending observation for an activity probe. Returns
        /// null unless the binding is complete for <paramref name="runnerId"/>
        /// and the activity is capturable: a re-registration probe requires
        /// <c>unknown</c> activity, an administrative removal may also capture
        /// an active session. A repeated capture of unchanged state reuses the
        /// pending observation id instead of re-snapshotting it.
        /// </summary>
        public AgentSessionActivityObservation? CaptureActivityObservation(
            string runnerId,
            string observationId,
            bool runnerRemoved,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(observationId))
                return null;
            if (!string.Equals(session.Runtime.RunnerId, runnerId, StringComparison.Ordinal))
                return null;
            var runtime = session.Runtime.Runtime;
            var runtimeSessionId = session.Status.AgentRuntimeSessionId;
            var workDir = session.Runtime.WorkDir;
            if (string.IsNullOrWhiteSpace(runtime)
                || string.IsNullOrWhiteSpace(runtimeSessionId)
                || string.IsNullOrWhiteSpace(workDir))
                return null;

            var activity = session.Status.Activity;
            var capturable = runnerRemoved
                ? activity != AgentSessionActivity.Idle || HasUnresolvedCurrentFacts(session)
                : activity == AgentSessionActivity.Unknown;
            if (!capturable)
                return null;

            if (session.Status.PendingActivityObservation is { } pending
                && ActivityObservationStillCurrent(session, pending, runnerRemoved))
                return pending;

            var observation = new AgentSessionActivityObservation(
                ObservationId: observationId,
                RunnerId: session.Runtime.RunnerId,
                Runtime: runtime,
                RuntimeSessionId: runtimeSessionId,
                WorkDir: workDir,
                BindingEpoch: session.BindingEpoch,
                ContextGeneration: session.Status.ContextGeneration,
                CapturedActivity: activity,
                InputSequence: CurrentInputSequence(session),
                Turns: CurrentTurnSnapshots(session),
                Followups: CurrentFollowupSnapshots(session),
                Reset: CurrentResetSnapshot(session),
                Stop: CurrentStopSnapshot(session),
                RunnerRemoved: runnerRemoved,
                CapturedAt: now);
            session.Status = session.Status with { PendingActivityObservation = observation };
            return observation;
        }

        /// <summary>
        /// Applies one Runner answer to the captured observation. The answer is
        /// fenced on the complete binding tuple, the binding epoch and context
        /// generation, the captured activity, and the captured snapshot of
        /// inputs, turns and operations; any mismatch — including work accepted
        /// after the capture — is discarded. <c>executing</c> restores active
        /// and leaves every Turn and lease untouched. <c>idle</c> and
        /// <c>unknown-to-runner</c> supersede the captured in-flight and already
        /// unknown Turns as terminal unknown, cancel the captured queued Turns,
        /// drop their dispatch leases, supersede the captured active operations,
        /// and re-derive activity from the remaining current facts. Neither
        /// answer replaces the binding nor advances the context generation.
        /// </summary>
        public AgentSessionActivitySettlement SettleActivityFromEvidence(
            RunnerSessionActivityProbeResult result,
            DateTime now)
        {
            if (result?.Probe is not { } probe
                || !RunnerSessionActivityObservations.IsKnown(result.Observation)
                || !probe.HasCompleteTarget()
                || !string.Equals(probe.SessionId, session.Id, StringComparison.Ordinal))
                return AgentSessionActivitySettlement.NotApplied;

            var pending = session.Status.PendingActivityObservation;
            if (pending is null
                || !string.Equals(pending.ObservationId, probe.ObservationId, StringComparison.Ordinal)
                || !MatchesObservationTarget(pending, probe)
                || !CurrentBindingMatchesObservation(session, probe)
                || !ObservationSnapshotUnchanged(session, pending)
                || session.Status.Activity != pending.CapturedActivity)
                return AgentSessionActivitySettlement.NotApplied;

            if (string.Equals(result.Observation, RunnerSessionActivityObservations.Executing, StringComparison.Ordinal))
            {
                var ownerTurnIds = pending.Turns
                    .Where(turn => turn.ContextGeneration == pending.ContextGeneration
                        && turn.SupersededAt is null
                        && turn.Status is AgentTurnStatus.Executing or AgentTurnStatus.Unknown)
                    .Select(turn => turn.TurnId)
                    .ToArray();
                session.Status = session.Status with
                {
                    PendingActivityObservation = null,
                    ConfirmedExecutionOwnership = ownerTurnIds.Length == 0
                        ? null
                        : new AgentSessionExecutionOwnership(
                            pending.ObservationId,
                            pending.ContextGeneration,
                            ownerTurnIds),
                };
                session.SetActivity(AgentSessionActivity.Active, now);
                return new AgentSessionActivitySettlement(
                    true,
                    result.Observation,
                    RestoredActive: true,
                    [],
                    [],
                    [],
                    [],
                    []);
            }

            var turns = (session.Status.Turns ?? []).ToList();
            var capturedTurnIds = pending.Turns.Select(turn => turn.TurnId).ToHashSet(StringComparer.Ordinal);
            var settledTurnIds = new List<string>();
            var cancelledTurnIds = new List<string>();
            for (var index = 0; index < turns.Count; index++)
            {
                var turn = turns[index];
                if (!capturedTurnIds.Contains(turn.Id)
                    || turn.ContextGeneration != pending.ContextGeneration
                    || turn.SupersededAt is not null)
                    continue;
                AgentTurnStatus supersededStatus;
                switch (turn.Status)
                {
                    case AgentTurnStatus.Queued:
                        // Accepted/StartedAt describe logical acceptance, not
                        // Runtime delivery. Only an unsealed, non-dispatching
                        // follow-up lease proves the effect boundary was not crossed.
                        if (FollowupLeaseProvesUndispatched(session, turn.Id) && string.IsNullOrWhiteSpace(turn.JobId))
                        {
                            supersededStatus = AgentTurnStatus.Cancelled;
                            cancelledTurnIds.Add(turn.Id);
                        }
                        else
                        {
                            supersededStatus = AgentTurnStatus.Unknown;
                            settledTurnIds.Add(turn.Id);
                        }
                        break;
                    case AgentTurnStatus.Executing:
                    case AgentTurnStatus.Unknown:
                        supersededStatus = AgentTurnStatus.Unknown;
                        settledTurnIds.Add(turn.Id);
                        break;
                    default:
                        continue;
                }
                turns[index] = turn with
                {
                    Status = supersededStatus,
                    SupersededAt = now,
                    UpdatedAt = now,
                    OperationId = turn.OperationId ?? TurnOperationId(session, turn.Id),
                };
            }

            var (keptLeases, releasedLeases, legacyLeaseKept) = PartitionLeasesForSettlement(session, turns);
            var status = session.Status;
            var supersededOperationIds = new List<string>();
            var capturedOperations = CapturedOperationIds(pending);
            if (status.PendingStop is { IsActive: true } stop
                && capturedOperations.Contains(stop.OperationId))
            {
                var supersededStop = stop with
                {
                    SupersededAt = now,
                    Disposition = AgentSessionStopDisposition.Unknown,
                    Reason = "activity-converged",
                };
                status = status with
                {
                    PendingStop = supersededStop,
                    SupersededStopClaims = (status.SupersededStopClaims ?? []).Append(supersededStop).ToArray(),
                };
                supersededOperationIds.Add(stop.OperationId);
            }
            if (status.PendingReset is { Outcome: null } reset
                && capturedOperations.Contains(reset.OperationId))
            {
                var outcome = SupersededResetOutcome(reset);
                var admissions = (status.SessionCommandAdmissionFacts ?? []).ToList();
                var admissionIndex = admissions.FindIndex(fact =>
                    string.Equals(fact.OperationId, reset.OperationId, StringComparison.Ordinal));
                if (admissionIndex >= 0)
                    admissions[admissionIndex] = admissions[admissionIndex] with { Outcome = outcome };
                else
                    admissions.Add(new AgentSessionCommandAdmissionTombstone(
                        reset.Command,
                        reset.OperationId,
                        reset.IdempotencyKey ?? reset.OperationId,
                        reset.OwnerProcessGeneration ?? string.Empty,
                        outcome));
                status = status with
                {
                    PendingReset = reset with
                    {
                        SupersededAt = now,
                        Outcome = outcome,
                    },
                    SessionCommandAdmissionFacts = admissions,
                };
                supersededOperationIds.Add(reset.OperationId);
            }

            session.Status = status with
            {
                Turns = turns,
                PendingFollowups = keptLeases,
                PendingFollowup = legacyLeaseKept,
                MissingRunnerFact = string.Equals(
                        result.Observation,
                        RunnerSessionActivityObservations.UnknownToRunner,
                        StringComparison.Ordinal)
                    ? new AgentSessionRunnerMissingFact(probe.RunnerId, probe.BindingEpoch, probe.ContextGeneration, now)
                    : session.Status.MissingRunnerFact,
                PendingActivityObservation = null,
                ConfirmedExecutionOwnership = null,
            };
            session.SetActivity(session.DeriveCurrentActivity(), now);

            var events = BuildActivityConvergenceEvents(
                session,
                result.Observation,
                pending.ContextGeneration,
                probe.BindingEpoch,
                settledTurnIds.Concat(cancelledTurnIds).ToArray(),
                supersededOperationIds,
                now);

            return new AgentSessionActivitySettlement(
                true,
                result.Observation,
                RestoredActive: false,
                settledTurnIds,
                cancelledTurnIds,
                supersededOperationIds,
                releasedLeases,
                events);
        }

        /// <summary>
        /// Activity derived from the current generation's facts only: a
        /// superseded Turn or operation takes no part. Uncertain beats active,
        /// because an unconfirmed Turn result can never be read as safe idle.
        /// </summary>
        public AgentSessionActivity DeriveCurrentActivity() =>
            DeriveActivityFrom(
                session.Status.Turns ?? [],
                session.Status.ContextGeneration,
                session.Status.PendingStop,
                session.Status.PendingReset);
    }

    private static AgentSessionActivity DeriveActivityFrom(
        IReadOnlyList<AgentTurnRecord> turns,
        long contextGeneration,
        AgentSessionStopClaim? pendingStop,
        AgentSessionResetReservation? pendingReset)
    {
        var uncertain = false;
        var nonterminal = false;
        foreach (var turn in turns)
        {
            if (turn.SupersededAt is not null) continue;
            // Facts accepted under an earlier context generation are older
            // unresolved facts: they never drive the current Activity, so a
            // replacement is not blocked by work the replaced context owned.
            if (turn.ContextGeneration != contextGeneration) continue;
            switch (turn.Status)
            {
                case AgentTurnStatus.Unknown:
                    uncertain = true;
                    break;
                case AgentTurnStatus.Queued:
                case AgentTurnStatus.Executing:
                    nonterminal = true;
                    break;
            }
        }
        if (uncertain) return AgentSessionActivity.Unknown;
        if (nonterminal) return AgentSessionActivity.Active;
        if (pendingStop is { IsActive: true }) return AgentSessionActivity.Active;
        if (pendingReset is { Outcome: null, SupersededAt: null })
            return AgentSessionActivity.Active;
        return AgentSessionActivity.Idle;
    }

    /// <summary>
    /// Durable Session-to-Job settlement fact. Emitted with the state
    /// transition that superseded a Job-owned initial Turn so the Job side can
    /// arbitrate idempotently; the Session never awaits a Job that calls back
    /// into it.
    /// </summary>
    private static IReadOnlyList<AgentSessionEvent> BuildActivityConvergenceEvents(
        AgentSession session,
        string observation,
        long contextGeneration,
        long bindingEpoch,
        IReadOnlyList<string> settledTurnIds,
        IReadOnlyList<string> supersededOperationIds,
        DateTime now)
    {
        var settledIds = settledTurnIds.ToHashSet(StringComparer.Ordinal);
        var settledJobIds = (session.Status.Turns ?? [])
            .Where(turn => settledIds.Contains(turn.Id) && !string.IsNullOrWhiteSpace(turn.JobId))
            .Select(turn => turn.JobId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (settledJobIds.Length == 0) return [];

        return
        [
            new AgentSessionActivityConverged(
                session.Id,
                observation,
                contextGeneration,
                bindingEpoch,
                settledTurnIds,
                settledJobIds,
                supersededOperationIds,
                now)
        ];
    }

    private static AgentSessionRecoveryOutcome SupersededResetOutcome(
        AgentSessionResetReservation reservation) =>
        new(
            Id: reservation.OperationId,
            Status: "superseded",
            ContextWindowSize: null,
            ContextWindowUsed: null,
            ContextUsagePercent: null,
            ContextWindowUsedBefore: null,
            Operation: reservation.Command,
            WasCompacted: false);

    /// <summary>
    /// Splits the captured dispatch leases: a lease survives only while its
    /// Turn is still live, so a settled, cancelled or never-assigned lease is
    /// released and its concurrency permit handed back. The Turn keeps the
    /// operation identity for audit, which is why dropping the lease loses no
    /// accepted identity.
    /// </summary>
    private static (
        IReadOnlyList<AgentSessionFollowupLease> Kept,
        IReadOnlyList<AgentSessionFollowupLease> Released,
        AgentSessionFollowupLease? LegacyKept) PartitionLeasesForSettlement(
        AgentSession session,
        IReadOnlyList<AgentTurnRecord> turns)
    {
        var combined = session.Status.PendingFollowups is { Count: > 0 } list
            ? list
            : session.Status.PendingFollowup is { } single ? (IReadOnlyList<AgentSessionFollowupLease>)[single] : [];
        var kept = new List<AgentSessionFollowupLease>();
        var released = new List<AgentSessionFollowupLease>();
        foreach (var lease in combined)
        {
            var live = !string.IsNullOrEmpty(lease.TurnId)
                && turns.Any(turn => string.Equals(turn.Id, lease.TurnId, StringComparison.Ordinal)
                    && turn.SupersededAt is null
                    && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
            if (live) kept.Add(lease);
            else released.Add(lease);
        }
        var legacyKept = session.Status.PendingFollowup is { } pending && kept.Contains(pending)
            ? pending
            : null;
        return (kept, released, legacyKept);
    }

    private static bool ActivityObservationStillCurrent(
        AgentSession current,
        AgentSessionActivityObservation pending,
        bool runnerRemoved) =>
        pending.RunnerRemoved == runnerRemoved
        && string.Equals(pending.RunnerId, current.Runtime.RunnerId, StringComparison.Ordinal)
        && string.Equals(pending.Runtime, current.Runtime.Runtime ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(pending.RuntimeSessionId, current.Status.AgentRuntimeSessionId ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(pending.WorkDir, current.Runtime.WorkDir ?? string.Empty, StringComparison.Ordinal)
        && pending.BindingEpoch == current.BindingEpoch
        && pending.ContextGeneration == current.Status.ContextGeneration
        && pending.CapturedActivity == current.Status.Activity
        && ObservationSnapshotUnchanged(current, pending);

    private static bool MatchesObservationTarget(
        AgentSessionActivityObservation pending,
        RunnerSessionActivityProbeRequest probe) =>
        string.Equals(pending.ObservationId, probe.ObservationId, StringComparison.Ordinal)
        && string.Equals(pending.RunnerId, probe.RunnerId, StringComparison.Ordinal)
        && string.Equals(pending.Runtime, probe.Runtime, StringComparison.Ordinal)
        && string.Equals(pending.RuntimeSessionId, probe.RuntimeSessionId, StringComparison.Ordinal)
        && string.Equals(pending.WorkDir, probe.WorkDir, StringComparison.Ordinal)
        && pending.BindingEpoch == probe.BindingEpoch
        && pending.ContextGeneration == probe.ContextGeneration;

    private static bool CurrentBindingMatchesObservation(
        AgentSession current,
        RunnerSessionActivityProbeRequest probe) =>
        string.Equals(current.Runtime.RunnerId, probe.RunnerId, StringComparison.Ordinal)
        && string.Equals(current.Runtime.Runtime ?? string.Empty, probe.Runtime, StringComparison.Ordinal)
        && string.Equals(current.Status.AgentRuntimeSessionId ?? string.Empty, probe.RuntimeSessionId, StringComparison.Ordinal)
        && string.Equals(current.Runtime.WorkDir ?? string.Empty, probe.WorkDir, StringComparison.Ordinal)
        && current.BindingEpoch == probe.BindingEpoch
        && current.Status.ContextGeneration == probe.ContextGeneration;

    private static bool ObservationSnapshotUnchanged(
        AgentSession current,
        AgentSessionActivityObservation pending) =>
        pending.InputSequence == CurrentInputSequence(current)
        && pending.Turns.SequenceEqual(CurrentTurnSnapshots(current))
        && pending.Followups.SequenceEqual(CurrentFollowupSnapshots(current))
        && Equals(pending.Reset, CurrentResetSnapshot(current))
        && Equals(pending.Stop, CurrentStopSnapshot(current));

    /// <summary>
    /// True only when the Turn's own lease proves it never reached dispatch.
    /// Logical acceptance is not an external-effect boundary; sealing or
    /// dispatching is, and a missing lease proves nothing.
    /// </summary>
    private static bool FollowupLeaseProvesUndispatched(AgentSession current, string turnId)
    {
        var leases = current.Status.PendingFollowups is { Count: > 0 } pending
            ? pending
            : current.Status.PendingFollowup is { } single ? (IReadOnlyList<AgentSessionFollowupLease>)[single] : [];
        var lease = leases.FirstOrDefault(candidate => string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal));
        return lease is not null && !lease.Dispatching && !lease.PayloadSealed;
    }

    /// <summary>
    /// True while a current-generation fact is still unresolved: a live Turn
    /// that is queued, executing or uncertain, or an operation still in
    /// flight. An administrative removal may capture those even when the
    /// derived Activity already reads idle.
    /// </summary>
    private static bool HasUnresolvedCurrentFacts(AgentSession current)
    {
        foreach (var turn in current.Status.Turns ?? [])
        {
            if (turn.SupersededAt is not null
                || turn.ContextGeneration != current.Status.ContextGeneration) continue;
            if (turn.Status is AgentTurnStatus.Queued
                or AgentTurnStatus.Executing
                or AgentTurnStatus.Unknown)
                return true;
        }
        if (current.Status.PendingStop is { IsActive: true }) return true;
        if (current.Status.PendingReset is { Outcome: null }) return true;
        return CurrentFollowupSnapshots(current).Count > 0;
    }

    private static long CurrentInputSequence(AgentSession current) =>
        current.Status.Inputs is { Count: > 0 } inputs
            ? inputs.Max(input => input.Sequence)
            : 0;

    private static IReadOnlyList<AgentSessionActivityTurnSnapshot> CurrentTurnSnapshots(AgentSession current) =>
        (current.Status.Turns ?? [])
            .Select(turn => new AgentSessionActivityTurnSnapshot(
                turn.Id,
                turn.Status,
                turn.ContextGeneration,
                turn.SupersededAt))
            .ToArray();

    private static IReadOnlyList<AgentSessionActivityFollowupSnapshot> CurrentFollowupSnapshots(AgentSession current)
    {
        var leases = current.Status.PendingFollowups is { Count: > 0 } pending
            ? pending
            : current.Status.PendingFollowup is { } single ? (IReadOnlyList<AgentSessionFollowupLease>)[single] : [];
        return leases.Select(lease => new AgentSessionActivityFollowupSnapshot(
            lease.OperationId,
            lease.TurnId,
            lease.Accepted,
            lease.Dispatching,
            lease.PayloadSealed,
            lease.ConcurrencyGateStatus)).ToArray();
    }

    private static AgentSessionActivityResetSnapshot? CurrentResetSnapshot(AgentSession current) =>
        current.Status.PendingReset is { } reset
            ? new AgentSessionActivityResetSnapshot(
                reset.OperationId,
                reset.EffectAdmitted,
                reset.Outcome is not null,
                reset.SupersededAt)
            : null;

    private static AgentSessionActivityStopSnapshot? CurrentStopSnapshot(AgentSession current) =>
        current.Status.PendingStop is { } stop
            ? new AgentSessionActivityStopSnapshot(
                stop.OperationId,
                stop.TurnId,
                stop.DispatchStarted,
                stop.Disposition,
                stop.SupersededAt)
            : null;

    private static HashSet<string> CapturedOperationIds(AgentSessionActivityObservation pending)
    {
        var ids = pending.Followups.Select(followup => followup.OperationId).ToHashSet(StringComparer.Ordinal);
        if (pending.Reset is { } reset) ids.Add(reset.OperationId);
        if (pending.Stop is { } stop) ids.Add(stop.OperationId);
        return ids;
    }

    private static string? TurnOperationId(AgentSession current, string turnId)
    {
        var leases = current.Status.PendingFollowups is { Count: > 0 } pending
            ? pending
            : current.Status.PendingFollowup is { } single ? (IReadOnlyList<AgentSessionFollowupLease>)[single] : [];
        return leases
            .FirstOrDefault(lease => string.Equals(lease.TurnId, turnId, StringComparison.Ordinal))
            ?.OperationId;
    }
}
