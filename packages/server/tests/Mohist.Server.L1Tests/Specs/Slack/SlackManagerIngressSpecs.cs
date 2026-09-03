using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

[Trait("level", "L1")]
public sealed class SlackManagerIngressSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerIngressSpecs(DefaultMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Setup_identity_conflict_is_a_conflict_response()
    {
        var team = $"T_MANAGER_SETUP_CONFLICT_{Guid.NewGuid():N}";
        using var first = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_MANAGER_SETUP_CONFLICT",
            managerBotUserId = "U_MANAGER_SETUP_CONFLICT",
        });
        first.EnsureSuccessStatusCode();

        using var conflicting = await _fixture.Client.PostAsJsonAsync("/api/slack-manager/setup", new
        {
            workspaceTeamId = team,
            managerAppId = "A_MANAGER_SETUP_CONFLICT_OTHER",
            managerBotUserId = "U_MANAGER_SETUP_CONFLICT_OTHER",
        });

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        using var document = JsonDocument.Parse(await conflicting.Content.ReadAsStringAsync());
        Assert.Equal("manager_identity_conflict", document.RootElement.GetProperty("code").GetString());
    }
}
