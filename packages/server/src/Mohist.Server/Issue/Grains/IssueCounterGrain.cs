namespace Mohist.Server.Issue.Grains;
using Mohist.Server.Infrastructure.Persistence;

public class IssueCounterGrain : Grain, IIssueCounterGrain
{
    private int _next;
    private readonly IStateStore<IssueCounterState> _store;

    public IssueCounterGrain(IStateStore<IssueCounterState> store)
    {
        _store = store;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _store.LoadAsync(GrainKey);
        _next = state?.Next ?? 1;
    }

    public async Task<int> NextAsync()
    {
        var value = _next++;
        await _store.SaveAsync(GrainKey, new IssueCounterState(_next));
        return value;
    }
}
