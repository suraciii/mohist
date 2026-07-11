using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class RuntimeBuildInfoTests
{
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
    public void StartedAt_UsesInjectedClock()
    {
        var now = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
        var environment = new MockEnvironmentVariableProvider
        {
            [RuntimeBuildInfo.GitHashEnvironmentVariable] = "envhash123",
        };
        var info = new RuntimeBuildInfo(environment, new FakeTimeProvider(now));

        Assert.Equal(now, info.StartedAt);
    }

    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasDetachedHead_ReturnsHash()
    {
        var fileSystem = new FakeRuntimeFileSystem();
        fileSystem.AddFile("/repo/.git/HEAD", "abc123def456");

        var hash = RuntimeBuildInfo.TryReadGitHeadFile(fileSystem, "/repo");

        Assert.Equal("abc123def456", hash);
    }

    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasSymbolicRef_ReturnsRefHash()
    {
        var fileSystem = new FakeRuntimeFileSystem();
        fileSystem.AddFile("/repo/.git/HEAD", "ref: refs/heads/main");
        fileSystem.AddFile("/repo/.git/refs/heads/main", "def789abc012");

        var hash = RuntimeBuildInfo.TryReadGitHeadFile(fileSystem, "/repo");

        Assert.Equal("def789abc012", hash);
    }

    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasMissingRef_ReturnsNull()
    {
        var fileSystem = new FakeRuntimeFileSystem();
        fileSystem.AddFile("/repo/.git/HEAD", "ref: refs/heads/nonexistent");

        var hash = RuntimeBuildInfo.TryReadGitHeadFile(fileSystem, "/repo");

        Assert.Null(hash);
    }

    private sealed class FakeRuntimeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public void AddFile(string path, string content) => _files[path] = content;

        public bool Exists(string path) => _files.ContainsKey(path);

        public string ReadAllText(string path) => _files[path];
    }
}
