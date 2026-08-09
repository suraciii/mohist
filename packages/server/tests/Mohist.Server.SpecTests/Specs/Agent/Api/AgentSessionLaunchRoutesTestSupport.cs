using Mohist.Server.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;
using Xunit.Sdk;
namespace Mohist.Server.SpecTests.Specs.Agent.Api;

public abstract class AgentSessionLaunchRoutesTestSupport
{
    protected readonly MohistIntegrationFixture _fixture;

    protected AgentSessionLaunchRoutesTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    protected static async Task WaitForJobTerminalAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        Func<Task> advance)
    {
        await advance();
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(expected, terminal.Status);
    }

    protected async Task<PollSnapshot> PollDispatchForSessionAsync(
        string agentJobId,
        string runnerId,
        string expectedSessionId)
    {
        var ready = await _fixture.AgentJobDispatches.WaitForAssignmentReadyForPollWithClockAsync(
            agentJobId,
            AgentJobDispatchProbe.DefaultWaitTimeout,
            _fixture.TimeProvider.Advance);
        Assert.Equal(runnerId, ready.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(ready.WorkId));
        var dispatch = await PollDispatchOnceAsync(
            runnerId,
            expectedSessionId,
            expectedAgentJobId: agentJobId);

        Assert.Equal(agentJobId, dispatch.AgentJobId);
        Assert.Equal(expectedSessionId, dispatch.AgentSessionId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, dispatch.OwnerKind);
        return dispatch;
    }

    protected async Task<string> PollAgentJobDispatchAsync(string agentJobId, string runnerId)
    {
        var ready = await _fixture.AgentJobDispatches.WaitForAssignmentReadyForPollWithClockAsync(
            agentJobId,
            AgentJobDispatchProbe.DefaultWaitTimeout,
            _fixture.TimeProvider.Advance);
        Assert.Equal(runnerId, ready.RunnerId);
        var dispatch = await PollDispatchOnceAsync(
            runnerId,
            expectedSessionId: null,
            expectedAgentJobId: agentJobId);
        Assert.Equal(agentJobId, dispatch.AgentJobId);
        return dispatch.WorkId;
    }

    protected Task<DispatchReadyForPoll> WaitForDispatchReadinessAsync(string agentJobId)
        => _fixture.AgentJobDispatches.WaitForAssignmentReadyForPollWithClockAsync(
            agentJobId,
            AgentJobDispatchProbe.DefaultWaitTimeout,
            _fixture.TimeProvider.Advance);

    protected Task<DispatchReadyForPoll> WaitForDispatchReadinessFromCurrentPointAsync(string agentJobId)
        => _fixture.AgentJobDispatches.WaitForAssignmentReadyForPollFromCurrentPointWithClockAsync(
            agentJobId,
            AgentJobDispatchProbe.DefaultWaitTimeout,
            _fixture.TimeProvider.Advance);

    private async Task<PollSnapshot> PollDispatchOnceAsync(
        string runnerId,
        string? expectedSessionId,
        string? expectedAgentJobId = null)
    {
        using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
        var dispatches = await poll.ReadDispatchElementsAsync();
        var matching = dispatches
            .Where(data => IsExpectedDispatch(data, expectedSessionId, expectedAgentJobId))
            .ToArray();
        Assert.True(
            matching.Length == 1,
            $"Expected exactly one dispatch for AgentJob '{expectedAgentJobId}' and session '{expectedSessionId}', "
            + $"but the single runner poll returned {dispatches.Count} dispatch(es) with {matching.Length} match(es).");

        var data = matching[0];
        var polledSessionId = ReadNullableString(data, "agentSessionId");
        var polledAgentJobId = ReadNullableString(data, "agentJobId");
        return new PollSnapshot(
            WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
            WorkId: data.GetProperty("workId").GetString() ?? string.Empty,
            AgentJobId: polledAgentJobId,
            ProjectId: ReadNullableString(data, "projectId"),
            AgentSessionId: polledSessionId,
            OwnerKind: ReadNullableString(data, "ownerKind"),
            Dispatch: data.Clone());
    }

    private static bool IsExpectedDispatch(
        JsonElement data,
        string? expectedSessionId,
        string? expectedAgentJobId)
    {
        var polledSessionId = ReadNullableString(data, "agentSessionId");
        var polledAgentJobId = ReadNullableString(data, "agentJobId");
        return (expectedSessionId is null || polledSessionId == expectedSessionId)
            && (expectedAgentJobId is null || polledAgentJobId == expectedAgentJobId);
    }

    private static string? ReadNullableString(JsonElement data, string propertyName)
        => data.TryGetProperty(propertyName, out var element)
            && element.ValueKind != JsonValueKind.Null
            ? element.GetString()
            : null;

    protected async Task CompletePendingAgentJobAsync(string runnerId, string agentJobId)
    {
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId);
        var state = await job.GetRuntimeSnapshotAsync();
        if (state.Status != AgentJobStatus.Pending)
            return;

        var ready = await _fixture.AgentJobDispatches.WaitForAssignmentReadyForPollWithClockAsync(
            agentJobId,
            AgentJobDispatchProbe.DefaultWaitTimeout,
            _fixture.TimeProvider.Advance);
        Assert.Equal(runnerId, ready.RunnerId);
        var dispatch = await PollDispatchOnceAsync(
            runnerId,
            expectedSessionId: null,
            expectedAgentJobId: agentJobId);
        Assert.Equal(agentJobId, dispatch.AgentJobId);
        var report = await job.ReportResultAsync(
            runnerId,
            dispatch.WorkId,
            new WorkResult(
                Status: "completed",
                Message: "completed by focused spec",
                Output: JSON.DeserializeElement("{}"),
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected the focused completion report");
    }

    protected sealed record PollSnapshot(
        string WorkflowRunId,
        string WorkId,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind,
        JsonElement Dispatch);

    protected async Task<string> CreateProjectAsync(string prefix)
    {
        // ProjectName caps at 63 DNS-label chars; trim the random suffix
        // so each project's name stays inside that bound.
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > ProjectDomainMaxLength
            ? raw[..ProjectDomainMaxLength]
            : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    protected const int ProjectDomainMaxLength = 63;

    protected async Task CreateWorkspaceAsync(string projectId, string name, IReadOnlyCollection<string>? repositories = null)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workspaces",
            new { name, repos = repositories ?? new[] { "main" } });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateWorkspace '{name}' failed: {(int)response.StatusCode} {body}");
        }
    }

    protected async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    /// <summary>
    /// Issue-512 T-001: POST a manual launch with a generated
    /// Idempotency-Key header. The route now requires the header (the
    /// coordinator owns the durable launch identity); existing tests
    /// that do not assert on idempotency semantics use a per-test
    /// GUID so the launch returns a fresh plan.
    /// </summary>
    protected Task<HttpResponseMessage> LaunchAsync(string projectId, string agentId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return _fixture.Client.SendAsync(request);
    }

    /// <summary>
    /// Issue-512 T-001: send a launch request with the supplied
    /// Idempotency-Key. Replays and conflict-resolution assertions
    /// use this so the test owns the key shape (the helper's
    /// <see cref="LaunchAsync(string,string,object)"/> uses a fresh
    /// GUID per call).
    /// </summary>
    protected Task<HttpResponseMessage> LaunchAsync(string projectId, string agentId, object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _fixture.Client.SendAsync(request);
    }

    protected Task<HttpResponseMessage> LaunchCliAsync(
        string projectId,
        string agentId,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agents/{agentId}/sessions/cli")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _fixture.Client.SendAsync(request);
    }

    /// <summary>
    /// Issue-512 T-001: send a launch request without an
    /// Idempotency-Key header. Used to assert the 400
    /// missing-header rejection gate.
    /// </summary>
    protected Task<HttpResponseMessage> LaunchWithoutIdempotencyKeyAsync(string projectId, string agentId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body),
        };
        return _fixture.Client.SendAsync(request);
    }

    /// <summary>
    /// Companion to <see cref="LaunchAsync(string,string,object)"/>
    /// for test classes that do not inherit from this support (the
    /// Sessions spec layer uses its own support). Returns a
    /// configured <see cref="HttpRequestMessage"/> the caller
    /// forwards to its own client.
    /// </summary>
    public static HttpRequestMessage BuildLaunchRequest(string projectId, string agentId, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return request;
    }

    protected async Task<AgentRef> CreateAgentAsync(string projectId, string name, string runtime)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { model = "openai/gpt-5.6", runtime },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    protected async Task PatchAgentRuntimeAsync(string projectId, string agentId, string runtime)
    {
        using var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agentId}",
            new
            {
                agentConfig = new { runtime },
            });
        response.EnsureSuccessStatusCode();
    }

    protected async Task<int> CreateIssueAsync(string projectId, string title)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, isDraft = true });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("number").GetInt32();
    }

    protected async Task<int> CreateEpicAsync(string projectId, string title)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/epics",
            new { title, description = "context epic", priority = "p2" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("number").GetInt32();
    }

    protected async Task RegisterRunnerAndAwaitOnlineAsync(string runnerId, string projectId)
    {
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runnerGrain.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, state.Status);
    }

    protected async Task<int> CountAgentLaunchSessionsAsync(string projectId)
    {
        var query = await GetAgentSessionQueryAsync();
        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            });
        return records.Count;
    }

    protected async Task<int> CountAgentJobsAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentJobs.CountAsync(job => job.ProjectId == projectId);
    }

    protected static async Task<IReadOnlyList<JsonElement>> LoadSessionClosedPayloadsAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToArrayAsync();
        var payloads = await db.AgentSessionTranscriptParts
            .AsNoTracking()
            // Issue 484: AgentJob terminal delivery now writes a
            // session.activity (activity=idle) part instead of the
            // deprecated session.closed part.
            .Where(p => turnIds.Contains(p.TurnId) && p.Type == TranscriptPartTypes.SessionActivity)
            .OrderBy(p => p.Sequence)
            .Select(p => p.PayloadJson)
            .ToArrayAsync();
        return payloads.Select(payload => JsonSerializer.Deserialize<JsonElement>(payload)).ToArray();
    }

    protected async Task<AgentSessionQuery> GetAgentSessionQueryAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
    }

    protected async Task<IAgentJobGrain?> FindAgentJobGrainAsync(string sessionId)
    {
        var launch = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(sessionId)
            .GetInitialLaunchAsync();
        return launch?.Turn?.JobId is { Length: > 0 } jobId
            ? _fixture.Grains.GetGrain<IAgentJobGrain>(jobId)
            : null;
    }

    protected async Task<AgentJobRuntimeSnapshot?> FindAgentJobSnapshotAsync(string sessionId)
    {
        var job = await FindAgentJobGrainAsync(sessionId);
        return job is null ? null : await job.GetRuntimeSnapshotAsync();
    }

    protected sealed record AgentRef(string Id, string Name);
}
