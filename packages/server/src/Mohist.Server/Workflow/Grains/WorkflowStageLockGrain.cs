using Mohist.Server.Infrastructure.Persistence;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowStageLockGrain : Grain, IWorkflowStageLockGrain
{
    private readonly IStateStore<WorkflowStageLockState> _store;
    private readonly ILogger<WorkflowStageLockGrain> _log;
    private WorkflowStageLockState _state = new();

    public WorkflowStageLockGrain(IStateStore<WorkflowStageLockState> store, ILogger<WorkflowStageLockGrain> log)
    {
        _store = store;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _state = await _store.LoadAsync(GrainKey) ?? new WorkflowStageLockState();
    }

    public async Task<StageLockAcquireResult> AcquireSequentialAsync(StageLockRequest request)
    {
        if (_state.Owner is { } owner)
        {
            if (owner.WorkflowRunId == request.WorkflowRunId && owner.Stage == request.Stage)
            {
                return new StageLockAcquireResult(true, request.Resource, owner.WorkflowRunId, _state.Waiting.Count);
            }

            EnqueueIfMissing(request);
            await SaveAsync();
            return new StageLockAcquireResult(false, request.Resource, owner.WorkflowRunId, _state.Waiting.Count);
        }

        var next = DequeueNext();
        if (next is not null && (next.WorkflowRunId != request.WorkflowRunId || next.Stage != request.Stage))
        {
            _state.Owner = new StageLockOwner(next.WorkflowRunId, next.Stage);
            EnqueueIfMissing(request);
            await SaveAsync();
            _log.LogInformation("Stage lock {LockKey} granted queued workflow {WorkflowRunId} before caller {Caller}",
                GrainKey, next.WorkflowRunId, request.WorkflowRunId);
            return new StageLockAcquireResult(false, request.Resource, next.WorkflowRunId, _state.Waiting.Count);
        }

        _state.Owner = new StageLockOwner(request.WorkflowRunId, request.Stage);
        RemoveWaiting(request.WorkflowRunId, request.Stage);
        await SaveAsync();
        _log.LogInformation("Stage lock {LockKey} acquired by workflow {WorkflowRunId} stage {Stage}",
            GrainKey, request.WorkflowRunId, request.Stage);
        return new StageLockAcquireResult(true, request.Resource, request.WorkflowRunId, _state.Waiting.Count);
    }

    public async Task<StageLockReleaseResult> ReleaseAsync(StageLockOwner owner)
    {
        if (_state.Owner is null
            || _state.Owner.WorkflowRunId != owner.WorkflowRunId
            || _state.Owner.Stage != owner.Stage)
        {
            RemoveWaiting(owner.WorkflowRunId, owner.Stage);
            await SaveAsync();
            return new StageLockReleaseResult(false, ResourceFromKey(), _state.Owner?.WorkflowRunId, _state.Waiting.Count);
        }

        var next = _state.Waiting.FirstOrDefault();
        _state.Owner = null;
        await SaveAsync();
        _log.LogInformation("Stage lock {LockKey} released by workflow {WorkflowRunId}; next={Next}",
            GrainKey, owner.WorkflowRunId, next?.WorkflowRunId ?? "<none>");
        return new StageLockReleaseResult(true, ResourceFromKey(), next?.WorkflowRunId, _state.Waiting.Count);
    }

    public Task<WorkflowStageLockState?> GetStateAsync() => Task.FromResult<WorkflowStageLockState?>(_state);

    private void EnqueueIfMissing(StageLockRequest request)
    {
        if (_state.Waiting.Any(w => w.WorkflowRunId == request.WorkflowRunId && w.Stage == request.Stage))
            return;

        _state.Waiting.Add(request);
    }

    private StageLockRequest? DequeueNext()
    {
        if (_state.Waiting.Count == 0) return null;
        var next = _state.Waiting[0];
        _state.Waiting.RemoveAt(0);
        return next;
    }

    private void RemoveWaiting(string workflowRunId, string stage)
    {
        _state.Waiting.RemoveAll(w => w.WorkflowRunId == workflowRunId && w.Stage == stage);
    }

    private Task SaveAsync() => _store.SaveAsync(GrainKey, _state);

    private string ResourceFromKey()
    {
        var idx = GrainKey.LastIndexOf(':');
        return idx >= 0 && idx + 1 < GrainKey.Length ? GrainKey[(idx + 1)..] : GrainKey;
    }
}
