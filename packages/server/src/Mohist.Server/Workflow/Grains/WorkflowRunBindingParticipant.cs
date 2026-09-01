using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
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

        _ = ParseAndValidateDefinition(payload);
        if (payload.VerificationCommand is not null)
            ProjectVerificationCommand.Require(payload.VerificationCommand);
        if (WorkflowProfileCatalog.IsSystemProfile(payload.ProfileId))
            ProjectVerificationCommand.Require(payload.VerificationCommand);

        var structure = new WorkflowStructure(
            payload.ProfileId,
            payload.Stages.Select(stage => new StageStructure(stage.Stage, stage.RequiresApproval)).ToList());
        var run = WorkflowRun.Create(payload.WorkflowRunId, structure, payload.Metadata.CreatedAt, payload.Metadata);
        run.ExplicitWorkflowProfileId = payload.ExplicitProfileId;
        run.Workspace = payload.Workspace;
        run.BoundWorkflowDefinitionJson = payload.DefinitionJson;
        run.VerificationCommand = payload.VerificationCommand;
        await _runs.SaveAsync(run);
        return new WorkflowRunBindingResult(WorkflowRunBindingOutcome.Applied, ToBinding(run) with
        {
            ExplicitProfileId = payload.ExplicitProfileId,
        });
    }

    private static WorkflowDefinition ParseAndValidateDefinition(BoundWorkflowStart payload)
    {
        if (string.IsNullOrWhiteSpace(payload.DefinitionJson))
            throw new ArgumentException("A bound workflow definition snapshot is required", nameof(payload));

        WorkflowDefinition definition;
        try
        {
            definition = WorkflowYamlSerializer.FromJson(payload.DefinitionJson);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("The bound workflow definition snapshot is invalid", nameof(payload), ex);
        }

        var stages = payload.Stages ?? throw new ArgumentException("Bound workflow stages are required", nameof(payload));
        if (definition.Stages.Count != stages.Count
            || definition.Stages.Select(stage => new BoundStageStructure(stage.Stage, stage.RequiresApproval))
                .SequenceEqual(stages) is false)
        {
            throw new ArgumentException(
                "Bound workflow definition stages do not match the startup stage list",
                nameof(payload));
        }

        return definition;
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
        && string.Equals(run.VerificationCommand, request.VerificationCommand, StringComparison.Ordinal)
        && (request.Bound is null || BindingMatches(ToBinding(run), request.Bound));

    private static bool BindingMatches(BoundWorkflowStart existing, BoundWorkflowStart requested) =>
        string.Equals(existing.ProjectId, requested.ProjectId, StringComparison.Ordinal)
        && existing.IssueNumber == requested.IssueNumber
        && existing.EpicNumber == requested.EpicNumber
        && string.Equals(existing.ExplicitProfileId, requested.ExplicitProfileId, StringComparison.Ordinal)
        && string.Equals(existing.ProfileId, requested.ProfileId, StringComparison.Ordinal)
        && existing.Stages.SequenceEqual(requested.Stages)
        && MetadataMatches(existing.Metadata, requested.Metadata)
        && Equals(existing.Workspace, requested.Workspace)
        && string.Equals(existing.DefinitionJson, requested.DefinitionJson, StringComparison.Ordinal)
        && string.Equals(existing.VerificationCommand, requested.VerificationCommand, StringComparison.Ordinal);

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
        run.Stages.Select(stage => new BoundStageStructure(stage.Id, stage.RequiresApproval)).ToList(),
        run.Metadata,
        run.Workspace,
        DefinitionJson: run.BoundWorkflowDefinitionJson,
        VerificationCommand: run.VerificationCommand);
}
