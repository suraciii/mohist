using Mohist.Server.SystemInfo;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemoryManagedAssetCatalog(
    ManagedAssetCatalogState state = ManagedAssetCatalogState.Available)
    : IManagedAssetCatalog
{
    public ManagedAssetCatalogState GetState() => state;
}
