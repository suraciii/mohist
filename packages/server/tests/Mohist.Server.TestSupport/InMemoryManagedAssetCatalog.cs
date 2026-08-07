using Mohist.Server.SystemInfo;

namespace Mohist.Server.TestSupport;

public sealed class InMemoryManagedAssetCatalog(
    ManagedAssetCatalogState state = ManagedAssetCatalogState.Available)
    : IManagedAssetCatalog
{
    public ManagedAssetCatalogState GetState() => state;
}
