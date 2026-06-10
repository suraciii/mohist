using Mohist.Server.Infrastructure.Data.Issue;
using Orleans.Runtime;

namespace Mohist.Server.Issue.Grains;

public class IssueCounterGrain : Grain, IIssueCounterGrain
{
    private readonly IPersistentState<IssueCounterState> _state;

    public IssueCounterGrain(
        [PersistentState("counter")] IPersistentState<IssueCounterState> state)
    {
        _state = state;
    }

    public Task<int> NextAsync() => NextAsyncImpl();

    private async Task<int> NextAsyncImpl()
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        var current = _state.State is null || _state.State.Next <= 0 ? 1 : _state.State.Next;
        _state.State = new IssueCounterState(current + 1);
        await _state.WriteStateAsync();
        return current;
    }
}
