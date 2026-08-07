using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.TestSupport;
using Xunit;
using static Mohist.Server.SpecTests.Specs.SystemSpecs.SystemUpdateTestFactory;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class SystemUpdateConsistencySpecs
{
    [Fact]
    public async Task GetConsistencyAsync_AllCoherentReturnsConsistent()
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog());

        var response = await service.GetConsistencyAsync();

        Assert.Equal("consistent", response.Status);
        Assert.All(response.Components, component => Assert.Equal("consistent", component.Status));
    }

    [Fact]
    public async Task GetConsistencyAsync_RunnerUnavailableIsReported()
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(
                runningGitHash: "newhash",
                sourceHead: "newhash",
                serverServiceStatus: "active",
                runnerServiceStatus: "inactive")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog());

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var runner = Assert.Single(response.Components, c => c.Name == "runner");
        Assert.Equal("unavailable", runner.Status);
    }

    [Theory]
    [InlineData(ManagedAssetCatalogState.Empty)]
    [InlineData(ManagedAssetCatalogState.Unavailable)]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenCatalogIsNotAvailable(
        ManagedAssetCatalogState state)
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog(state));

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
        Assert.Equal("mismatched", managed.Status);
    }

}
