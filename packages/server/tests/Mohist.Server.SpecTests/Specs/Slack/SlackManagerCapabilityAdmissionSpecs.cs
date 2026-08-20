using System.Net;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerCapabilityAdmissionSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerCapabilityAdmissionSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("/api/projects/project-that-does-not-exist/slack-manager/connections/connection/remove-binding")]
    [InlineData("/api/projects/project-that-does-not-exist/slack-manager/connections/connection/permanent-delete")]
    [InlineData("/api/projects/project-that-does-not-exist/slack-manager/install-agent/credentials")]
    [InlineData("/api/projects/project-that-does-not-exist/slack-manager/setup/runtime-credentials")]
    [InlineData("/api/v1/projects/project-that-does-not-exist/agents")]
    public async Task Manager_marked_unlisted_requests_are_rejected_before_project_lookup(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(ManagerCapabilityCatalog.ManagerModeHeader, "1");
        request.Content = new StringContent("{}");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "manager_capability_not_available",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Manager_marked_allowlisted_missing_target_reaches_existing_not_found_path()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/projects/project-that-does-not-exist/slack-connections/connection/diagnostic");
        request.Headers.TryAddWithoutValidation(ManagerCapabilityCatalog.ManagerModeHeader, "1");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unmarked_operator_request_keeps_existing_route_behavior()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-that-does-not-exist/slack-manager/connections/connection/remove-binding")
        {
            Content = new StringContent("{}"),
        };

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
