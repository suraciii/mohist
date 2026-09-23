using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L1")]
public sealed class SlackManagerIngressSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerIngressSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Legacy_manual_setup_route_is_removed()
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
