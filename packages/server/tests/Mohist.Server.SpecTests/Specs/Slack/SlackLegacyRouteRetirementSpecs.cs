using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackLegacyRouteRetirementSpecs
{
    private static readonly string[] RetiredRoutes =
    [
        "/api/slack-manager/credentials",
        "/api/projects/{projectRef}/slack-manager/connections/{connectionId}/begin-authorization",
        "/api/projects/{projectRef}/slack-manager/connections/{connectionId}/authorization-progress",
        "/api/projects/{projectRef}/slack-manager/connections/{connectionId}/authorize",
        "/api/projects/{projectRef}/slack-connections/{connectionId}/rotate-credentials",
        "/api/projects/{projectRef}/slack-connections/{connectionId}/adapter-session",
    ];

    private static readonly string[] KeptControlPlaneRoutes =
    [
        "/api/slack-manager/setup",
        "/api/slack-manager/setup/configuration",
        "/api/slack-manager/setup/runtime-credentials",
        "/api/slack-manager/setup/progress",
        "/api/projects/{projectRef}/slack-manager/install-agent",
        "/api/projects/{projectRef}/slack-manager/install-agent/credentials",
        "/api/slack-manager/adapter",
        "/api/slack-manager/adapter/{enrollmentId}/deliveries/claim",
        "/api/slack-manager/adapter/{enrollmentId}/deliveries/claim-uncertain",
        "/api/slack-manager/adapter/{enrollmentId}/deliveries/ack",
        "/api/slack-connections/adapter",
        "/api/projects/{projectRef}/slack-connections/{connectionId}/deliveries/claim",
        "/api/projects/{projectRef}/slack-connections/{connectionId}/deliveries/claim-uncertain",
        "/api/projects/{projectRef}/slack-connections/{connectionId}/deliveries/ack",
    ];

    private readonly MohistIntegrationFixture _fixture;

    public SlackLegacyRouteRetirementSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public void Retired_slack_routes_are_not_mapped()
    {
        var patterns = _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.All(RetiredRoutes, route => Assert.DoesNotContain(patterns, pattern =>
            string.Equals(pattern, route, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void New_setup_install_progress_and_lease_routes_stay_mapped()
    {
        var patterns = _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.All(KeptControlPlaneRoutes, route => Assert.Contains(patterns, pattern =>
            string.Equals(pattern, route, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("POST", "/api/slack-manager/credentials")]
    [InlineData("POST", "/api/projects/proj-retired/slack-manager/connections/conn-retired/begin-authorization")]
    [InlineData("POST", "/api/projects/proj-retired/slack-manager/connections/conn-retired/authorization-progress")]
    [InlineData("POST", "/api/projects/proj-retired/slack-manager/connections/conn-retired/authorize")]
    [InlineData("POST", "/api/projects/proj-retired/slack-connections/conn-retired/rotate-credentials")]
    [InlineData("POST", "/api/projects/proj-retired/slack-connections/conn-retired/adapter-session")]
    public async Task Retired_routes_answer_404_not_found(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { }),
        };

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
