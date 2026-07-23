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

    protected async Task<PollSnapshot> PollDispatchForSessionAsync(string runnerId, string expectedSessionId)
    {
        var attempts = 50;
        for (var i = 0; i < attempts; i++)
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

            if (match is not null) return match;
        }

        throw new InvalidOperationException($"No polled dispatch carrying AgentSessionId='{expectedSessionId}' after {attempts} attempts");
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
    /// Polls until the runner picks up the dispatch (the grain's
    /// <see cref="AgentJobGrain.TryDispatchAsync"/> runs asynchronously
    /// after <c>SubmitAsync</c> returns, so the work may not be visible
    /// on the runner's active list the instant the 201 is observed).
    /// </summary>
    protected async Task<IAgentJobGrain?> FindAgentJobGrainAsync(string sessionId)
    {
        return await TestWait.ForAsync(
            ProbeAgentJobGrainAsync,
            job => job is not null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            $"agent job grain for session '{sessionId}'");
    }

    protected async Task<IAgentJobGrain?> ProbeAgentJobGrainAsync()
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
                    if (snapshot.CurrentWorkId == work.WorkId)
                        return job;
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
