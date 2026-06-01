using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Persistence;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowBacklogGrain : Grain, IWorkflowBacklogGrain
{
    private readonly Queue<string> _waiting = new();
    private readonly Dictionary<string, string> _running = new();
    private readonly HashSet<string> _all = new();
    private readonly IStateStore<WorkflowBacklogState> _store;
    private readonly IWorkflowBacklogDirectory _directory;
    private readonly ILogger<WorkflowBacklogGrain> _log;

    public WorkflowBacklogGrain(IStateStore<WorkflowBacklogState> store, IWorkflowBacklogDirectory directory, ILogger<WorkflowBacklogGrain> log)
    {
        _store = store;
        _directory = directory;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _directory.RegisterProject(GrainKey);
        var state = await _store.LoadAsync(GrainKey);
        if (state is null) return;
        foreach (var wfId in state.Waiting)
            _waiting.Enqueue(wfId);
        foreach (var (wfId, runnerId) in state.Running)
            _running[wfId] = runnerId;
        foreach (var wfId in state.All)
            _all.Add(wfId);
    }

    public async Task RegisterAsync(string workflowId)
    {
        var alreadyWaiting = RemoveFromWaiting(workflowId);

        _all.Add(workflowId);
        if (!_running.ContainsKey(workflowId))
            _waiting.Enqueue(workflowId);
        await SaveAsync();

        if (_running.ContainsKey(workflowId))
        {
            _log.LogInformation("Workflow {WfId} is already running in backlog; registration left it in place", workflowId);
            return;
        }

        if (alreadyWaiting)
        {
            _log.LogInformation("Workflow {WfId} re-queued in backlog, waiting={Waiting}", workflowId, _waiting.Count);
            return;
        }

        _log.LogInformation("Workflow {WfId} registered to backlog, waiting={Waiting}", workflowId, _waiting.Count);
    }

    public async Task RequeueAsync(string workflowId)
    {
        var removedRunning = _running.Remove(workflowId);
        var alreadyWaiting = RemoveFromWaiting(workflowId);

        _all.Add(workflowId);
        _waiting.Enqueue(workflowId);
        await SaveAsync();

        if (removedRunning || alreadyWaiting)
        {
            _log.LogInformation("Workflow {WfId} re-queued in backlog, waiting={Waiting}", workflowId, _waiting.Count);
            return;
        }

        _log.LogInformation("Workflow {WfId} queued in backlog, waiting={Waiting}", workflowId, _waiting.Count);
    }

    public async Task RestoreRunningAsync(string workflowId, string runnerId)
    {
        RemoveFromWaiting(workflowId);
        _all.Add(workflowId);
        _running[workflowId] = runnerId;
        await SaveAsync();
        _log.LogInformation("Workflow {WfId} restored as running for runner {RunnerId}", workflowId, runnerId);
    }

    public async Task<string?> ClaimAsync(string runnerId)
    {
        var changed = false;
        var staleRunningIds = _running
            .Where(kv => string.Equals(kv.Value, runnerId, StringComparison.Ordinal) && !_all.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        if (staleRunningIds.Count > 0)
        {
            foreach (var workflowId in staleRunningIds)
                _running.Remove(workflowId);
            changed = true;
        }

        while (_waiting.Count > 0)
        {
            var wfId = _waiting.Dequeue();
            changed = true;
            if (!_all.Contains(wfId)) continue;
            if (_running.ContainsKey(wfId)) continue;

            _running[wfId] = runnerId;
            await SaveAsync();
            _log.LogInformation("Runner {RunnerId} claimed workflow {WfId}", runnerId, wfId);
            return wfId;
        }

        if (changed)
            await SaveAsync();

        return null;
    }

    public async Task ReleaseAsync(string workflowId)
    {
        _all.Remove(workflowId);
        RemoveFromWaiting(workflowId);
        _running.Remove(workflowId);
        if (_waiting.Count == 0 && _running.Count == 0 && _all.Count == 0)
        {
            await _store.DeleteAsync(GrainKey);
        }
        else
        {
            await SaveAsync();
        }
        _log.LogInformation("Workflow {WfId} released from backlog", workflowId);
    }

    private bool RemoveFromWaiting(string workflowId)
    {
        if (_waiting.Count == 0) return false;

        var removed = false;
        var remaining = new Queue<string>();

        while (_waiting.Count > 0)
        {
            var candidate = _waiting.Dequeue();
            if (candidate == workflowId)
            {
                removed = true;
                continue;
            }

            remaining.Enqueue(candidate);
        }

        while (remaining.Count > 0)
            _waiting.Enqueue(remaining.Dequeue());

        return removed;
    }

    private Task SaveAsync() => _store.SaveAsync(GrainKey, new WorkflowBacklogState(_waiting.ToList(), new Dictionary<string, string>(_running), new HashSet<string>(_all)));
}
