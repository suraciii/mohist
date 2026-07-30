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
        TimeSpan timeout,
        Func<Task>? advance = null)
        => await TestWait.ForAsync(
            () => job.GetTerminalResultAsync(),
            t => t.Status == expected,
            timeout,
            TimeSpan.FromMilliseconds(200),
            $"Agent job to reach {expected}",
            advance);

    protected async Task<PollSnapshot> PollDispatchForSessionAsync(
        string agentJobId,
        string runnerId,
        string expectedSessionId)
    {
        var assignment = await _fixture.AgentJobDispatches.WaitForRunnerAcceptedAsync(agentJobId);
        Assert.Equal(runnerId, assignment.RunnerId);

        var dispatch = await PollDispatchOnceAsync(runnerId, expectedSessionId);
        return dispatch ?? throw new XunitException(
            $"Runner '{runnerId}' accepted AgentJob '{agentJobId}' but did not return its dispatch for AgentSessionId '{expectedSessionId}'.\n" +
            $"Runner registry:\n{await _fixture.DescribeRunnerRegistryAsync()}");
    }

    private async Task<PollSnapshot?> PollDispatchOnceAsync(string runnerId, string expectedSessionId)
    {
        using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
        var dispatches = await poll.ReadDispatchElementsAsync();
        PollSnapshot? match = null;
        var others = new List<JsonElement>();
        foreach (var data in dispatches)
        {
            var polledSessionId = data.TryGetProperty("agentSessionId", out var sessionIdElement)
                && sessionIdElement.ValueKind != JsonValueKind.Null
                ? sessionIdElement.GetString()
                : null;
            if (match is null && polledSessionId == expectedSessionId)
            {
                var workId = data.GetProperty("workId").GetString() ?? string.Empty;
                var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement)
                    && agentJobIdElement.ValueKind != JsonValueKind.Null
                    ? agentJobIdElement.GetString()
                    : null;
                var projectId = data.TryGetProperty("projectId", out var projectIdElement)
                    && projectIdElement.ValueKind != JsonValueKind.Null
                    ? projectIdElement.GetString()
                    : null;
                var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement)
                    && ownerKindElement.ValueKind != JsonValueKind.Null
                    ? ownerKindElement.GetString()
                    : null;
                match = new PollSnapshot(
                    WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                    WorkId: workId,
                    AgentJobId: agentJobId,
                    ProjectId: projectId,
                    AgentSessionId: polledSessionId,
                    OwnerKind: ownerKind);
            }
            else
            {
                others.Add(data);
            }
        }

        foreach (var other in others)
            await DrainDispatchElementAsync(runnerId, other);

        return match;
    }

    protected async Task DrainDispatchAsync(string runnerId)
    {
        for (var i = 0; i < 30; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            if (dispatches.Count == 0) return;
            foreach (var data in dispatches)
                await DrainDispatchElementAsync(runnerId, data);
        }
    }

    protected async Task DrainDispatchElementAsync(string runnerId, JsonElement data)
    {
        var workId = data.GetProperty("workId").GetString();
        var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement)
            && ownerKindElement.ValueKind != JsonValueKind.Null
            ? ownerKindElement.GetString()
            : null;

        if (!string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            return;

        var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement)
            && agentJobIdElement.ValueKind != JsonValueKind.Null
            ? agentJobIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(agentJobId) || string.IsNullOrWhiteSpace(workId))
            return;

        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            workId!,
            new WorkResult(
                Status: "completed",
                Message: "drained",
                Output: JSON.DeserializeElement("{}"),
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected drain report");
    }

    protected sealed record PollSnapshot(
        string WorkflowRunId,
        string WorkId,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind);

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
        await TestWait.ForAsync(
            () => runnerGrain.GetRuntimeStateAsync(),
            s => s.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"Runner '{runnerId}' to reach Online");
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

    /// <summary>
    /// Looks up the AgentJob grain that owns the dispatched work for the
    /// given generic session id. The launch endpoint mints a fresh
    /// <c>agent-job-launch-{guid}</c> key per launch, so this probes the
    /// runner registry to find the active work item and recovers the key.
    /// A job that found no runner on its first attempt owns no work item
    /// until a backoff retry lands, and the frozen fake clock releases those
    /// only on demand. The registry is shared, so the session id must be
    /// matched or a concurrent job's grain is returned.
    /// </summary>
    protected async Task<IAgentJobGrain?> FindAgentJobGrainAsync(string sessionId)
    {
        return await TestWait.ForAsync(
            () => ProbeAgentJobGrainAsync(sessionId),
            job => job is not null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            $"agent job grain for session '{sessionId}'",
            _fixture.ReleaseDispatchBackoffAsync);
    }

    protected async Task<IAgentJobGrain?> ProbeAgentJobGrainAsync(string sessionId)
    {
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnerIdsAsync();
        foreach (var runnerId in runners)
        {
            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var state = await runner.GetRuntimeStateAsync();
            foreach (var work in state.ActiveWorks)
            {
                if (string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
                {
                    var job = _fixture.Grains.GetGrain<IAgentJobGrain>(work.OwnerId);
                    var snapshot = await job.GetRuntimeSnapshotAsync();
                    if (snapshot.CurrentWorkId == work.WorkId
                        && string.Equals(snapshot.AgentSessionId, sessionId, StringComparison.Ordinal))
                    {
                        return job;
                    }
                }
            }
        }

        return null;
    }

    protected async Task<AgentJobRuntimeSnapshot?> FindAgentJobSnapshotAsync(string sessionId)
    {
        var job = await FindAgentJobGrainAsync(sessionId);
        return job is null ? null : await job.GetRuntimeSnapshotAsync();
    }

    protected sealed record AgentRef(string Id, string Name);
}
