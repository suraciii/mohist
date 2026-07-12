using EnvironmentAbstractions.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Mohist.Server.Infrastructure.Security;
using Xunit;

namespace Mohist.Server.UnitTests.Security;

public sealed class OperatorCredentialTests
{
    private const string Token = "test-operator-token-0123456789abcdef";

    [Fact]
    public void AuthorizesOnlyOneExactCredentialValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:OperatorToken"] = Token,
            })
            .Build();
        var credential = new OperatorCredential(
            configuration,
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false));
        var context = new DefaultHttpContext();

        Assert.False(credential.Authorizes(context.Request.Headers));

        context.Request.Headers[OperatorCredential.HeaderName] = "wrong-operator-token-0123456789abcdef";
        Assert.False(credential.Authorizes(context.Request.Headers));

        context.Request.Headers[OperatorCredential.HeaderName] = Token;
        Assert.True(credential.Authorizes(context.Request.Headers));

        context.Request.Headers[OperatorCredential.HeaderName] = new StringValues([Token, Token]);
        Assert.False(credential.Authorizes(context.Request.Headers));
    }

    [Fact]
    public void DefaultCredential_IsCreatedOnceInUserOnlyFileAndReused()
    {
        var home = Path.Combine(Path.GetTempPath(), $"mohist-operator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
            {
                ["HOME"] = home,
            };
            var configuration = new ConfigurationBuilder().Build();

            var first = new OperatorCredential(configuration, environment);
            var path = Path.Combine(home, ".mohist", "operator-token");
            var token = File.ReadAllText(path).Trim();
            Assert.True(token.Length >= 32);

            var second = new OperatorCredential(configuration, environment);
            var context = new DefaultHttpContext();
            context.Request.Headers[OperatorCredential.HeaderName] = token;
            Assert.True(first.Authorizes(context.Request.Headers));
            Assert.True(second.Authorizes(context.Request.Headers));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentCredentialOverridesConfigCredential()
    {
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            [OperatorCredential.TokenEnvironmentVariable] = "environment-operator-token-0123456789abcdef",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:OperatorToken"] = "configuration-operator-token-0123456789abcdef",
            })
            .Build();
        var credential = new OperatorCredential(configuration, environment);
        var context = new DefaultHttpContext();

        context.Request.Headers[OperatorCredential.HeaderName] =
            "configuration-operator-token-0123456789abcdef";
        Assert.False(credential.Authorizes(context.Request.Headers));

        context.Request.Headers[OperatorCredential.HeaderName] =
            "environment-operator-token-0123456789abcdef";
        Assert.True(credential.Authorizes(context.Request.Headers));
    }
}
