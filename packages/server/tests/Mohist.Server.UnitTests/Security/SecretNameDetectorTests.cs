using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Security.Secrets;
using Xunit;

namespace Mohist.Server.UnitTests.Security;

public sealed class SecretNameDetectorTests
{
    [Theory]
    [InlineData("appToken")]
    [InlineData("botToken")]
    [InlineData("token")]
    [InlineData("AccessToken")]
    [InlineData("client_secret")]
    [InlineData("ClientSecret")]
    [InlineData("webhookKey")]
    [InlineData("API_KEY")]
    [InlineData("publicKey")]
    [InlineData("PrivateKey")]
    [InlineData("encryption_key_id")]
    public void IsSecretKey_ReturnsTrueForSensitiveNames(string candidate)
    {
        Assert.True(SecretNameDetector.IsSecretKey(candidate));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("ProjectId")]
    [InlineData("endpointUrl")]
    [InlineData("scopes")]
    [InlineData("")]
    public void IsSecretKey_ReturnsFalseForNonSecretNames(string candidate)
    {
        Assert.False(SecretNameDetector.IsSecretKey(candidate));
    }
}
