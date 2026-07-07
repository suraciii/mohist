using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Acquires and releases sequential stage locks for the grain-owned run.
/// </summary>
internal sealed class WorkflowStageLockCoordinator
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowStageLockCoordinator(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Returns false only when another workflow currently holds the declared
    /// sequential resource.
    /// </summary>
    public async Task<bool> AcquireStageLocksIfNeededAsync(string stage)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return true;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException($"Workflow '{_owner.GrainKey}' stage '{stage}' requires resource '{resource}' but project id is missing");

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = _owner.Grains.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.AcquireSequentialAsync(new StageLockRequest(_owner.GrainKey, stage, resource, projectId));

        return result.Acquired;
    }

    public async Task ReleaseCurrentStageLocksAsync(string reason)
    {
        if (_owner.RunOrNull?.CurrentStageId is null) return;
        await ReleaseStageLocksAsync(_owner.RunOrNull.CurrentStageId, reason);
    }

    /// <summary>
    /// Releases this workflow's lock for the given stage, if that stage uses
    /// a sequential resource.
    /// </summary>
    public async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = _owner.Grains.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.ReleaseAsync(new StageLockOwner(_owner.GrainKey, stage));

        // Pull scheduling rediscovers assignable runs from persisted state.
        _ = result.NextWorkflowRunId;
    }

    /// <summary>
    /// Returns the first sequential resource for a stage, or null when no lock
    /// is required.
    /// </summary>
    private async Task<string?> GetSequentialLockResourceAsync(string stage)
    {
        var stageDef = await _owner.ProfileManager.LoadStageSpecsAsync(_owner.GrainKey, stage);
        if (stageDef.LockBehavior is null) return null;
        if (!string.Equals(stageDef.LockBehavior, "sequential", StringComparison.OrdinalIgnoreCase))
            return null;
        return stageDef.Resources?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }
}
