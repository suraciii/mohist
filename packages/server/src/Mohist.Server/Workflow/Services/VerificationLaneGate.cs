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
    /// Returns the next lane index whose durable outcome is not <c>pass</c>,
    /// or -1 when all six lanes have a durable pass. Used by ordered
    /// dispatch and recovery to skip already-passing lanes and gate on the
    /// first non-passing one. The catalog order is authoritative; runs without a
    /// snapshot return -1 so legacy runs never enter the lane gate.
    /// </summary>
    public static int FirstNonPassingLaneIndex(WorkflowRun run)
    {
        if (!IsLaneEnabledRun(run)) return -1;

        var laneAttempts = new Dictionary<string, VerificationLaneAttempt>(StringComparer.Ordinal);
        foreach (var attempt in WorkflowBoundDefinitionResolver.CollectLaneAttempts(run))
            laneAttempts[attempt.LaneId] = attempt;

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