using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Test double for <see cref="IActionCatalogSource"/> that always reports
/// "no catalog available" — used by spec tests that want the manager to
/// skip Action-contract validation and rely on Definition-only validation.
/// Specs that need to assert the catalog-backed path inject a stub that
/// returns a fixture catalog via the constructor.
/// </summary>
public sealed class NullActionCatalogSource : IActionCatalogSource
{
    public static readonly NullActionCatalogSource Instance = new();

    public Task<ActionCatalog?> GetCatalogAsync() => Task.FromResult<ActionCatalog?>(null);
}

/// <summary>
/// Test double for <see cref="IActionCatalogSource"/> that returns a fixed
/// <see cref="ActionCatalog"/> for every call. Use this to exercise the
/// catalog-backed save path in spec tests without touching the Runner
/// registry grain.
/// </summary>
public sealed class StubActionCatalogSource : IActionCatalogSource
{
    private readonly ActionCatalog? _catalog;

    public StubActionCatalogSource(ActionCatalog? catalog)
    {
        _catalog = catalog;
    }

    public Task<ActionCatalog?> GetCatalogAsync() => Task.FromResult(_catalog);
}
