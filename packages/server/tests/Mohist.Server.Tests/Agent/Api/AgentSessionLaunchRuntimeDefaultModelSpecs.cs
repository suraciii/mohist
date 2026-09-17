using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

/// <summary>
/// Readiness + definition-first launch behavior for an Agent with an unset
/// Model: no Project value participates, the Runtime chooses the model at
/// dispatch, and the accepted Job snapshot records the Runtime-chosen
/// (null) model. Nothing substitutes a borrowed model.
/// </summary>
[Collection("LaunchIntegration")]
[Trait("level", "L1")]
public sealed class AgentSessionLaunchRuntimeDefaultModelSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchRuntimeDefaultModelSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task UnsetModel_IsNotAReadinessGap_AndLaunchIsAccepted()
    {
        var projectId = await CreateProjectAsync("launch-runtime-default");
        var agent = await CreateModellessAgentAsync(projectId, "runtime-default-agent");

        var readiness = await GetReadinessAsync(projectId, agent.Id);
        Assert.Equal("unknown", readiness.Conclusion);
        Assert.DoesNotContain("model-missing", readiness.Gaps);
        Assert.Empty(readiness.Gaps);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(
            projectId,
            agent.Id,
            new { prompt = "run with the runtime default" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UnsetModel_DispatchesWithoutAModelSoTheRuntimeChooses()
    {
        var projectId = await CreateProjectAsync("launch-runtime-default-dispatch");
        var agent = await CreateModellessAgentAsync(projectId, "runtime-default-dispatch-agent");
        var runnerId = $"launch-runtime-default-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                agent.Id,
                new { prompt = "run with the runtime default" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            // The accepted snapshot records the Runtime-chosen (null) model
            // rather than a substituted value.
            var frozen = await _fixture.Grains
                .GetGrain<Mohist.Server.Agent.Grains.IAgentJobGrain>(jobId)
                .GetRuntimeSnapshotAsync();
            Assert.Equal("pi", frozen.ExecutionDefinition?.Runtime);
            Assert.Null(frozen.ExecutionDefinition?.Model);

            var snapshot = await ClaimDispatchForSessionAsync(jobId, runnerId, sessionId);
            var dispatch = await PollDispatchEnvelopeForWorkAsync(runnerId, snapshot.WorkId!);
            Assert.Equal(AgentConfigSchema.DefaultRuntime, ReadRuntimeFromDispatch(dispatch));
            Assert.False(HasDispatchField(dispatch, "model"), "the dispatch must not carry a substituted model");
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

    private async Task<JsonElement> PollDispatchEnvelopeForWorkAsync(string runnerId, string workId)
    {
        for (var i = 0; i < 50; i++)
        {
            using var poll = await _fixture.Client.PostRunnerPollAsync(
                runnerId,
                ReadyPollRequest(runnerId));
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

    private static bool HasDispatchField(JsonElement dispatch, string field)
    {
        var withJson = dispatch.GetProperty("with").GetString();
        Assert.False(string.IsNullOrWhiteSpace(withJson));
        using var doc = JsonDocument.Parse(withJson!);
        return doc.RootElement.TryGetProperty(field, out _);
    }
}
