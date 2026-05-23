using Microsoft.Extensions.Logging;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowBacklogGrain : Grain, IWorkflowBacklogGrain
{
    private readonly Queue<string> _waiting = new();
    private readonly Dictionary<string, string> _running = new();
    private readonly HashSet<string> _all = new();
    private readonly ILogger<WorkflowBacklogGrain> _log;

    public WorkflowBacklogGrain(ILogger<WorkflowBacklogGrain> log)
    {
        _log = log;
    }

    public Task RegisterAsync(string workflowId)
    {
        if (_all.Contains(workflowId)) return Task.CompletedTask;

        _all.Add(workflowId);
        _waiting.Enqueue(workflowId);
        _log.LogInformation("Workflow {WfId} registered to backlog, waiting={Waiting}", workflowId, _waiting.Count);
        return Task.CompletedTask;
    }

    public Task<string?> ClaimAsync(string runnerId)
    {
        while (_waiting.Count > 0)
        {
            var wfId = _waiting.Dequeue();
            if (!_all.Contains(wfId)) continue;

            _running[wfId] = runnerId;
            _log.LogInformation("Runner {RunnerId} claimed workflow {WfId}", runnerId, wfId);
            return Task.FromResult<string?>(wfId);
        }

        return Task.FromResult<string?>(null);
    }

    public Task ReleaseAsync(string workflowId)
    {
        _all.Remove(workflowId);
        _running.Remove(workflowId);
        _log.LogInformation("Workflow {WfId} released from backlog", workflowId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListWaitingAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_waiting.ToList().AsReadOnly());
    }

    public Task<IReadOnlyList<(string WorkflowId, string RunnerId)>> ListRunningAsync()
    {
        return Task.FromResult<IReadOnlyList<(string, string)>>(_running.Select(kv => (kv.Key, kv.Value)).ToList().AsReadOnly());
    }

    public Task ClearAsync()
    {
        _waiting.Clear();
        _running.Clear();
        _all.Clear();
        return Task.CompletedTask;
    }
}
