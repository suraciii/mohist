using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Slack;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackWebLinkBuilderTests
{
    [Fact]
    public void BuildOpenSession_UsesTheConfiguredExternalOriginAndEscapedProjectScopedRoute()
    {
        var link = Build(new SlackProviderOptions { ExternalWebUrl = "https://mohist.example/app" })
            .BuildOpenSession("release notes", "session/one");

        Assert.NotNull(link);
        Assert.Equal("https://mohist.example/app/release%20notes/sessions/session%2Fone", link.Url);
        AssertOrdinaryLink(link);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("https://localhost:5173")]
    [InlineData("https://api.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://169.254.1.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::]")]
    [InlineData("https://operator@mohist.example")]
    [InlineData("https://mohist.example?query=value")]
    [InlineData("https://mohist.example#fragment")]
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
        AssertOrdinaryLink(link);
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

    [Fact]
    public void BuildOpenSession_EscapesMrkdwnDelimitersInRouteSegments()
    {
        var link = Build(new SlackProviderOptions { ExternalWebUrl = "https://mohist.example" })
            .BuildOpenSession("project>|name", "session>|one");

        Assert.NotNull(link);
        Assert.Equal("https://mohist.example/project%3E%7Cname/sessions/session%3E%7Cone", link.Url);
        AssertOrdinaryLink(link);
    }

    private static void AssertOrdinaryLink(SlackWebLink link)
    {
        var section = Assert.Single(link.Blocks.EnumerateArray());
        Assert.Equal("section", section.GetProperty("type").GetString());
        var text = section.GetProperty("text");
        Assert.Equal("mrkdwn", text.GetProperty("type").GetString());
        Assert.Equal($"<{link.Url}|Open in Mohist>", text.GetProperty("text").GetString());
        Assert.Equal(["type", "text"], section.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["type", "text"], text.EnumerateObject().Select(property => property.Name));
    }

    private static SlackWebLinkBuilder Build(SlackProviderOptions options) =>
        new(Options.Create(options));
}
