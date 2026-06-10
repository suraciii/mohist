using Mohist.Server.Infrastructure.Data.Epic;
using Orleans.Runtime;

namespace Mohist.Server.Epic.Grains;

public class EpicCounterGrain : Grain, IEpicCounterGrain
{
    private readonly IPersistentState<EpicCounterState> _state;

    public EpicCounterGrain(
        [PersistentState("epic-counter")] IPersistentState<EpicCounterState> state)
    {
        _state = state;
    }

    public Task<int> NextAsync() => NextAsyncImpl();

    private async Task<int> NextAsyncImpl()
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        var current = _state.State is null || _state.State.Next <= 0 ? 1 : _state.State.Next;
        _state.State = new EpicCounterState(current + 1);
        await _state.WriteStateAsync();
        return current;
    }
}
