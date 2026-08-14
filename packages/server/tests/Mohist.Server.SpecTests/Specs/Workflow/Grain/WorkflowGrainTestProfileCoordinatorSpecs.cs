using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Orleans;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Direct grain tests need the production profile-binding call to succeed
/// without a full Orleans cluster or profile-coordinator activation.
/// </summary>
internal sealed class WorkflowGrainTestProfileCoordinatorFactory : IGrainFactory
{
    private readonly IWorkflowProfileReferenceCoordinatorGrain _stub;

    public WorkflowGrainTestProfileCoordinatorFactory(
        IWorkflowRunStore runs,
        WorkflowDefinitionResolver resolver)
    {
        _stub = new WorkflowGrainTestProfileCoordinator(runs, resolver);
    }

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
    {
        if (typeof(TGrainInterface) == typeof(IWorkflowProfileReferenceCoordinatorGrain))
            return (TGrainInterface)(object)_stub;
        throw new NotSupportedException(
            $"{nameof(WorkflowGrainTestProfileCoordinatorFactory)} does not support {typeof(TGrainInterface).Name}");
    }

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix) =>
        throw new NotSupportedException();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix) =>
        throw new NotSupportedException();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix) =>
        throw new NotSupportedException();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix) =>
        throw new NotSupportedException();

    TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) =>
        throw new NotSupportedException();

    void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) =>
        throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) =>
        throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey) =>
        throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey) =>
        throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) =>
        throw new NotSupportedException();

    IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) =>
        throw new NotSupportedException();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) =>
        throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(GrainId grainId) =>
        throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType) =>
        throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) =>
        throw new NotSupportedException();

    IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey) =>
        throw new NotSupportedException();
}

internal sealed class WorkflowGrainTestProfileCoordinator(
    IWorkflowRunStore runs,
    WorkflowDefinitionResolver resolver) : IWorkflowProfileReferenceCoordinatorGrain
{
    public Task<WorkflowProfileReferenceResult> SetProjectDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision) =>
        throw new NotSupportedException();

    public async Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision)
    {
        var existing = await runs.LoadAsync(payload.WorkflowRunId);
        if (existing is not null)
        {
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.AlreadyApplied,
                existing.WorkflowProfileId ?? string.Empty,
                expectedRevision ?? 1L,
                Binding: ToBinding(existing));
        }

        var profile = (await resolver.LoadTemplateAsync(
            payload.WorkflowRunId,
            payload.ProjectId,
            payload.IssueNumber)).Profile
            ?? throw new InvalidOperationException("Test Profile resolver returned no Profile");
        var bound = new BoundWorkflowStart(
            payload.WorkflowRunId,
            payload.ProjectId,
            payload.IssueNumber,
            payload.EpicNumber,
            payload.ExplicitProfileId,
            profile.Id,
            profile.AgentAction,
            profile.Definition.Stages
                .Select(stage => new BoundStageStructure(stage.Stage, stage.RequiresApproval))
                .ToList(),
            payload.Metadata,
            payload.Workspace);
        var structure = new WorkflowStructure(
            bound.ProfileId,
            bound.Stages.Select(stage => new StageStructure(stage.Stage, stage.RequiresApproval)).ToList());
        var run = WorkflowRun.Create(payload.WorkflowRunId, structure, payload.Metadata.CreatedAt, payload.Metadata);
        run.ExplicitWorkflowProfileId = payload.ExplicitProfileId;
        run.AgentAction = bound.AgentAction;
        run.Workspace = payload.Workspace;
        await runs.SaveAsync(run);

        return new WorkflowProfileReferenceResult(
            WorkflowProfileReferenceResultCode.Applied,
            bound.ProfileId,
            expectedRevision ?? 1L,
            Binding: bound);
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

    public Task<WorkflowProfileReferenceResult> DeleteProfileAsync(
        WorkflowProfileCommandPayload.DeleteProfile payload,
        string commandId,
        long? expectedRevision) =>
        throw new NotSupportedException();

    public Task<WorkflowProfileReferenceResult> SetAgentActionOverrideAsync(
        WorkflowProfileCommandPayload.SetAgentActionOverride payload,
        string commandId,
        long? expectedRevision) => throw new NotSupportedException();

    public Task<WorkflowProfileSaveResult> UpdateProfileAsync(
        WorkflowProfileCommandPayload.UpdateProfile payload,
        string commandId,
        long? expectedRevision) => throw new NotSupportedException();
}
