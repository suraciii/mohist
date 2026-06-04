namespace Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Persistence;

public class EpicCounterGrain : Grain, IEpicCounterGrain
{
    private int _next;
    private readonly IStateStore<EpicCounterState> _store;

    public EpicCounterGrain(IStateStore<EpicCounterState> store)
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
        await _store.SaveAsync(GrainKey, new EpicCounterState(_next));
        return value;
    }
}
