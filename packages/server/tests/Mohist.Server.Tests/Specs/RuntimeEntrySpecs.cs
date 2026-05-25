using System.Net;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class RuntimeEntrySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RuntimeEntrySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WebRoot_WhenConfigured_ServesIndexAndSpaFallback()
    {
        var root = await _fixture.Client.GetStringAsync("/");
        var route = await _fixture.Client.GetStringAsync("/issues/1");

        Assert.Contains("Mohist Test Web", root);
        Assert.Contains("Mohist Test Web", route);
    }

    [Fact]
    public async Task AgentStatus_WhenRunnerRegisteredWithoutActiveWork_ReportsIdleRuntime()
    {
        await _fixture.Client.PostOkAsync("/api/runner/runtime-test-runner/register", new { capabilities = Array.Empty<string>(), hostname = "test-host" });

        var status = await _fixture.Client.GetDataAsync<AgentStatusDto>("/api/agent/status");

        Assert.False(status.Running);
        Assert.Equal(0, status.Capacity.Active);
        Assert.True(status.Capacity.Max > 0);
        Assert.Contains(status.Runners, r => r.Id == "runtime-test-runner");
    }

    [Fact]
    public async Task ApiFallback_WhenUnknownApiPath_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/api/missing-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record AgentStatusDto(bool Running, RunnerDto[] Runners, AgentCapacityDto Capacity);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id);
}
