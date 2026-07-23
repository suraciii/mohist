using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

public sealed class RunnerRegistryCatalogSource : IActionCatalogSource, IScopedService
{
    private readonly IGrainFactory _grainFactory;

    public RunnerRegistryCatalogSource(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<ActionCatalog?> GetCatalogAsync()
    {
        var registry = _grainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnersAsync();

        RunnerInfo? selected = null;
        foreach (var runner in runners)
        {
            if (runner.ActionCatalog is null)
                continue;

            if (selected is null || IsLater(runner, selected))
                selected = runner;
        }

        return selected?.ActionCatalog;
    }

    private static bool IsLater(RunnerInfo candidate, RunnerInfo current)
    {
        if (candidate.RegisteredAt is null)
            return false;
        if (current.RegisteredAt is null)
            return true;
        var timestampComparison = candidate.RegisteredAt.Value.CompareTo(current.RegisteredAt.Value);
        return timestampComparison > 0
            || (timestampComparison == 0
                && string.Compare(candidate.RunnerId, current.RunnerId, StringComparison.Ordinal) > 0);
    }
}
