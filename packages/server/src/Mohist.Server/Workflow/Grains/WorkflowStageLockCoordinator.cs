using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

internal sealed class WorkflowStageLockCoordinator
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowStageLockCoordinator(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

    public async Task<bool> AcquireStageLocksIfNeededAsync(string stage)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return true;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException($"Workflow '{_owner.GrainKey}' stage '{stage}' requires resource '{resource}' but project id is missing");

        var key = BuildLockKey(projectId, resource);
        var lockGrain = _owner.Grains.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.AcquireSequentialAsync(new StageLockRequest(_owner.GrainKey, stage, resource, projectId));

        return result.Acquired;
    }

    public async Task ReleaseCurrentStageLocksAsync(string reason)
    {
        if (_owner.RunOrNull?.CurrentStageId is null) return;
        await ReleaseStageLocksAsync(_owner.RunOrNull.CurrentStageId, reason);
    }

    public async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var key = BuildLockKey(projectId, resource);
        var lockGrain = _owner.Grains.GetGrain<IWorkflowStageLockGrain>(key);
        await lockGrain.ReleaseAsync(new StageLockOwner(_owner.GrainKey, stage));
    }

    private async Task<string?> GetSequentialLockResourceAsync(string stage)
    {
        var stageDef = await _owner.DefinitionResolver.LoadStageSpecsAsync(
            _owner.GrainKey, stage, _owner.GetProjectId(), _owner.GetIssueNumber(), _owner.GetWorkflowProfileId());
        if (stageDef.LockBehavior is null) return null;
        if (!string.Equals(stageDef.LockBehavior, "sequential", StringComparison.OrdinalIgnoreCase))
            return null;
        return stageDef.Resources?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }

    private string BuildLockKey(string projectId, string resource)
    {
        var repository = _owner.RunOrNull?.Repository;
        if (repository is not null
            && _owner.GetIssueNumber() is not null
            && string.Equals(resource, "project-integration", StringComparison.Ordinal))
            return WorkflowStageLockKeys.ForProjectRepositoryResource(projectId, repository.Name, resource);

        return WorkflowStageLockKeys.ForProjectResource(projectId, resource);
    }
}
