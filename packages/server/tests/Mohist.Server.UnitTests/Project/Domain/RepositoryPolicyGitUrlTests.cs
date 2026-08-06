using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Project.Domain;

public class RepositoryPolicyGitUrlTests
{
    [Theory]
    [InlineData("https://user:token@example.com/repo.git", false)]
    [InlineData("http://user@example.com/repo.git", false)]
    [InlineData("https://example.com/repo.git", true)]
    [InlineData("http://example.com/repo.git", true)]
    [InlineData("git@example.com:repo.git", true)]
    [InlineData("ssh://git@example.com/repo.git", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void TryNormalizeGitUrl_RejectsEmbeddedHttpCredentials(string raw, bool expected)
    {
        var result = RepositoryPolicy.TryNormalizeGitUrl(raw, out _);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryNormalizeGitUrl_Null_ReturnsFalse()
    {
        Assert.False(RepositoryPolicy.TryNormalizeGitUrl(null, out _));
    }

    [Fact]
    public void Validate_FlagsEmbeddedHttpCredentials()
    {
        var errors = RepositoryPolicy.Validate([
            new RepositoryPolicy.NormalizedRepository(
                "server",
                "https://user:token@example.com/repo.git",
                "main",
                IsDefault: true),
        ]);

        Assert.Contains(errors, e => e.Code == "repositories[0].gitUrl");
    }
}
