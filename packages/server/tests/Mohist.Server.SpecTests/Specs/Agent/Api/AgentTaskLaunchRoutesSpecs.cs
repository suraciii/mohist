using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public sealed class AgentTaskLaunchRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentTaskLaunchRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task TaskLaunch_CreatesDefinitionAndCanonicalLaunch_ReplaysIdentities()
    {
        var projectId = await CreateProjectAsync("task-launch");
        const string key = "task-launch-replay";
        var body = new
        {
            prompt = "Implement the task-first route",
            name = "task-route-agent",
            runtime = "pi",
            model = "provider/task",
            variant = "balanced",
        };

        using var first = await PostTaskAsync(projectId, body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        foreach (var field in new[] { "agentId", "agentName", "jobId", "sessionId", "inputId", "turnId", "workspaceId", "sessionUrl", "transcriptUrl", "jobUrl", "observationUrl" })
            Assert.False(string.IsNullOrWhiteSpace(firstData.GetProperty(field).GetString()), field);
        Assert.Equal("task-route-agent", firstData.GetProperty("agentName").GetString());
        Assert.True(firstData.GetProperty("sessionUrl").GetString()!.Contains(
            $"/sessions/{firstData.GetProperty("sessionId").GetString()}",
            StringComparison.Ordinal));

        using var agent = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{firstData.GetProperty("agentId").GetString()}");
        agent.EnsureSuccessStatusCode();
        var agentData = (await agent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("pi", agentData.GetProperty("agentConfig").GetProperty("runtime").GetString());
        Assert.Equal("provider/task", agentData.GetProperty("agentConfig").GetProperty("model").GetString());
        Assert.Equal("balanced", agentData.GetProperty("agentConfig").GetProperty("variant").GetString());
        Assert.False(string.IsNullOrWhiteSpace(agentData.GetProperty("instructions").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(agentData.GetProperty("description").GetString()));
        Assert.NotEqual("Needs setup", agentData.GetProperty("readiness").GetProperty("conclusion").GetString());

        using var replay = await PostTaskAsync(projectId, body, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayData = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        foreach (var field in new[] { "agentId", "agentName", "jobId", "sessionId", "inputId", "turnId", "workspaceId", "targetId", "origin", "status", "sessionUrl", "transcriptUrl", "jobUrl", "observationUrl" })
            Assert.True(
                string.Equals(firstData.GetProperty(field).GetString(), replayData.GetProperty(field).GetString(), StringComparison.Ordinal),
                field);

        using var agents = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents?all=true");
        agents.EnsureSuccessStatusCode();
        var agentEntries = (await agents.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Single(agentEntries.EnumerateArray());
    }

    [Fact]
    public async Task TaskLaunch_UsesProjectDefaultWhenHintsAreOmitted()
    {
        var projectId = await CreateProjectAsync("task-default");
        using var configured = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi", model = "provider/default", variant = "balanced" });
        configured.EnsureSuccessStatusCode();

        using var response = await PostTaskAsync(
            projectId,
            new { prompt = "use the project default" },
            "task-default-key");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        using var agent = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{data.GetProperty("agentId").GetString()}");
        agent.EnsureSuccessStatusCode();
        var config = (await agent.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("agentConfig");
        Assert.Equal("pi", config.GetProperty("runtime").GetString());
        Assert.Equal("provider/default", config.GetProperty("model").GetString());
        Assert.Equal("balanced", config.GetProperty("variant").GetString());
    }

    [Fact]
    public async Task TaskLaunch_RejectsClosedFieldsAndMalformedHintsBeforeCreatingAgent()
    {
        var projectId = await CreateProjectAsync("task-validation");
        var before = await AgentCountAsync(projectId);

        using var unsupported = await PostTaskAsync(projectId, new { prompt = "task", model = "provider/task", instructions = "no" }, "task-unsupported");
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal("unsupported_field", (await unsupported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, await AgentCountAsync(projectId));

        using var malformedRuntime = await PostTaskAsync(projectId, new { prompt = "task", runtime = "fast", model = "provider/task" }, "task-runtime");
        Assert.Equal(HttpStatusCode.BadRequest, malformedRuntime.StatusCode);
        Assert.Contains("runtime", (await malformedRuntime.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        using var malformedModel = await PostTaskAsync(projectId, new { prompt = "task", model = "gpt" }, "task-model");
        Assert.Equal(HttpStatusCode.BadRequest, malformedModel.StatusCode);
        Assert.Contains("model", (await malformedModel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await AgentCountAsync(projectId));
    }

    [Fact]
    public async Task TaskLaunch_RejectsDeterminableFailuresBeforeCreate()
    {
        var projectId = await CreateProjectAsync("task-rejections");
        var before = await AgentCountAsync(projectId);

        using var noKey = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agent-tasks",
            new { prompt = "task", model = "provider/task" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("idempotency_key_required", (await noKey.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var noInput = await PostTaskAsync(projectId, new { model = "provider/task" }, "task-input");
        Assert.Equal(HttpStatusCode.BadRequest, noInput.StatusCode);
        Assert.Equal("input_required", (await noInput.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var noConfig = await PostTaskAsync(projectId, new { prompt = "task" }, "task-config");
        Assert.Equal(HttpStatusCode.Conflict, noConfig.StatusCode);
        var noConfigPayload = await noConfig.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("execution_config_unresolvable", noConfigPayload.GetProperty("code").GetString());
        Assert.Equal(2, noConfigPayload.GetProperty("details").GetProperty("repairs").GetArrayLength());

        var existing = await CreateAgentAsync(projectId, "already-used");
        using var nameConflict = await PostTaskAsync(
            projectId,
            new { prompt = "task", name = existing.Name, model = "provider/task" },
            "task-name-conflict");
        Assert.Equal(HttpStatusCode.Conflict, nameConflict.StatusCode);
        Assert.Equal("AGENT_NAME_CONFLICT", (await nameConflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(before + 1, await AgentCountAsync(projectId));
    }

    [Fact]
    public async Task TaskLaunch_ReplayWithChangedExecutionHintConflicts()
    {
        var projectId = await CreateProjectAsync("task-fingerprint");
        const string key = "task-fingerprint-key";
        using var first = await PostTaskAsync(projectId, new { prompt = "same task", model = "provider/one" }, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var changedModel = await PostTaskAsync(projectId, new { prompt = "same task", model = "provider/two" }, key);
        Assert.Equal(HttpStatusCode.Conflict, changedModel.StatusCode);
        Assert.Equal("launch_idempotency_conflict", (await changedModel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var addedVariant = await PostTaskAsync(projectId, new { prompt = "same task", model = "provider/one", variant = "high" }, key);
        Assert.Equal(HttpStatusCode.Conflict, addedVariant.StatusCode);
    }

    [Fact]
    public async Task TaskLaunch_UnknownContextMatchesDefinitionFirstNotFoundBoundary()
    {
        var projectId = await CreateProjectAsync("task-context");
        var before = await AgentCountAsync(projectId);

        using var response = await PostTaskAsync(
            projectId,
            new { prompt = "task", model = "provider/task", context = new { issueNumber = 999999 } },
            "task-context-unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, await AgentCountAsync(projectId));
    }

    private async Task<HttpResponseMessage> PostTaskAsync(string projectId, object body, string key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<int> AgentCountAsync(string projectId)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents?all=true");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetArrayLength();
    }
}
