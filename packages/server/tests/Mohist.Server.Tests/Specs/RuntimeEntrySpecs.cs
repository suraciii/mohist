using System.Net;
using Mohist.Server.Api;
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
        var projectName = $"runtime-status-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });

        try
        {
            await _fixture.Client.PostOkAsync("/api/runner/runtime-test-runner/register", new { capabilities = Array.Empty<string>(), hostname = "test-host", projectId = project.Id });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={project.Id}");

            Assert.False(status.Running);
            Assert.True(status.RunnerAvailable);
            Assert.False(status.EmbeddedRunnerEnabled);
            Assert.Null(status.RunnerMessage);
            Assert.Equal(0, status.Capacity.Active);
            Assert.True(status.Capacity.Max > 0);
            Assert.Contains(status.Runners, r => r.Id == "runtime-test-runner");
        }
        finally
        {
            await _fixture.Client.PostAsync("/api/runner/runtime-test-runner/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_WhenNoRunnerConnected_ReportsUnavailableRuntime()
    {
        var status = AgentStatusResponse.Create([], [], 3);

        Assert.False(status.Running);
        Assert.False(status.RunnerAvailable);
        Assert.False(status.EmbeddedRunnerEnabled);
        Assert.Equal("No runner is connected. Start the Mohist runner process.", status.RunnerMessage);
    }

    [Fact]
    public async Task ApiFallback_WhenUnknownApiPath_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/api/missing-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record AgentStatusDto(bool Running, bool RunnerAvailable, bool EmbeddedRunnerEnabled, string? RunnerMessage, RunnerDto[] Runners, AgentCapacityDto Capacity);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string? Kind = null);
    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
}
