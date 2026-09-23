using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

/// <summary>
/// Spec coverage for the new project-scoped
/// <c>GET /api/projects/{projectRef}/agents/availability</c> endpoint
/// (issue #133 / T-001). The Agents list reads this summary to render
/// Availability and active/queued workload for every Agent with a single
/// HTTP call — the route fetches runner capacity exactly once regardless
/// of Agent count, reads the derived owner stores exactly once for the
/// whole request, and serves one entry per active Agent. Counts are
/// derived from the real SQLite owner facts; an unattributable owner row
/// nulls the counts instead of presenting them as zero.
/// </summary>
[Trait("level", "L1")]
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
        // The availability list mirrors the Agents surface, which since the
        // Agent-definition authority change also lists the built-in Workflow
        // Agents (planner/builder/reviewer) alongside Project Agents.
        Assert.Equal(6, entries.Length);

        var returnedIds = entries
            .Select(e => e.GetProperty("agentId").GetString()!)
            .ToHashSet();
        Assert.Subset(returnedIds, new HashSet<string> { first.Id, second.Id, third.Id });
        Assert.Contains("builtin:mohist/planner", returnedIds);
        Assert.Contains("builtin:mohist/builder", returnedIds);
        Assert.Contains("builtin:mohist/reviewer", returnedIds);

        Assert.Equal(1, _fixture.RunnerStatus.CallCount);
        // Every Agent's derived facts come from one batched store read, not
        // a per-Agent query.
        Assert.Equal(1, _fixture.CapacityReads.Count);
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
        var single = Assert.Single(entries, e => e.GetProperty("agentId").GetString() == active.Id);
        Assert.Equal(active.Id, single.GetProperty("agentId").GetString());
        Assert.DoesNotContain(entries, e => e.GetProperty("agentId").GetString() == archived.Id);
        // Built-in Workflow Agents stay available; only the archived Project
        // Agent is omitted.
        Assert.Contains(entries, e => e.GetProperty("agentId").GetString() == "builtin:mohist/planner");
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
        // A Project without its own Agents still lists the built-in Workflow
        // Agents, mirroring the Agents surface.
        var ids = payload.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("agentId").GetString()!)
            .ToHashSet();
        Assert.Equal(
            new HashSet<string> { "builtin:mohist/planner", "builtin:mohist/builder", "builtin:mohist/reviewer" },
            ids);
        Assert.Equal(1, _fixture.RunnerStatus.CallCount);
    }

    [Fact]
    public async Task GetAvailability_DerivesActiveAndQueuedCountsFromOwnerFacts()
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

        // Three accepted visible Pending Jobs queue; two running Jobs occupy
        // the Agent's limit of two straight from their owner status.
        await SeedPendingJobAsync(projectId, agent.Id, "job-1");
        await SeedPendingJobAsync(projectId, agent.Id, "job-2");
        await SeedPendingJobAsync(projectId, agent.Id, "job-3");
        await SeedRunningJobAsync(projectId, agent.Id, "job-running-1");
        await SeedRunningJobAsync(projectId, agent.Id, "job-running-2");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = payload.GetProperty("data").EnumerateArray()
            .ToDictionary(e => e.GetProperty("agentId").GetString()!);

        var counter = entries[agent.Id];
        Assert.Equal(2, counter.GetProperty("activeRuns").GetInt32());
        Assert.Equal(3, counter.GetProperty("queuedCount").GetInt32());
        Assert.False(counter.GetProperty("canStartNow").GetBoolean());
        Assert.Equal("concurrency-limit", counter.GetProperty("waitingReason").GetString());

        var idleEntry = entries[idle.Id];
        Assert.Equal(0, idleEntry.GetProperty("activeRuns").GetInt32());
        Assert.Equal(0, idleEntry.GetProperty("queuedCount").GetInt32());
        // An idle Agent keeps its Runner-level blocker: the shared fake runner
        // witnesses no Runtime, and that reason is preserved verbatim.
        Assert.False(idleEntry.GetProperty("canStartNow").GetBoolean());
        Assert.Equal("runtime-not-ready", idleEntry.GetProperty("waitingReason").GetString());
    }

    [Fact]
    public async Task GetAvailability_AndStatus_IncludeQueuedFollowupTurnFromTheDerivedRead()
    {
        var projectId = await CreateProjectAsync("availability-followup-queue");
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
        var acceptedAt = _fixture.TimeProvider.GetUtcNow();
        // A claimed, still-queued follow-up Turn: it occupies the Agent's
        // single slot and stays visible waiting work in the same read.
        await SeedQueuedFollowupSessionAsync(projectId, agent.Id, "session-followup", acceptedAt, claimedAt: acceptedAt);

        using var availabilityResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");
        Assert.Equal(HttpStatusCode.OK, availabilityResponse.StatusCode);
        var availabilityPayload = await availabilityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(
            availabilityPayload.GetProperty("data").EnumerateArray(),
            e => e.GetProperty("agentId").GetString() == agent.Id);
        Assert.Equal(1, entry.GetProperty("activeRuns").GetInt32());
        Assert.Equal(1, entry.GetProperty("queuedCount").GetInt32());
        Assert.False(entry.GetProperty("canStartNow").GetBoolean());
        Assert.Equal("concurrency-limit", entry.GetProperty("waitingReason").GetString());

        using var statusResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusPayload = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var status = statusPayload.GetProperty("data");
        Assert.Equal(1, status.GetProperty("availability").GetProperty("activeRuns").GetInt32());
        Assert.Equal("concurrency-limit", status.GetProperty("availability").GetProperty("waitingReason").GetString());
        // The waiting list keeps the established contract: a Session-owned
        // turn carries its Session id, with its acceptance timestamp.
        var waiting = status.GetProperty("waitingWork").EnumerateArray().ToArray();
        var followup = Assert.Single(waiting);
        Assert.Equal("session-followup", followup.GetProperty("jobId").GetString());
        Assert.Equal("waiting", followup.GetProperty("status").GetString());
        Assert.Equal("concurrency-limit", followup.GetProperty("waitingReason").GetString());
        Assert.Equal(acceptedAt.ToString("o"), followup.GetProperty("submittedAt").GetString());
    }

    [Fact]
    public async Task GetAvailability_AndStatus_PerformOneCapacityReadPerRequest()
    {
        var projectId = await CreateProjectAsync("availability-one-read");
        await CreateAgentAsync(projectId, "alpha");
        await CreateAgentAsync(projectId, "beta");
        var detail = await CreateAgentAsync(projectId, "gamma");
        _fixture.SetOnlineRunners([]);

        using var availabilityResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");
        Assert.Equal(HttpStatusCode.OK, availabilityResponse.StatusCode);
        Assert.Equal(1, _fixture.CapacityReads.Count);

        _fixture.CapacityReads.Reset();
        using var statusResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{detail.Id}/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        // The detail conclusion and its waiting-work list share one snapshot.
        Assert.Equal(1, _fixture.CapacityReads.Count);
    }

    [Fact]
    public async Task GetAvailability_UnattributableOwnerWork_NullsCountsAndReportsDispatchPending()
    {
        var projectId = await CreateProjectAsync("availability-incomplete");
        var agent = await CreateAgentAsync(projectId, "shaded-agent");
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
        // A Session row whose indexed Agent label is null still queues
        // ordinary work: no Agent can be credited or cleared with it.
        await SeedUnattributedQueuedSessionAsync(projectId);

        using var availabilityResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/availability");
        Assert.Equal(HttpStatusCode.OK, availabilityResponse.StatusCode);
        var availabilityPayload = await availabilityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entries = availabilityPayload.GetProperty("data").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            // The unknown counts are absent from the wire rather than coerced
            // to zero, and the conclusion stays waiting with incomplete
            // owner evidence.
            Assert.False(entry.TryGetProperty("activeRuns", out _));
            Assert.False(entry.TryGetProperty("queuedCount", out _));
            Assert.False(entry.GetProperty("canStartNow").GetBoolean());
            Assert.Equal("dispatch-pending", entry.GetProperty("waitingReason").GetString());
            Assert.True(entry.GetProperty("capacity").GetProperty("incomplete").GetBoolean());
        });

        using var statusResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusPayload = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var availability = statusPayload.GetProperty("data").GetProperty("availability");
        Assert.False(availability.TryGetProperty("activeRuns", out _));
        Assert.True(availability.GetProperty("capacityIncomplete").GetBoolean());
        Assert.Equal("dispatch-pending", availability.GetProperty("waitingReason").GetString());
        Assert.False(availability.GetProperty("canStartNow").GetBoolean());
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

    private async Task SeedRunningJobAsync(string projectId, string agentId, string jobKey)
    {
        var store = _fixture.Services.GetRequiredService<IAgentJobStore>();
        var input = new AgentJobInput(
            Prompt: "seed running occupancy test",
            ProjectId: projectId,
            AgentId: agentId);
        var state = new AgentJobState
        {
            Status = AgentJobStatus.Running,
            Input = input,
            SubmittedAt = _fixture.TimeProvider.GetUtcNow(),
        };
        await store.SaveAsync(jobKey, JsonSerializer.Serialize(state, JSON.Options));
    }

    private async Task SeedQueuedFollowupSessionAsync(
        string projectId,
        string agentId,
        string sessionId,
        DateTimeOffset acceptedAt,
        DateTimeOffset? claimedAt)
    {
        var recordedAt = acceptedAt.UtcDateTime;
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", agentId);
        var session = AgentSession.Create(sessionId, "runner", "/work", metadata, recordedAt, "pi");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            ContextGeneration = 1,
            Inputs = [new("input", 1, "prompt", "api", AgentSessionInputAcceptance.Accepted,
                recordedAt, ContextGeneration: 1)],
            Turns = [new("turn", 1, ["input"], AgentTurnStatus.Queued, RecordedAt: recordedAt,
                ContextGeneration: 1, CapacityClaimedAt: claimedAt)],
            PendingFollowups = [new("system-turn:turn", "runtime", Accepted: true,
                AcceptedAt: recordedAt, StartedAt: recordedAt, InputId: "input", TurnId: "turn")],
        };
        await AddSessionRowAsync(session, recordedAt);
    }

    private async Task SeedUnattributedQueuedSessionAsync(string projectId)
    {
        // A readable Agent-owned row that lost its agent label: its
        // queued ordinary Turn cannot be attributed to any requested Agent.
        var recordedAt = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/source-id", "workflow-run-1")
            .WithLabel("mohist.io/session-name", "build")
            .WithLabel("mohist.io/agent-id", "unattributed-agent");
        var session = AgentSession.Create("session-unattributed", "runner", "/work", metadata, recordedAt, "pi");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            ContextGeneration = 1,
            Inputs = [new("input", 1, "prompt", "api", AgentSessionInputAcceptance.Accepted,
                recordedAt, ContextGeneration: 1)],
            Turns = [new("turn", 1, ["input"], AgentTurnStatus.Queued, RecordedAt: recordedAt,
                ContextGeneration: 1)],
            PendingFollowups = [new("system-turn:turn", "runtime", Accepted: true,
                AcceptedAt: recordedAt, StartedAt: recordedAt, InputId: "input", TurnId: "turn")],
        };
        var row = AgentSessionJson.ToRow(session, recordedAt);
        var state = JsonNode.Parse(row.State)!.AsObject();
        state["metadata"]!["labels"]!.AsObject().Remove("mohist.io/agent-id");
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        row.State = state.ToJsonString();
        db.AgentSessions.Add(row);
        await db.SaveChangesAsync();
    }

    private async Task AddSessionRowAsync(AgentSession session, DateTime updatedAt)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.AgentSessions.Add(AgentSessionJson.ToRow(session, updatedAt));
        await db.SaveChangesAsync();
    }

    private async Task<AgentSessionRow> LoadSessionRowAsync(string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        return await db.AgentSessions.SingleAsync(candidate => candidate.Id == sessionId);
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
