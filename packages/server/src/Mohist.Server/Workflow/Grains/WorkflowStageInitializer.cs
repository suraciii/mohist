using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Enforces the commit invariant <c>StageStarted => Initialized</c>. Empty
/// stages may advance immediately, so one commit can materialize multiple
/// newly started stages.
/// </summary>
internal sealed class WorkflowStageInitializer
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowStageInitializer(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

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

            var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(
                _owner.GrainKey, pendingStart.Stage,
                _owner.GetProjectId(),
                _owner.GetIssueNumber(),
                _owner.GetWorkflowProfileId());
            var initEvents = run.InitializeStage(
                stageDef.Tasks,
                stageDef.Checks.ToList(),
                _owner.Now(),
                advance: true);
            materialized.AddRange(initEvents);
        }

        return materialized;
    }
}
