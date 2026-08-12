using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Direct grain tests need the production profile-binding call to succeed
/// without a full Orleans cluster or profile-coordinator activation.
/// </summary>
internal sealed class WorkflowGrainTestProfileCoordinatorFactory : IGrainFactory
{
    private static readonly IWorkflowProfileReferenceCoordinatorGrain Stub = new WorkflowGrainTestProfileCoordinator();

    TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
    {
        if (typeof(TGrainInterface) == typeof(IWorkflowProfileReferenceCoordinatorGrain))
            return (TGrainInterface)(object)Stub;
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

internal sealed class WorkflowGrainTestProfileCoordinator : IWorkflowProfileReferenceCoordinatorGrain
{
    public Task<WorkflowProfileReferenceResult> SetProjectDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision) =>
        throw new NotSupportedException();

    public Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision) =>
        Task.FromResult(new WorkflowProfileReferenceResult(
            WorkflowProfileReferenceResultCode.Applied,
            payload.ProfileId,
            expectedRevision ?? 1L));

    public Task<WorkflowProfileReferenceResult> DeleteProfileAsync(
        WorkflowProfileCommandPayload.DeleteProfile payload,
        string commandId,
        long? expectedRevision) =>
        throw new NotSupportedException();
}
