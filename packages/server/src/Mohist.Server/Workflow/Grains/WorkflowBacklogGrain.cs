using Microsoft.Extensions.Logging;
using Mohist.Server.Storage;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowBacklogGrain : Grain, IWorkflowBacklogGrain
{
    private readonly Queue<string> _waiting = new();
    private readonly Dictionary<string, string> _running = new();
    private readonly HashSet<string> _all = new();
    private readonly IStateStore<WorkflowBacklogState> _store;
    private readonly ILogger<WorkflowBacklogGrain> _log;

    public WorkflowBacklogGrain(IStateStore<WorkflowBacklogState> store, ILogger<WorkflowBacklogGrain> log)
    {
        _store = store;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
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
        if (_all.Contains(workflowId))
        {
            if (_waiting.Contains(workflowId)) return;

            _running.Remove(workflowId);
            _waiting.Enqueue(workflowId);
            await SaveAsync();
            _log.LogInformation("Workflow {WfId} re-queued in backlog, waiting={Waiting}", workflowId, _waiting.Count);
            return;
        }

        _all.Add(workflowId);
        _waiting.Enqueue(workflowId);
        await SaveAsync();
        _log.LogInformation("Workflow {WfId} registered to backlog, waiting={Waiting}", workflowId, _waiting.Count);
    }

    public async Task<string?> ClaimAsync(string runnerId)
    {
        while (_waiting.Count > 0)
        {
            var wfId = _waiting.Dequeue();
            if (!_all.Contains(wfId)) continue;

            _running[wfId] = runnerId;
            await SaveAsync();
            _log.LogInformation("Runner {RunnerId} claimed workflow {WfId}", runnerId, wfId);
            return wfId;
        }

        return null;
    }

    public async Task ReleaseAsync(string workflowId)
    {
        _all.Remove(workflowId);
        _running.Remove(workflowId);
        await SaveAsync();
        _log.LogInformation("Workflow {WfId} released from backlog", workflowId);
    }

    public Task<IReadOnlyList<string>> ListWaitingAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_waiting.ToList().AsReadOnly());
    }

    public Task<IReadOnlyList<(string WorkflowId, string RunnerId)>> ListRunningAsync()
    {
        return Task.FromResult<IReadOnlyList<(string, string)>>(_running.Select(kv => (kv.Key, kv.Value)).ToList().AsReadOnly());
    }

    public async Task ClearAsync()
    {
        _waiting.Clear();
        _running.Clear();
        _all.Clear();
        await SaveAsync();
    }

    private Task SaveAsync() => _store.SaveAsync(GrainKey, new WorkflowBacklogState(_waiting.ToList(), new Dictionary<string, string>(_running), new HashSet<string>(_all)));
}
