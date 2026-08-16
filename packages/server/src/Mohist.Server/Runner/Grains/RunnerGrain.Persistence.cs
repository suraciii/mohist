namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    private async Task PersistAsync()
    {
        await _state.WriteStateAsync();
    }
}
