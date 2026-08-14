using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Creates the complete initial WorkflowRun binding in one store transaction.
/// </summary>
public sealed class WorkflowRunBindingParticipant : Grain, IWorkflowRunBindingParticipant
{
    private readonly IWorkflowRunStore _runs;

    public WorkflowRunBindingParticipant(IWorkflowRunStore runs)
    {
        _runs = runs;
    }

    public async Task<WorkflowRunBindingResult> GetBindingAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun request)
    {
        var run = await _runs.LoadAsync(this.GetPrimaryKeyString());
        if (run is null)
            return new WorkflowRunBindingResult(WorkflowRunBindingOutcome.RunNotFound);
        if (!StartupRequestMatches(run, request))
        {
            return new WorkflowRunBindingResult(
                WorkflowRunBindingOutcome.Conflict,
                ToBinding(run),
                $"WorkflowRun '{run.Id}' already has conflicting startup facts");
        }
        return new WorkflowRunBindingResult(WorkflowRunBindingOutcome.AlreadyApplied, ToBinding(run));
    }

    public async Task<WorkflowRunBindingResult> BindAsync(
        BoundWorkflowStart payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var existing = await _runs.LoadAsync(payload.WorkflowRunId);
        if (existing is not null)
        {
            var binding = ToBinding(existing);
            if (!BindingMatches(binding, payload))
            {
                return new WorkflowRunBindingResult(
                    WorkflowRunBindingOutcome.Conflict,
                    binding,
                    $"WorkflowRun '{payload.WorkflowRunId}' already has conflicting startup facts");
            }
            return new WorkflowRunBindingResult(WorkflowRunBindingOutcome.AlreadyApplied, binding);
        }

        var structure = new WorkflowStructure(
            payload.ProfileId,
            payload.Stages.Select(stage => new StageStructure(stage.Stage, stage.RequiresApproval)).ToList());
        var run = WorkflowRun.Create(payload.WorkflowRunId, structure, payload.Metadata.CreatedAt, payload.Metadata);
        run.ExplicitWorkflowProfileId = payload.ExplicitProfileId;
        run.AgentAction = payload.AgentAction;
        run.Workspace = payload.Workspace;
        await _runs.SaveAsync(run);
        return new WorkflowRunBindingResult(WorkflowRunBindingOutcome.Applied, ToBinding(run) with
        {
            ExplicitProfileId = payload.ExplicitProfileId,
        });
    }

    private static bool StartupRequestMatches(
        WorkflowRun run,
        WorkflowProfileCommandPayload.BindWorkflowRun request) =>
        string.Equals(run.Metadata.ProjectId, request.ProjectId, StringComparison.Ordinal)
        && run.Metadata.IssueNumber == request.IssueNumber
        && run.Metadata.EpicNumber == request.EpicNumber
        && string.Equals(run.ExplicitWorkflowProfileId, request.ExplicitProfileId, StringComparison.Ordinal)
        && MetadataMatches(run.Metadata, request.Metadata)
        && Equals(run.Workspace, request.Workspace)
        && (request.Bound is null || BindingMatches(ToBinding(run), request.Bound));

    private static bool BindingMatches(BoundWorkflowStart existing, BoundWorkflowStart requested) =>
        string.Equals(existing.ProjectId, requested.ProjectId, StringComparison.Ordinal)
        && existing.IssueNumber == requested.IssueNumber
        && existing.EpicNumber == requested.EpicNumber
        && string.Equals(existing.ExplicitProfileId, requested.ExplicitProfileId, StringComparison.Ordinal)
        && string.Equals(existing.ProfileId, requested.ProfileId, StringComparison.Ordinal)
        && string.Equals(existing.AgentAction, requested.AgentAction, StringComparison.Ordinal)
        && existing.Stages.SequenceEqual(requested.Stages)
        && MetadataMatches(existing.Metadata, requested.Metadata)
        && Equals(existing.Workspace, requested.Workspace);

    private static bool MetadataMatches(WorkflowRunMetadata existing, WorkflowRunMetadata requested) =>
        string.Equals(existing.Name, requested.Name, StringComparison.Ordinal)
        && existing.ProjectId == requested.ProjectId
        && existing.IssueNumber == requested.IssueNumber
        && existing.EpicNumber == requested.EpicNumber
        && DictionaryMatches(existing.Labels, requested.Labels)
        && DictionaryMatches(existing.Annotations, requested.Annotations);

    private static bool DictionaryMatches(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyDictionary<string, string>? requested)
    {
        if (ReferenceEquals(existing, requested)) return true;
        if (existing is null || requested is null || existing.Count != requested.Count) return false;
        return existing.All(pair => requested.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static BoundWorkflowStart ToBinding(WorkflowRun run) => new(
        run.Id,
        run.Metadata.ProjectId ?? string.Empty,
        run.Metadata.IssueNumber,
        run.Metadata.EpicNumber,
        run.ExplicitWorkflowProfileId,
        run.WorkflowProfileId ?? string.Empty,
        run.AgentAction,
        run.Stages.Select(stage => new BoundStageStructure(stage.Id, stage.RequiresApproval)).ToList(),
        run.Metadata,
        run.Workspace);
}
