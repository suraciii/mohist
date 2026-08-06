using System.Security.Cryptography;
using System.Text;
using Mohist.Server.GitHub;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubWebhookSignatureTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("{\"action\":\"labeled\"}");

    private static string Sign(string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Payload)).ToLowerInvariant();

    [Fact]
    public void Verify_MatchesHermesStyleSignature()
    {
        Assert.True(GitHubWebhookSignature.Verify(Payload, "shared-secret", Sign("shared-secret")));
    }

    [Fact]
    public void Verify_RejectsWrongSecret()
    {
        Assert.False(GitHubWebhookSignature.Verify(Payload, "shared-secret", Sign("other-secret")));
    }

    [Fact]
    public void Verify_RejectsMissingHeader()
    {
        Assert.False(GitHubWebhookSignature.Verify(Payload, "shared-secret", null));
        Assert.False(GitHubWebhookSignature.Verify(Payload, "shared-secret", string.Empty));
    }

    [Theory]
    [InlineData("md5=deadbeef")]
    [InlineData("sha256=")]
    [InlineData("sha256=xyz")]
    [InlineData("sha256=abcdef")] // wrong length
    public void Verify_RejectsMalformedHeader(string header)
    {
        Assert.False(GitHubWebhookSignature.Verify(Payload, "shared-secret", header));
    }

    [Fact]
    public void Verify_RejectsEmptySecret()
    {
        Assert.False(GitHubWebhookSignature.Verify(Payload, string.Empty, Sign("shared-secret")));
    }
}
