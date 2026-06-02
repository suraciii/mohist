using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Persistence;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowBacklogGrain : Grain, IWorkflowBacklogGrain
{
    private readonly Queue<string> _waiting = new();
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
        foreach (var wfId in state.All)
            _all.Add(wfId);
    }

    public async Task EnqueueAsync(string workflowRunId)
    {
        var alreadyWaiting = RemoveFromWaiting(workflowRunId);

        _all.Add(workflowRunId);
        _waiting.Enqueue(workflowRunId);
        await SaveAsync();

        if (alreadyWaiting)
        {
            _log.LogInformation("Workflow {WfId} re-queued in backlog, waiting={Waiting}", workflowRunId, _waiting.Count);
            return;
        }

        _log.LogInformation("Workflow {WfId} registered to backlog, waiting={Waiting}", workflowRunId, _waiting.Count);
    }

    public async Task<string?> ClaimAsync(string runnerId)
    {
        var changed = false;
        var attempts = _waiting.Count;

        for (var i = 0; i < attempts && _waiting.Count > 0; i++)
        {
            var wfId = _waiting.Dequeue();
            changed = true;
            if (!_all.Contains(wfId)) continue;

            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(wfId);
            var assignment = await workflow.AssignRunnerAsync(runnerId);
            switch (assignment.Status)
            {
                case WorkflowAssignmentStatus.Assigned:
                    _all.Remove(wfId);
                    await SaveAsync();
                    _log.LogInformation("Runner {RunnerId} assigned workflow {WfId} from backlog", runnerId, wfId);
                    return wfId;

                case WorkflowAssignmentStatus.Rejected:
                default:
                    _all.Remove(wfId);
                    break;
            }
        }

        if (changed)
            await SaveAsync();

        return null;
    }

    private bool RemoveFromWaiting(string workflowRunId)
    {
        if (_waiting.Count == 0) return false;

        var removed = false;
        var remaining = new Queue<string>();

        while (_waiting.Count > 0)
        {
            var candidate = _waiting.Dequeue();
            if (candidate == workflowRunId)
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

    private Task SaveAsync() => _store.SaveAsync(GrainKey, new WorkflowBacklogState(_waiting.ToList(), new HashSet<string>(_all)));
}
