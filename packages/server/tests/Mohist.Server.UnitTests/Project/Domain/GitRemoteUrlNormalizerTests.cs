using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Project.Domain;

/// <summary>
/// issue-417 T-003: conformance vectors for the credential-free Git
/// remote URL normalizer. Bumping
/// <see cref="GitRemoteUrlNormalizer.NormalizationVersion"/> requires
/// extending the expected fingerprints here.
/// </summary>
public class GitRemoteUrlNormalizerTests
{
    [Theory]
    [InlineData("git@example.com:owner/repo.git", "ssh://example.com/owner/repo")]
    [InlineData("git@example.com:owner/repo", "ssh://example.com/owner/repo")]
    [InlineData("ssh://git@example.com/owner/repo.git", "ssh://example.com/owner/repo")]
    [InlineData("git+ssh://git@example.com/owner/repo.git", "git+ssh://example.com/owner/repo")]
    [InlineData("https://example.com/owner/repo.git", "https://example.com/owner/repo")]
    [InlineData("https://user:pw@example.com/owner/repo.git", "https://example.com/owner/repo")]
    [InlineData("https://example.com:8443/owner/repo.git", "https://example.com:8443/owner/repo")]
    [InlineData("HTTPS://Example.COM/Owner/Repo.git", "https://example.com/Owner/Repo")]
    [InlineData("https://example.com/owner/repo/", "https://example.com/owner/repo")]
    [InlineData("https://example.com/owner/repo?ref=main", "https://example.com/owner/repo")]
    [InlineData("https://example.com/owner/repo#fragment", "https://example.com/owner/repo")]
    [InlineData("https://example.com/owner/sub/repo.git", "https://example.com/owner/sub/repo")]
    public void TryNormalize_Canonicalizes(string input, string expected)
    {
        Assert.True(GitRemoteUrlNormalizer.TryNormalize(input, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void TryNormalize_RejectsUnparseable(string? input)
    {
        Assert.False(GitRemoteUrlNormalizer.TryNormalize(input, out var _));
    }

    [Fact]
    public void Fingerprint_StableAcrossHttpsVariants()
    {
        // Case-only differences in the path are intentionally NOT
        // considered equivalent: Git remotes are path-sensitive on
        // POSIX file systems and case-insensitive on Windows / macOS,
        // and the RepositoryPolicy alias-rejection check needs the
        // canonical identity to agree with whatever the user typed
        // when they declared the remote.
        var a = GitRemoteUrlNormalizer.Fingerprint("https://example.com/owner/repo.git");
        var b = GitRemoteUrlNormalizer.Fingerprint("https://example.com:443/owner/repo");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Fingerprint, b!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_ScpLikeAndSshEquivalent()
    {
        var scp = GitRemoteUrlNormalizer.Fingerprint("git@example.com:owner/repo.git");
        var ssh = GitRemoteUrlNormalizer.Fingerprint("ssh://git@example.com/owner/repo.git");

        Assert.NotNull(scp);
        Assert.NotNull(ssh);
        Assert.Equal(scp!.Fingerprint, ssh!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_IgnoresCredentials()
    {
        var a = GitRemoteUrlNormalizer.Fingerprint("https://example.com/owner/repo.git");
        var b = GitRemoteUrlNormalizer.Fingerprint("https://user:pw@example.com/owner/repo.git");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Fingerprint, b!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_DiffersAcrossHosts()
    {
        var a = GitRemoteUrlNormalizer.Fingerprint("https://example.com/owner/repo.git");
        var b = GitRemoteUrlNormalizer.Fingerprint("https://other.com/owner/repo.git");

        Assert.NotEqual(a!.Fingerprint, b!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_DiffersAcrossPaths()
    {
        var a = GitRemoteUrlNormalizer.Fingerprint("https://example.com/owner/repo.git");
        var b = GitRemoteUrlNormalizer.Fingerprint("https://example.com/other/repo.git");

        Assert.NotEqual(a!.Fingerprint, b!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_UnparseableReturnsNull()
    {
        Assert.Null(GitRemoteUrlNormalizer.Fingerprint(""));
        Assert.Null(GitRemoteUrlNormalizer.Fingerprint(null));
        Assert.Null(GitRemoteUrlNormalizer.Fingerprint("not a url"));
    }
}