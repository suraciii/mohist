using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Issue-452 design D5 spec tests: the generic agent-session open and
/// attach routes derive the session runtime from the session itself
/// rather than hardcoding <c>opencode</c>. The session's runtime was
/// pinned at launch time by the launcher, so it is authoritative on
/// every subsequent runner call.
/// </summary>
[Collection("MohistIntegration")]
public class GenericAgentSessionRuntimeOpenAttachSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public GenericAgentSessionRuntimeOpenAttachSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GenericOpen_PersistedPiRuntime_OpensSessionWithPi()
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"generic-open-pi-{Guid.NewGuid():N}");
        var agent = await CreateAgentAsync(project.Id, "open-pi-agent", runtime: "pi");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = "open on pi" });
        launch.EnsureSuccessStatusCode();
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

        // Re-open: the runner's open call must derive the runtime from
        // the session snapshot (which is pi), not a hardcoded literal.
        var runnerId = $"generic-open-pi-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });

        try
        {
            using var open = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
                new
                {
                    workId = $"work-{Guid.NewGuid():N}",
                    workType = "task",
                    stage = "Build",
                    title = "open on pi",
                });
            open.EnsureSuccessStatusCode();
            var openPayload = await open.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pi", openPayload.GetProperty("runtime").GetString());

            var info = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.NotNull(info);
            Assert.Equal("pi", info!.Runtime);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", content: null);
        }
    }

    [Fact]
    public async Task GenericAttach_PersistedPiRuntime_BindsSessionWithPi()
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"generic-attach-pi-{Guid.NewGuid():N}");
        var agent = await CreateAgentAsync(project.Id, "attach-pi-agent", runtime: "pi");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = "attach on pi" });
        launch.EnsureSuccessStatusCode();
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

        var runnerId = $"generic-attach-pi-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });

        try
        {
            using var open = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
                new { workId = $"work-{Guid.NewGuid():N}" });
            open.EnsureSuccessStatusCode();

            using var attach = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/attach",
                new
                {
                    runtimeSessionId = $"runtime-{Guid.NewGuid():N}",
                    workDir = "/tmp/generic-attach-pi",
                    processPid = 4321,
                });
            attach.EnsureSuccessStatusCode();
            var attachPayload = await attach.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pi", attachPayload.GetProperty("runtime").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", content: null);
        }
    }

    [Fact]
    public async Task GenericAttach_AgentConnectionSource_BindsSession()
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"generic-attach-connection-{Guid.NewGuid():N}");
        var sessionId = $"agent-connection-session-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"input-{Guid.NewGuid():N}",
            TurnId: $"turn-{Guid.NewGuid():N}",
            Prompt: "attach an agent connection session",
            Source: "agent-connection",
            JobId: $"job-{Guid.NewGuid():N}",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = project.Id,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
                [GenericAgentSessionMetadata.AgentId] = "connection-agent",
                [GenericAgentSessionMetadata.AgentName] = "Connection Agent",
            }),
            Runtime: "opencode"));

        var runnerId = $"generic-attach-connection-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });

        try
        {
            using var attach = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/attach",
                new
                {
                    runtimeSessionId = $"runtime-{Guid.NewGuid():N}",
                    workDir = "/tmp/generic-attach-connection",
                    processPid = 4321,
                });
            attach.EnsureSuccessStatusCode();
            var attachPayload = await attach.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("opencode", attachPayload.GetProperty("runtime").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", content: null);
        }
    }

    [Fact]
    public async Task GenericOpen_DefaultRuntime_OpensSessionWithOpenCode()
    {
        // Sanity: the default (no AgentConfig.runtime, no request
        // override) still resolves to opencode and is observed on the
        // open call.
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"generic-open-default-{Guid.NewGuid():N}");
        var agent = await CreateAgentAsync(project.Id, "open-default-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = "open on default" });
        launch.EnsureSuccessStatusCode();
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

        var runnerId = $"generic-open-default-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });

        try
        {
            using var open = await _fixture.Client.PostAsJsonAsync(
                $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
                new { workId = $"work-{Guid.NewGuid():N}" });
            open.EnsureSuccessStatusCode();
            var openPayload = await open.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("opencode", openPayload.GetProperty("runtime").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", content: null);
        }
    }

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name, string? runtime = null)
    {
        object agentConfig = runtime is null
            ? new { model = "openai/gpt-5.6" }
            : new { model = "openai/gpt-5.6", runtime };

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig,
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private sealed record ProjectDto(string Id);
    private sealed record AgentRef(string Id, string Name);
}
