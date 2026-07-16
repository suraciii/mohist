using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class RuntimeBuildInfoTests
{
    [Fact]
    public void MetadataIdentity_WhenAssemblyHasInformationalVersion_ReturnsVersionAndGitHash()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var info = new RuntimeBuildInfo(
            new MockEnvironmentVariableProvider(),
            new StubRuntimeSourceIdentity(),
            time);

        Assert.NotNull(info.Version);
        Assert.NotNull(info.GitHash);
        Assert.NotEmpty(info.Version);
        Assert.NotEmpty(info.GitHash);
        Assert.Equal(TestTime.UtcNow, info.StartedAt);
    }

    [Fact]
    public void GitHash_WhenInitialized_RemainsStableForProcessLifetime()
    {
        var environment = new MockEnvironmentVariableProvider();
        var sourceIdentity = new StubRuntimeSourceIdentity("headhash456");
        var time = new FakeTimeProvider(TestTime.UtcNow);

        var info1 = new RuntimeBuildInfo(environment, sourceIdentity, time);
        var info2 = new RuntimeBuildInfo(environment, sourceIdentity, time);

        Assert.Equal(info1.GitHash, info2.GitHash);
        Assert.Equal(info1.Version, info2.Version);
    }

    [Fact]
    public void ResolveIdentity_WhenInformationalVersionHasNoHash_FallsBackToEnvironmentHash()
    {
        var identity = RuntimeBuildInfo.ResolveIdentity(
            "1.2.3",
            "1.2.3.0",
            () => "envhash123",
            () => "headhash456");

        Assert.Equal("1.2.3", identity.Version);
        Assert.Equal("envhash123", identity.GitHash);
    }

    [Fact]
    public void ResolveIdentity_WhenInformationalVersionHasNoHashAndEnvironmentIsEmpty_FallsBackToGitHead()
    {
        var identity = RuntimeBuildInfo.ResolveIdentity(
            "1.2.3",
            "1.2.3.0",
            () => null,
            () => "headhash456");

        Assert.Equal("1.2.3", identity.Version);
        Assert.Equal("headhash456", identity.GitHash);
    }

    [Fact]
    public void StartedAt_IsCapturedAtInitialization()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);

        var info = new RuntimeBuildInfo(
            new MockEnvironmentVariableProvider(),
            new StubRuntimeSourceIdentity(),
            time);

        Assert.Equal(TestTime.UtcNow, info.StartedAt);
    }

    private sealed class StubRuntimeSourceIdentity(string? gitHead = null) : IRuntimeSourceIdentity
    {
        public string? GitHead { get; } = gitHead;
    }
}
