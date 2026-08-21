using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Readiness + definition-first launch behavior under the Project default
/// execution configuration (issue-560 T-001): a default resolves
/// model-missing / variant-without-model into launchable dispatches with the
/// model Readiness resolved, resolution happens once at launch, and without
/// a default the existing Needs-setup gating is unchanged.
/// </summary>
[Collection("LaunchIntegration")]
public sealed class AgentSessionLaunchDefaultExecutionConfigSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchDefaultExecutionConfigSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_WithoutDefaultAndWithoutModel_IsBlockedByReadiness()
    {
        var projectId = await CreateProjectAsync("launch-default-missing");
        var agent = await CreateModellessAgentAsync(projectId, "gap-agent");

        var readiness = await GetReadinessAsync(projectId, agent.Id);
        Assert.Equal("not-configured", readiness.Conclusion);
        Assert.Contains("model-missing", readiness.Gaps);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(
            projectId,
            agent.Id,
            new { prompt = "run the task" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_not_configured", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_WithProjectDefault_DispatchesWithTheResolvedConfiguration()
    {
        var projectId = await CreateProjectAsync("launch-default-model");
        var agent = await CreateModellessAgentAsync(projectId, "default-model-agent");
        await SetDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");
        var runnerId = $"launch-default-model-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            // The default resolved the gap: Readiness is no longer
            // Not needs setup even though the definition carries no model.
            var readiness = await GetReadinessAsync(projectId, agent.Id);
            Assert.Equal("unknown", readiness.Conclusion);
            Assert.DoesNotContain("model-missing", readiness.Gaps);

            using var response = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                agent.Id,
                new { prompt = "run with the project default" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var snapshot = await ClaimDispatchForSessionAsync(jobId, runnerId, sessionId);
            var dispatch = await PollDispatchEnvelopeForWorkAsync(runnerId, snapshot.WorkId!);
            Assert.Equal("openai/gpt-5.6", ReadModelFromDispatch(dispatch));
            Assert.Equal("high", ReadVariantFromDispatch(dispatch));
            Assert.Equal("pi", ReadRuntimeFromDispatch(dispatch));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_IsResolvedOnceAtLaunch_DefaultEditDoesNotChangeTheSnapshot()
    {
        var projectId = await CreateProjectAsync("launch-resolved-once");
        var agent = await CreateModellessAgentAsync(projectId, "resolved-once-agent");
        await SetDefaultAsync(projectId, "opencode", "openai/gpt-5.6", null);
        var runnerId = $"launch-resolved-once-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                agent.Id,
                new { prompt = "snapshot the resolution" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            // Change the Project default after the launch converged: the
            // in-flight execution keeps the model resolved at launch time.
            await SetDefaultAsync(projectId, "opencode", "anthropic/sonnet-4.6", null);

            var jobSnapshot = await _fixture.Grains.GetGrain<IAgentJobGrain>(jobId).GetRuntimeSnapshotAsync();
            Assert.Equal("openai/gpt-5.6", jobSnapshot!.ExecutionDefinition!.Model);

            var sessionInfo = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            var snapshot = await ClaimDispatchForSessionAsync(jobId, runnerId, sessionId);
            var dispatch = await PollDispatchEnvelopeForWorkAsync(runnerId, snapshot.WorkId!);
            Assert.Equal("openai/gpt-5.6", ReadModelFromDispatch(dispatch));
            Assert.NotNull(sessionInfo);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task<AgentReadinessAssertion> GetReadinessAsync(string projectId, string agentId)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/{agentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var readiness = body.GetProperty("data").GetProperty("executability");
        return new AgentReadinessAssertion(
            readiness.GetProperty("state").GetString()!,
            readiness.GetProperty("gaps").EnumerateArray()
                .Select(gap => gap.GetProperty("code").GetString()!)
                .ToArray());
    }

    private readonly record struct AgentReadinessAssertion(string Conclusion, IReadOnlyList<string> Gaps)
    {
        public bool Contains(string code) => Gaps.Contains(code);
    }

    private async Task<AgentRef> CreateModellessAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                skills = Array.Empty<string>(),
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task<AgentRef> CreateAgentWithConfigAsync(
        string projectId,
        string name,
        object config)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = config,
                skills = Array.Empty<string>(),
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task SetDefaultAsync(
        string projectId,
        string runtime,
        string model,
        string? variant)
    {
        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            (object)(variant is null ? new { runtime, model } : new { runtime, model, variant }));
        Assert.True(
            response.IsSuccessStatusCode,
            $"setting the default execution config failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<JsonElement> PollDispatchEnvelopeForWorkAsync(string runnerId, string workId)
    {
        for (var i = 0; i < 50; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            foreach (var data in dispatches)
            {
                if (string.Equals(data.GetProperty("workId").GetString(), workId, StringComparison.Ordinal))
                    return data;
                await DrainDispatchElementAsync(runnerId, data);
            }
        }

        throw new InvalidOperationException($"No polled dispatch for workId '{workId}'");
    }

    private static string ReadModelFromDispatch(JsonElement dispatch) => ReadFromDispatchWith(dispatch, "model");

    private static string ReadVariantFromDispatch(JsonElement dispatch) => ReadFromDispatchWith(dispatch, "variant");

    private static string ReadRuntimeFromDispatch(JsonElement dispatch) => ReadFromDispatchWith(dispatch, "runtime");

    private static string ReadFromDispatchWith(JsonElement dispatch, string field)
    {
        var withJson = dispatch.GetProperty("with").GetString();
        Assert.False(string.IsNullOrWhiteSpace(withJson));
        using var doc = JsonDocument.Parse(withJson!);
        Assert.True(
            doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String,
            $"the dispatch envelope carries no '{field}'");
        return value.GetString()!;
    }
}
