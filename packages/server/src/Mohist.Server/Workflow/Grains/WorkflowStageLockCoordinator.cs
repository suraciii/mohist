using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Command-side coordinator for the sequential stage lock: resolves the
/// resource, acquires the lock for the current stage on dispatch, releases
/// the lock owned by the current stage on retry/rerun/stop, and exposes the
/// <c>ReleaseStageLocksAsync(stage, reason)</c> path consumed by the bus-side
/// <c>WorkflowStageLockReleaseHandler</c>.
///
/// Composed inside the grain process (mirrors <see cref="WorkflowReadModel"/>):
/// the grain is the consistency boundary for <see cref="Domain.Run.WorkflowRun"/>
/// and the lock acquire/release path must observe grain state with the same
/// strong-consistency guarantee as the rest of the command surface. The
/// coordinator only reads <c>CurrentStageId</c> off the run to decide its
/// release target — it does not mutate run state, write
/// <c>_lastKnownRunnerId</c>, or invoke <c>SaveRunAsync</c>. No new async
/// yield points are introduced: the only awaits on this path are the
/// pre-existing <see cref="IWorkflowStageLockGrain"/> round trip and the
/// pre-existing <see cref="WorkflowProfileManager.LoadStageSpecsAsync"/>
/// profile load.
/// </summary>
public sealed class WorkflowStageLockCoordinator
{
    private readonly WorkflowGrain _owner;

    public WorkflowStageLockCoordinator(WorkflowGrain owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Acquires the sequential stage lock for the given stage if the stage
    /// spec declares <c>LockBehavior == "sequential"</c>. Returns <c>true</c>
    /// when the lock is acquired or no lock is needed; <c>false</c> when the
    /// lock is currently held by another workflow run. Short-circuits to
    /// <c>true</c> when the stage has no sequential resource, and throws
    /// <see cref="InvalidOperationException"/> when the resource is non-null
    /// but the workflow's project id annotation is missing.
    /// </summary>
    public async Task<bool> AcquireStageLocksIfNeededAsync(string stage)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return true;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException($"Workflow '{_owner.GrainKey}' stage '{stage}' requires resource '{resource}' but project id is missing");

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = _owner.GrainFactoryAccess.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.AcquireSequentialAsync(new StageLockRequest(_owner.GrainKey, stage, resource, projectId));

        return result.Acquired;
    }

    /// <summary>
    /// Releases the sequential stage lock owned by this workflow run for the
    /// run's current stage. Used by the grain's retry/rerun/stop paths.
    /// Resolves the stage id from the in-memory run (read-only) and forwards
    /// to <see cref="ReleaseStageLocksAsync(string, string)"/>.
    /// </summary>
    public async Task ReleaseCurrentStageLocksAsync(string reason)
    {
        if (_owner.RunOrNull?.CurrentStageId is null) return;
        await ReleaseStageLocksAsync(_owner.RunOrNull.CurrentStageId, reason);
    }

    /// <summary>
    /// Releases the sequential stage lock owned by this workflow run for the
    /// given stage. Used by both the grain's retry/rerun/stop paths (via
    /// <see cref="ReleaseCurrentStageLocksAsync"/>) and by the bus-side
    /// <c>WorkflowStageLockReleaseHandler</c> that subscribes to
    /// <c>com.mohist.workflow.stage.{completed,failed}</c> events.
    ///
    /// The grain's <c>On()</c> dispatch used to call this synchronously after
    /// emitting a <see cref="StageCompleted"/>/<see cref="StageFailed"/>
    /// event; the lock release now flows through the event bus so the
    /// handler runs as part of the same in-process dispatch that
    /// <c>WorkflowRunStopped</c> already rides. Pull-scheduling (T-005
    /// cleanup D8) means a successful release no longer requires the
    /// previously-no-op <c>RequeueWorkflowIdAsync</c>: the next runner poll
    /// rediscovers the assignable workflow run from persisted state.
    /// </summary>
    public async Task ReleaseStageLocksAsync(string stage, string reason)
    {
        var resource = await GetSequentialLockResourceAsync(stage);
        if (resource is null) return;

        var projectId = _owner.GetProjectId();
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var key = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = _owner.GrainFactoryAccess.GetGrain<IWorkflowStageLockGrain>(key);
        var result = await lockGrain.ReleaseAsync(new StageLockOwner(_owner.GrainKey, stage));

        // The release grain surfaces the next waiter's run id, but pull
        // scheduling rediscovers assignable runs from persisted workflow
        // state — no per-project backlog mutation is required here. The
        // previous RequeueWorkflowIdAsync was a no-op and is deleted.
        _ = result.NextWorkflowRunId;
    }

    /// <summary>
    /// Resolves the first non-blank <c>Resources</c> entry for the given
    /// stage, but only when the stage spec declares <c>LockBehavior ==
    /// "sequential"</c>. Returns <c>null</c> for any stage that does not
    /// require a sequential lock, which lets acquire/release short-circuit.
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