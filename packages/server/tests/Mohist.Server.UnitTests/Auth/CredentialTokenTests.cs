using Mohist.Server.Auth.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class CredentialTokenTests
{
    [Theory]
    [InlineData(CredentialKind.Session, "session")]
    [InlineData(CredentialKind.Refresh, "refresh")]
    [InlineData(CredentialKind.Pat, "pat")]
    [InlineData(CredentialKind.Runner, "runner")]
    [InlineData(CredentialKind.Integration, "integration")]
    public void GeneratedToken_HasKindPrefixAndParsesBack(CredentialKind kind, string prefix)
    {
        var token = CredentialToken.Generate(kind);

        Assert.StartsWith($"moh_{prefix}_", token, StringComparison.Ordinal);
        Assert.True(CredentialToken.TryParse(token, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void GeneratedToken_SecretIsBase64UrlWithoutPadding()
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        var secret = token["moh_pat_".Length..];

        Assert.All(secret, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.DoesNotContain('=', secret);
        Assert.True(secret.Length >= 40);
    }

    [Fact]
    public void TwoGeneratedTokens_Differ()
    {
        var first = CredentialToken.Generate(CredentialKind.Pat);
        var second = CredentialToken.Generate(CredentialKind.Pat);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("moh")]
    [InlineData("moh_pat")]
    [InlineData("moh_pat_")]
    [InlineData("moh_unknown_x")]
    [InlineData("pat_abc_xyz")]
    public void MalformedTokens_AreRejected(string token)
    {
        Assert.False(CredentialToken.TryParse(token, out _));
    }

    [Fact]
    public void UnderscoresInsideSecret_AreAccepted()
    {
        Assert.True(CredentialToken.TryParse("moh_pat_secret_with_underscores", out var kind));
        Assert.Equal(CredentialKind.Pat, kind);
    }

    [Fact]
    public void Hash_IsDeterministicLowercaseHex()
    {
        var first = CredentialToken.Hash("moh_pat_secret");
        var second = CredentialToken.Hash("moh_pat_secret");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, character =>
            Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
    }
}
