using Mohist.Server.Storage;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowSessionGrain : Grain, IWorkflowSessionGrain
{
    private readonly IStateStore<WorkflowSessionGrainState> _store;
    private WorkflowSessionEntry? _entry;

    public WorkflowSessionGrain(IStateStore<WorkflowSessionGrainState> store)
    {
        _store = store;
    }

    private string Key => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _store.LoadAsync(Key);
        _entry = state?.Entry;
    }

    public async Task RegisterAsync(string acpSessionId, string workDir)
    {
        _entry = new WorkflowSessionEntry(acpSessionId, workDir, DateTime.UtcNow.ToString("o"));
        await _store.SaveAsync(Key, new WorkflowSessionGrainState(_entry));
    }

    public Task<WorkflowSessionEntry?> GetAsync()
    {
        return Task.FromResult(_entry);
    }
}
