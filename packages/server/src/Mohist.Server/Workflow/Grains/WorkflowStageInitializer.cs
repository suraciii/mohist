using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Command-side initializer for fresh stages. Materializes any
/// <see cref="StageStarted"/> event in a commit batch by loading fresh stage
/// specs and calling <see cref="WorkflowRunExtensions.InitializeStage"/>.
/// Maintains the invariant <c>StageStarted ⟹ Initialized</c>: a stage is
/// always initialized before its <see cref="StageStarted"/> is persisted or
/// surfaced via <see cref="WorkflowRunExtensions.NextWork"/>. The merged
/// events are returned so the caller commits them in a single batch with
/// the original events (which preserves event ordering for downstream
/// subscribers).
///
/// Composed inside the grain process (mirrors <see cref="WorkflowReadModel"/>):
/// the grain is the consistency boundary for <see cref="WorkflowRun"/> and
/// the init step must observe the in-memory run with the same
/// strong-consistency guarantee as the rest of the command surface. The
/// initializer only reads <c>_run</c> via the grain's internal accessor and
/// mutates <c>run.Stages</c> through <c>InitializeStage</c>; it does not
/// touch session health, write <c>_lastKnownRunnerId</c>, or invoke
/// <c>SaveRunAsync</c> directly (the grain owns the save path). No new
/// async yield points are introduced: the only await on this path is the
/// pre-existing <see cref="Services.WorkflowProfileManager.LoadStageSpecsAsync"/>
/// profile load.
///
/// The loop terminates when no further <see cref="StageStarted"/> is emitted
/// — each <c>InitializeStage</c> → <c>Advance</c> may auto-skip an empty
/// stage and emit another <see cref="StageStarted"/> for the next stage,
/// which must also be initialized. Already-handled stages are tracked in a
/// hash set so the same stage is initialized at most once per call.
/// </summary>
public sealed class WorkflowStageInitializer
{
    private readonly WorkflowGrain _owner;

    public WorkflowStageInitializer(WorkflowGrain owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Pre-commit step that materializes any <see cref="StageStarted"/> event
    /// by loading fresh stage specs and calling
    /// <see cref="WorkflowRunExtensions.InitializeStage"/>. The merged events
    /// are returned so the caller commits them in a single batch with the
    /// original events. When <c>_run</c> is null the original event list is
    /// returned unchanged — no stage spec is touched.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowEvent>> InitializeFreshStagesAsync(IReadOnlyList<WorkflowEvent> events)
    {
        var run = _owner.RunOrNull;
        if (run is null) return events;

        var materialized = new List<WorkflowEvent>(events);
        var initializedStages = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            StageStarted? pendingStart = null;
            foreach (var e in materialized)
            {
                if (e is StageStarted started
                    && !initializedStages.Contains(started.Stage))
                {
                    var stageRun = run.Stages.FirstOrDefault(s => string.Equals(s.Id, started.Stage, StringComparison.Ordinal));
                    if (stageRun is { Initialized: false })
                    {
                        pendingStart = started;
                        break;
                    }
                }
            }

            if (pendingStart is null) break;

            initializedStages.Add(pendingStart.Stage);

            var projectId = _owner.GetProjectId();
            var issueId = _owner.GetIssueId();
            var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(
                _owner.GrainKey, pendingStart.Stage,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId,
                string.IsNullOrWhiteSpace(issueId) ? null : issueId);
            var initEvents = run.InitializeStage(stageDef.Tasks, stageDef.Checks);
            materialized.AddRange(initEvents);
        }

        return materialized;
    }
}