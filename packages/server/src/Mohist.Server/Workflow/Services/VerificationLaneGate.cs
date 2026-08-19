using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Verifies whether the verification-lane gate allows ordered dispatch and
/// stage advancement for a lane-enabled run. Legacy runs (no bound
/// snapshot or snapshot with the aggregate <c>verify</c> task) never enter
/// the gate; only a snapshot-backed run whose build stage carries the six
/// recognized lanes is gated.
///
/// The gate keeps earlier passing lanes stable: only a direct successful
/// retry attempt for a failed or timed-out lane can change the lane to
/// <c>pass</c>. Recovery helpers (<c>recover:fix-ci</c>) are intentionally
/// outside the lane catalog and cannot promote a lane to <c>pass</c>.
/// </summary>
public static class VerificationLaneGate
{
    /// <summary>
    /// True when the run's bound definition constitutes a lane-enabled build
    /// stage. Profile edits after binding cannot change this answer: the
    /// snapshot reads the bound definition, not the live profile.
    /// </summary>
    public static bool IsLaneEnabledRun(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson)) return false;
        try
        {
            var definition = WorkflowYamlSerializer.FromJson(run.BoundWorkflowDefinitionJson);
            return WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns one authoritative lane view per catalog id. A pending retry
    /// keeps the prior failed or timed-out evidence as the lane outcome while
    /// exposing the retry's new attempt identity. A later direct pass replaces
    /// that evidence; a repair helper never enters this map because it has no
    /// lane metadata.
    /// </summary>
    public static IReadOnlyDictionary<string, VerificationLaneAttempt> AuthoritativeLaneAttempts(WorkflowRun run)
    {
        if (!IsLaneEnabledRun(run))
            return new Dictionary<string, VerificationLaneAttempt>(StringComparer.Ordinal);

        var result = new Dictionary<string, VerificationLaneAttempt>(StringComparer.Ordinal);
        foreach (var group in WorkflowBoundDefinitionResolver
            .CollectLaneAttempts(run)
            .GroupBy(attempt => attempt.LaneId, StringComparer.Ordinal))
        {
            var latest = group.Last();
            if (latest.Outcome == VerificationLaneOutcome.Pending)
            {
                var latestTerminal = group.LastOrDefault(attempt =>
                    attempt.Outcome is VerificationLaneOutcome.Pass
                        or VerificationLaneOutcome.Fail
                        or VerificationLaneOutcome.Timeout);
                if (latestTerminal is not null
                    && latestTerminal.Outcome is VerificationLaneOutcome.Fail or VerificationLaneOutcome.Timeout)
                {
                    latest = latest with
                    {
                        Outcome = latestTerminal.Outcome,
                        Error = latestTerminal.Error,
                        Detail = latestTerminal.Detail,
                        FinishedAt = latestTerminal.FinishedAt,
                    };
                }
            }
            result[group.Key] = latest;
        }
        return result;
    }

    /// <summary>
    /// Returns the next lane index whose authoritative outcome is not
    /// <c>pass</c>, or -1 when all six lanes have a durable pass. The catalog
    /// order is authoritative; runs without a snapshot return -1 so legacy
    /// runs never enter the lane gate.
    /// </summary>
    public static int FirstNonPassingLaneIndex(WorkflowRun run)
    {
        if (!IsLaneEnabledRun(run)) return -1;

        var laneAttempts = AuthoritativeLaneAttempts(run);
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var laneId = VerificationLaneCatalog.LaneIds[i];
            if (!laneAttempts.TryGetValue(laneId, out var attempt)
                || attempt.Outcome != VerificationLaneOutcome.Pass)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// True when the build stage may advance: every recognized lane has a
    /// durable pass, or the run is not lane-enabled (legacy aggregate path
    /// applies the existing dispatch and gate behavior). Legacy runs MUST
    /// NOT wait for synthesized lane state.
    /// </summary>
    public static bool CanAdvanceBuildStage(WorkflowRun run)
    {
        if (!IsLaneEnabledRun(run)) return true;
        return FirstNonPassingLaneIndex(run) < 0;
    }

    /// <summary>
    /// Returns true when the supplied task is a verification lane task the
    /// dispatcher may claim right now. A later catalog lane is blocked while
    /// an earlier lane has not yet reached a durable pass outcome. Non-lane
    /// tasks are always eligible.
    /// </summary>
    public static bool IsClaimableLaneTask(WorkflowRun run, TaskRun task)
    {
        if (!IsLaneEnabledRun(run)) return true;
        if (!VerificationLaneCatalog.IsKnownLane(task.DefinitionId)) return true;

        var laneOrder = VerificationLaneCatalog.OrderOf(task.DefinitionId);
        if (laneOrder < 0) return true;

        // The first non-passing lane is the only claimable lane task; every
        // later lane is blocked until that one passes.
        return laneOrder == FirstNonPassingLaneIndex(run);
    }
}