using Mohist.Server.Infrastructure.Data.Epic;
using Orleans.Runtime;

namespace Mohist.Server.Epic.Grains;

public class EpicCounterGrain : Grain, IEpicCounterGrain
{
    private readonly IPersistentState<EpicCounterState> _state;

    public EpicCounterGrain(
        [PersistentState("counter")] IPersistentState<EpicCounterState> state)
    {
        _state = state;
    }

    public Task<int> NextAsync() => NextAsyncImpl();

    private async Task<int> NextAsyncImpl()
    {
        var current = _state.State.Next;
        _state.State = new EpicCounterState(current + 1);
        await _state.WriteStateAsync();
        return current;
    }
}
