using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Spec coverage for the new project-scoped
/// <c>GET /api/projects/{projectRef}/agents/availability</c> endpoint
/// (issue #133 / T-001). The Agents list reads this summary to render
/// Availability and active/queued workload for every Agent with a single
/// HTTP call — the route fetches runner capacity exactly once regardless
/// of Agent count and serves one entry per active Agent.
/// </summary>
public sealed class AgentAvailabilityListRoutesSpecs : IClassFixture<AgentAvailabilityListFixture>
{
    private readonly AgentAvailabilityListFixture _fixture;

    public AgentAvailabilityListRoutesSpecs(AgentAvailabilityListFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetAvailability_NAgents_CallsRunnerStatusSourceExactlyOnce()
    {
        var projectId = await CreateProjectAsync("availability-single-read");
        var first = await CreateAgentAsync(projectId, "alpha");
        var second = await CreateAgentAsync(projectId, "beta");
        var third = await CreateAgentAsync(projectId, "gamma");
        _fixture.SetOnlineRunners(
        [
            new RunnerStatusView(
                Id: "runner-1",
                Kind: "external",
                Hostname: "host-1",
                Scope: new RunnerScopeView("global"),
                Status: "idle",
                RegisteredAt: null,
                LastHeartbeatAt: null,
                ConnectionState: "connected",
                Capabilities: Array.Empty<string>(),
                CoderModels: Array.Empty<string>(),
                CoderModelCount: 0,
                Capacity: new RunnerCapacityView(0, 4),
                ActiveWorks: Array.Empty<RunnerActiveWorkView>()),
        ]);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        var entries = payload.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(3, entries.Length);

        var returnedIds = entries
            .Select(e => e.GetProperty("agentId").GetString()!)
            .ToHashSet();
        Assert.Equal(new HashSet<string> { first.Id, second.Id, third.Id }, returnedIds);

        Assert.Equal(1, _fixture.RunnerStatus.CallCount);
    }

    [Fact]
    public async Task GetAvailability_ReadyAgentNoOnlineRunner_ReportsNoOnlineRunnerAvailability()
    {
        var projectId = await CreateProjectAsync("availability-offline");
        var agent = await CreateAgentAsync(projectId, "ready-offline");
        await SeedCompletedJobAsync(projectId, agent.Id, "ready-offline");
        _fixture.SetOnlineRunners([]);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(payload.GetProperty("data").EnumerateArray());
        Assert.Equal(agent.Id, entry.GetProperty("agentId").GetString());
        Assert.False(entry.GetProperty("canStartNow").GetBoolean());
        Assert.Equal("no-online-runner", entry.GetProperty("waitingReason").GetString());
        Assert.Equal(0, entry.GetProperty("activeRuns").GetInt32());
        Assert.Equal(0, entry.GetProperty("queuedCount").GetInt32());
        var capacity = entry.GetProperty("capacity");
        Assert.Equal(0, capacity.GetProperty("usedSlots").GetInt32());
        Assert.Equal(0, capacity.GetProperty("totalSlots").GetInt32());
        Assert.Equal(2, entry.GetProperty("maxConcurrentRuns").GetInt32());

        using var listResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents");
        var listPayload = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var listedAgent = Assert.Single(listPayload.GetProperty("data").EnumerateArray());
        Assert.Equal("Ready", listedAgent.GetProperty("readiness").GetProperty("conclusion").GetString());
    }

    [Fact]
    public async Task GetAvailability_OmitsArchivedAgentsFromSummary()
    {
        var projectId = await CreateProjectAsync("availability-archived");
        var active = await CreateAgentAsync(projectId, "still-active");
        var archived = await CreateAgentAsync(projectId, "archived-agent");
        await ArchiveAgentAsync(projectId, archived.Id);
        _fixture.SetOnlineRunners([]);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = payload.GetProperty("data").EnumerateArray().ToArray();
        var single = Assert.Single(entries);
        Assert.Equal(active.Id, single.GetProperty("agentId").GetString());
        Assert.DoesNotContain(entries, e => e.GetProperty("agentId").GetString() == archived.Id);
        Assert.Equal(1, _fixture.RunnerStatus.CallCount);
    }

    [Fact]
    public async Task GetAvailability_EmptyProject_ReturnsEmptyArray()
    {
        var projectId = await CreateProjectAsync("availability-empty");
        _fixture.SetOnlineRunners([]);

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(0, payload.GetProperty("data").GetArrayLength());
        Assert.Equal(1, _fixture.RunnerStatus.CallCount);
    }

    [Fact]
    public async Task GetAvailability_QueuedCountMatchesPendingJobsAndActiveRunsFromConcurrencyGrain()
    {
        var projectId = await CreateProjectAsync("availability-counts");
        var agent = await CreateAgentAsync(projectId, "counter-agent");
        var idle = await CreateAgentAsync(projectId, "idle-agent");
        _fixture.SetOnlineRunners(
        [
            new RunnerStatusView(
                Id: "runner-1",
                Kind: "external",
                Hostname: "host-1",
                Scope: new RunnerScopeView("global"),
                Status: "idle",
                RegisteredAt: null,
                LastHeartbeatAt: null,
                ConnectionState: "connected",
                Capabilities: Array.Empty<string>(),
                CoderModels: Array.Empty<string>(),
                CoderModelCount: 0,
                Capacity: new RunnerCapacityView(0, 4),
                ActiveWorks: Array.Empty<RunnerActiveWorkView>()),
        ]);

        await SeedPendingJobAsync(projectId, agent.Id, "job-1");
        await SeedPendingJobAsync(projectId, agent.Id, "job-2");
        await SeedPendingJobAsync(projectId, agent.Id, "job-3");
        await AcquireConcurrencyPermitAsync(projectId, agent.Id, "active-1");
        await AcquireConcurrencyPermitAsync(projectId, agent.Id, "active-2");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = payload.GetProperty("data").EnumerateArray()
            .ToDictionary(e => e.GetProperty("agentId").GetString()!);

        var counter = entries[agent.Id];
        Assert.Equal(2, counter.GetProperty("activeRuns").GetInt32());
        Assert.Equal(3, counter.GetProperty("queuedCount").GetInt32());

        var idleEntry = entries[idle.Id];
        Assert.Equal(0, idleEntry.GetProperty("activeRuns").GetInt32());
        Assert.Equal(0, idleEntry.GetProperty("queuedCount").GetInt32());
    }

    [Fact]
    public async Task GetAvailability_AndStatus_IncludeCanonicalFollowupGateWaiter()
    {
        var projectId = await CreateProjectAsync("availability-followup-gate");
        var agent = await CreateAgentAsync(projectId, "followup-agent", maxConcurrentRuns: 1);
        _fixture.SetOnlineRunners(
        [
            new RunnerStatusView(
                Id: "runner-1",
                Kind: "external",
                Hostname: "host-1",
                Scope: new RunnerScopeView("global"),
                Status: "idle",
                RegisteredAt: null,
                LastHeartbeatAt: null,
                ConnectionState: "connected",
                Capabilities: Array.Empty<string>(),
                CoderModels: Array.Empty<string>(),
                CoderModelCount: 0,
                Capacity: new RunnerCapacityView(0, 4),
                ActiveWorks: Array.Empty<RunnerActiveWorkView>()),
        ]);

        var gate = _fixture.Services.GetRequiredService<Orleans.IGrainFactory>()
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agent.Id));
        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agent.Id, "job-active", "job-active", AgentConcurrencyPermitOwnerKind.Job));
        Assert.Equal(
            AgentConcurrencyAcquireResult.Waiting,
            await gate.AcquireAsync(
                projectId,
                agent.Id,
                "followup:queued",
                "session-followup",
                AgentConcurrencyPermitOwnerKind.Followup,
                "followup:queued"));

        using var availabilityResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");
        Assert.Equal(HttpStatusCode.OK, availabilityResponse.StatusCode);
        var availabilityPayload = await availabilityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(availabilityPayload.GetProperty("data").EnumerateArray());
        Assert.Equal(1, entry.GetProperty("activeRuns").GetInt32());
        Assert.Equal(1, entry.GetProperty("queuedCount").GetInt32());
        Assert.Equal("capacity-full", entry.GetProperty("waitingReason").GetString());

        using var statusResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusPayload = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var status = statusPayload.GetProperty("data");
        Assert.Equal(1, status.GetProperty("availability").GetProperty("activeRuns").GetInt32());
        Assert.Equal("capacity-full", status.GetProperty("availability").GetProperty("waitingReason").GetString());
        var waiting = status.GetProperty("waitingWork").EnumerateArray().ToArray();
        var followup = Assert.Single(waiting);
        Assert.Equal("session-followup", followup.GetProperty("jobId").GetString());
        Assert.Equal("capacity-full", followup.GetProperty("waitingReason").GetString());
    }

    private async Task SeedPendingJobAsync(string projectId, string agentId, string jobKey)
    {
        var store = _fixture.Services.GetRequiredService<IAgentJobStore>();
        var input = new AgentJobInput(
            Prompt: "seed for count derivation test",
            ProjectId: projectId,
            AgentId: agentId);
        var state = new AgentJobState
        {
            Status = AgentJobStatus.Pending,
            Input = input,
            SubmittedAt = _fixture.TimeProvider.GetUtcNow(),
        };
        await store.SaveAsync(jobKey, JsonSerializer.Serialize(state, JSON.Options));
    }

    private async Task SeedCompletedJobAsync(string projectId, string agentId, string agentName)
    {
        var store = _fixture.Services.GetRequiredService<IAgentJobStore>();
        var state = new AgentJobState
        {
            Status = AgentJobStatus.Completed,
            Input = new AgentJobInput(
                Prompt: "seed for readiness test",
                Model: "openai/gpt-5.6",
                ProjectId: projectId,
                Runtime: "opencode",
                AgentId: agentId,
                AgentInstructions: $"instructions for {agentName}",
                Skills: ["coding"]),
            SubmittedAt = _fixture.TimeProvider.GetUtcNow(),
            TerminalAt = _fixture.TimeProvider.GetUtcNow(),
        };
        await store.SaveAsync($"completed-{Guid.NewGuid():N}", JsonSerializer.Serialize(state, JSON.Options));
    }

    private async Task AcquireConcurrencyPermitAsync(string projectId, string agentId, string token)
    {
        var grains = _fixture.Services.GetRequiredService<Orleans.IGrainFactory>();
        var gate = grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));
        await gate.AcquireAsync(projectId, agentId, token, $"job-{token}", AgentConcurrencyPermitOwnerKind.Job);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var projectName = $"{prefix}-{Guid.NewGuid():N}";
        var trimmed = projectName.Length > 63 ? projectName[..63] : projectName;
        var response = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            trimmed);
        return response.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Project create did not return an id");
    }

    private async Task<AgentWire> CreateAgentAsync(string projectId, string name, int maxConcurrentRuns = 2)
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns,
            });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = payload.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Agent create did not return an id");
        return new AgentWire(id);
    }

    private async Task ArchiveAgentAsync(string projectId, string agentId)
    {
        using var response = await _fixture.Client.DeleteAsync(
            $"/api/projects/{projectId}/agents/{agentId}");
        response.EnsureSuccessStatusCode();
    }

    private sealed record AgentWire(string Id);
}
