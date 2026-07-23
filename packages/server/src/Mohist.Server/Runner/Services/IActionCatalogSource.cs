using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

public interface IActionCatalogSource
{
    Task<ActionCatalog?> GetCatalogAsync();
}
