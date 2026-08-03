using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackWebLinkBuilderTests
{
    [Fact]
    public void BuildOpenSession_UsesTheConfiguredExternalOriginAndEscapedProjectScopedRoute()
    {
        var link = Build(new SlackProviderOptions { ExternalWebUrl = "https://mohist.example/app" })
            .BuildOpenSession("release notes", "session/one");

        Assert.NotNull(link);
        Assert.Equal("https://mohist.example/app/release%20notes/sessions/session%2Fone", link.Url);
        var button = link.Blocks[0].GetProperty("elements")[0];
        Assert.Equal("Open in Mohist", button.GetProperty("text").GetProperty("text").GetString());
        Assert.Equal(link.Url, button.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://localhost:5173")]
    [InlineData("https://api.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::]")]
    [InlineData("https://operator@mohist.example")]
    [InlineData("http://mohist.example")]
    public void BuildOpenSession_RejectsUnsafeOrUnconfiguredExternalUrls(string externalWebUrl)
    {
        var link = Build(new SlackProviderOptions { ExternalWebUrl = externalWebUrl })
            .BuildOpenSession("demo", "session-1");

        Assert.Null(link);
    }

    [Fact]
    public void BuildOpenSession_AllowsAnExplicitDevelopmentHttpOrigin()
    {
        var link = Build(new SlackProviderOptions
        {
            ExternalWebUrl = "http://dev.mohist.example/base",
            DevelopmentExternalWebUrlAllowlist = ["http://dev.mohist.example"],
        }).BuildOpenSession("demo", "session-1");

        Assert.NotNull(link);
        Assert.Equal("http://dev.mohist.example/base/demo/sessions/session-1", link.Url);
    }

    [Fact]
    public void BuildOpenSession_DoesNotLetTheDevelopmentAllowlistBypassLocalHostRejection()
    {
        var link = Build(new SlackProviderOptions
        {
            ExternalWebUrl = "http://localhost:5173",
            DevelopmentExternalWebUrlAllowlist = ["http://localhost:5173"],
        }).BuildOpenSession("demo", "session-1");

        Assert.Null(link);
    }

    private static SlackWebLinkBuilder Build(SlackProviderOptions options) =>
        new(Options.Create(options));
}
