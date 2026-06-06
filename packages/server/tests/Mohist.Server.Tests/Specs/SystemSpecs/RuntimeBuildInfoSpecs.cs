using System.Reflection;
using Mohist.Server.SystemInfo;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class RuntimeBuildInfoSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void MetadataIdentity_WhenAssemblyHasInformationalVersion_ReturnsVersionAndGitHash()
    {
        var info = new RuntimeBuildInfo();

        Assert.NotNull(info.Version);
        Assert.NotNull(info.GitHash);
        Assert.NotEmpty(info.Version);
        Assert.NotEmpty(info.GitHash);
        Assert.True(info.StartedAt <= DateTimeOffset.UtcNow);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void GitHash_WhenInitialized_RemainsStableForProcessLifetime()
    {
        var info1 = new RuntimeBuildInfo();
        var info2 = new RuntimeBuildInfo();

        Assert.Equal(info1.GitHash, info2.GitHash);
        Assert.Equal(info1.Version, info2.Version);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void StartedAt_IsCapturedAtInitialization()
    {
        var before = DateTimeOffset.UtcNow.AddMilliseconds(-100);
        var info = new RuntimeBuildInfo();
        var after = DateTimeOffset.UtcNow.AddMilliseconds(100);

        Assert.True(info.StartedAt >= before, "StartedAt should be after initialization began");
        Assert.True(info.StartedAt <= after, "StartedAt should be before initialization completed");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasDetachedHead_ReturnsHash()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"mohist-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        try
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, ".git", "HEAD"), "abc123def456");

            var hash = RuntimeBuildInfo.TryReadGitHeadFile(repoDir);

            Assert.Equal("abc123def456", hash);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasSymbolicRef_ReturnsRefHash()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"mohist-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        try
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git", "refs", "heads"));
            File.WriteAllText(Path.Combine(repoDir, ".git", "refs", "heads", "main"), "def789abc012");
            File.WriteAllText(Path.Combine(repoDir, ".git", "HEAD"), "ref: refs/heads/main");

            var hash = RuntimeBuildInfo.TryReadGitHeadFile(repoDir);

            Assert.Equal("def789abc012", hash);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void TryReadGitHeadFile_WhenRepoHasMissingRef_ReturnsNull()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"mohist-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        try
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, ".git", "HEAD"), "ref: refs/heads/nonexistent");

            var hash = RuntimeBuildInfo.TryReadGitHeadFile(repoDir);

            Assert.Null(hash);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
