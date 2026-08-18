using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Resolves stage specifications for snapshot-backed runs from
/// <see cref="WorkflowRun.BoundWorkflowDefinitionJson"/> and exposes
/// whether a run's bound build stage constitutes a lane-enabled verification
/// run.
///
/// Snapshot-backed runs MUST NOT call the live profile provider for stage or
/// lock resolution; this resolver is the only allowed path. Pre-snapshot runs
/// (no <see cref="WorkflowRun.BoundWorkflowDefinitionJson"/>) are explicit
/// legacy mode and fall back to the retained pre-change aggregate definition
/// for affected built-in profiles; lane predicates are NOT evaluated for them.
/// </summary>
public static class WorkflowBoundDefinitionResolver
{
    /// <summary>
    /// Result of resolving stage specs for a workflow run. The
    /// <see cref="IsLaneEnabled"/> flag is meaningful only when
    /// <see cref="Source"/> is <see cref="BoundDefinitionSource.Snapshot"/>;
    /// legacy and missing sources always return <c>false</c> so callers never
    /// apply the lane gate to a run that has no six-lane sequence.
    /// </summary>
    public sealed record StageSpecResult(
        StageDefinition Stage,
        BoundDefinitionSource Source,
        bool IsLaneEnabled,
        WorkflowDefinition? Definition);

    public enum BoundDefinitionSource
    {
        /// <summary>Resolved from the run's persisted bound-definition snapshot.</summary>
        Snapshot = 0,
        /// <summary>Run has no snapshot; legacy aggregate definition retained for the affected built-in profile.</summary>
        LegacyAggregate = 1,
        /// <summary>Run has no snapshot and no retained legacy definition; stage cannot be resolved.</summary>
        Missing = 2,
    }

    /// <summary>
    /// Resolves the stage specification for a workflow run. The
    /// <paramref name="boundStageId"/> selector matches by stage id. Snapshot
    /// takes priority; legacy aggregate fallback applies only when the run's
    /// bound profile id matches a retained legacy built-in profile and the
    /// caller is asking for a stage the legacy definition actually contains.
    /// </summary>
    public static StageSpecResult ResolveStage(
        WorkflowRun run,
        string stageId)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(stageId);

        var snapshotResult = TryResolveSnapshotStage(run, stageId);
        if (snapshotResult is not null) return snapshotResult;

        if (!string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson))
        {
            // Run claims to be snapshot-backed but no stage matched; surface the
            // missing state rather than silently falling through to legacy.
            return new StageSpecResult(
                Stage: null!,
                Source: BoundDefinitionSource.Missing,
                IsLaneEnabled: false,
                Definition: null);
        }

        var legacy = RetainedLegacyAggregate.TryGetLegacyDefinition(run.WorkflowProfileId, stageId);
        if (legacy is not null)
        {
            return new StageSpecResult(
                Stage: legacy,
                Source: BoundDefinitionSource.LegacyAggregate,
                IsLaneEnabled: false,
                Definition: null);
        }

        return new StageSpecResult(
            Stage: null!,
            Source: BoundDefinitionSource.Missing,
            IsLaneEnabled: false,
            Definition: null);
    }

    private static StageSpecResult? TryResolveSnapshotStage(WorkflowRun run, string stageId)
    {
        if (string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson)) return null;

        WorkflowDefinition definition;
        try
        {
            definition = WorkflowYamlSerializer.FromJson(run.BoundWorkflowDefinitionJson);
        }
        catch (Exception ex)
        {
            throw new WorkflowDefinitionResolutionException(
                WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition,
                $"Workflow '{run.Id}' has an unreadable BoundWorkflowDefinitionJson: {ex.Message}");
        }

        var stage = definition.Stages
            .FirstOrDefault(s => string.Equals(s.Stage, stageId, StringComparison.Ordinal));
        if (stage is null) return null;

        var isLaneEnabled = IsLaneEnabledBuildStage(definition);
        return new StageSpecResult(
            Stage: stage,
            Source: BoundDefinitionSource.Snapshot,
            IsLaneEnabled: isLaneEnabled,
            Definition: definition);
    }

    /// <summary>
    /// True when the bound definition's <c>build</c> stage carries the
    /// complete six-lane sequence in catalog order, with each task using
    /// <c>core/script</c>. Profile edits after binding cannot change this:
    /// the predicate reads the snapshot captured at run-bind time.
    /// </summary>
    public static bool IsLaneEnabledBuildStage(WorkflowDefinition definition)
    {
        var build = definition.Stages.FirstOrDefault(s => string.Equals(s.Stage, "build", StringComparison.Ordinal));
        if (build is null) return false;
        if (build.Tasks.Count != VerificationLaneCatalog.LaneIds.Count) return false;
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var task = build.Tasks[i];
            var expectedId = VerificationLaneCatalog.LaneIds[i];
            if (!string.Equals(task.Id, expectedId, StringComparison.Ordinal)) return false;
            if (!string.Equals(task.Uses, "core/script", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Walks every stage's tasks and returns the lane attempts persisted on
    /// recognized lane tasks. Order is preserved as recorded; callers should
    /// use <see cref="VerificationLaneCatalog.OrderOf"/> to derive canonical
    /// order. The returned list may be sparse when some lane tasks have not
    /// started or have not yet reported.
    /// </summary>
    public static IReadOnlyList<VerificationLaneAttempt> CollectLaneAttempts(WorkflowRun run)
    {
        var attempts = new List<VerificationLaneAttempt>();
        foreach (var stage in run.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                if (task.Lane is not null) attempts.Add(task.Lane);
            }
        }
        return attempts;
    }
}