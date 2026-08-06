using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Slack.Services;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAdapterOperatorAuthenticatorTests
{
    private const string OperatorToken = "test-operator-token-0123456789abcdef";

    [Fact]
    public async Task Valid_token_and_operator_id_return_the_trimmed_identity()
    {
        var auth = NewAuthenticator();
        var headers = new HeaderDictionary
        {
            [OperatorCredential.HeaderName] = OperatorToken,
            [SlackAdapterOperatorAuthenticator.OperatorIdHeaderName] = "  operator-1  ",
        };

        Assert.Equal("operator-1", await auth.AuthenticateAsync(headers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task Missing_or_wrong_operator_token_is_rejected(string token)
    {
        var auth = NewAuthenticator();
        var headers = new HeaderDictionary
        {
            [OperatorCredential.HeaderName] = token,
            [SlackAdapterOperatorAuthenticator.OperatorIdHeaderName] = "operator-1",
        };

        Assert.Null(await auth.AuthenticateAsync(headers));
    }

    [Fact]
    public async Task Missing_or_blank_operator_id_is_rejected()
    {
        var auth = NewAuthenticator();
        var withoutId = new HeaderDictionary { [OperatorCredential.HeaderName] = OperatorToken };
        var blankId = new HeaderDictionary
        {
            [OperatorCredential.HeaderName] = OperatorToken,
            [SlackAdapterOperatorAuthenticator.OperatorIdHeaderName] = "   ",
        };

        Assert.Null(await auth.AuthenticateAsync(withoutId));
        Assert.Null(await auth.AuthenticateAsync(blankId));
    }

    [Fact]
    public async Task Repeated_operator_id_header_values_are_rejected()
    {
        var auth = NewAuthenticator();
        var headers = new HeaderDictionary
        {
            [OperatorCredential.HeaderName] = OperatorToken,
        };
        headers.Append(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, "operator-1");
        headers.Append(SlackAdapterOperatorAuthenticator.OperatorIdHeaderName, "operator-2");

        Assert.Null(await auth.AuthenticateAsync(headers));
    }

    private static SlackAdapterOperatorAuthenticator NewAuthenticator()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:OperatorToken"] = OperatorToken,
            })
            .Build();
        var credential = new OperatorCredential(configuration, new MockEnvironmentVariableProvider());
        return new SlackAdapterOperatorAuthenticator(credential);
    }
}
