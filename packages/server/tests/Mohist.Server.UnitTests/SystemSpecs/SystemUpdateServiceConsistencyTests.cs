using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.UnitTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.UnitTests.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateServiceConsistencyTests
{
    [Fact]
    public async Task GetConsistencyAsync_AllCoherentReturnsConsistent()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateConsistencyService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.GetConsistencyAsync();

        Assert.Equal("consistent", response.Status);
        Assert.All(response.Components, component => Assert.Equal("consistent", component.Status));
    }

    [Fact]
    public async Task GetConsistencyAsync_RunnerUnavailableIsReported()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateConsistencyService(
            new SequencedSystemInfo(CreateInfo(
                runningGitHash: "newhash",
                sourceHead: "newhash",
                serverServiceStatus: "active",
                runnerServiceStatus: "inactive")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var runner = Assert.Single(response.Components, c => c.Name == "runner");
        Assert.Equal("unavailable", runner.Status);
    }

    [Fact]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenNoSkillIsAvailable()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateConsistencyService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            hasManagedAssets: false);

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
        Assert.Equal("mismatched", managed.Status);
    }
}
