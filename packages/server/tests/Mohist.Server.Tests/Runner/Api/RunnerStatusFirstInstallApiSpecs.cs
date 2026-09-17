using System.Net;
using System.Net.Http.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

/// <summary>
/// First-install inventory coverage. This class owns an isolated integration
/// fixture so the durable Runner definition store is genuinely empty instead
/// of sharing the mutation collection's already-registered Runners.
/// </summary>
[Trait("level", "L1")]
public sealed class RunnerStatusFirstInstallApiSpecs : IClassFixture<MohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerStatusFirstInstallApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetRunners_NoDurableDefinitions_ReturnsFirstInstallInventory()
    {
        using var response = await _fixture.Client.GetAsync("/api/runners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        var data = payload.GetProperty("data");
        var inventory = data.GetProperty("inventory");
        Assert.Equal("first-install", inventory.GetProperty("state").GetString());
        Assert.Equal(0, data.GetProperty("runners").GetArrayLength());
        Assert.Empty(data.GetProperty("runners").EnumerateArray());

        var action = Assert.Single(inventory.GetProperty("nextActions").EnumerateArray());
        Assert.Equal("install-runner", action.GetProperty("code").GetString());
        Assert.Equal("mo install runner --repo-root <path>", action.GetProperty("command").GetString());
    }
}
