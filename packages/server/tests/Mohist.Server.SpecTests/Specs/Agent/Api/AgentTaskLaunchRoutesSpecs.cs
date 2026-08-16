using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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
        Assert.Equal("pi", agentData.GetProperty("effectiveExecutionConfig").GetProperty("runtime").GetString());
        Assert.Equal("provider/task", agentData.GetProperty("effectiveExecutionConfig").GetProperty("model").GetString());
        Assert.Equal("balanced", agentData.GetProperty("effectiveExecutionConfig").GetProperty("variant").GetString());
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

        using var listed = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents");
        listed.EnsureSuccessStatusCode();
        var listedAgent = (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray().Single();
        Assert.Equal("pi", listedAgent.GetProperty("effectiveExecutionConfig").GetProperty("runtime").GetString());
        Assert.Equal("provider/default", listedAgent.GetProperty("effectiveExecutionConfig").GetProperty("model").GetString());
    }

    [Fact]
    public async Task TaskLaunch_PreflightProjectsScopeAndLaunchRejectsScopeDrift()
    {
        var projectId = await CreateProjectAsync("task-preflight");
        using var configured = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi", model = "provider/default", variant = "balanced" });
        configured.EnsureSuccessStatusCode();

        const string key = "task-preflight-key";
        var body = new
        {
            prompt = "confirm the execution scope",
            context = new { repository = "main" },
        };
        using var preflightRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks/preflight")
        {
            Content = JsonContent.Create(body),
        };
        preflightRequest.Headers.Add("Idempotency-Key", key);
        preflightRequest.Headers.Add("X-Mohist-Launch-Origin", "web");
        using var preflight = await _fixture.Client.SendAsync(preflightRequest);
        Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
        var preflightData = (await preflight.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("pi", preflightData.GetProperty("execution").GetProperty("runtime").GetString());
        Assert.Equal("provider/default", preflightData.GetProperty("execution").GetProperty("model").GetString());
        Assert.Equal("project-workspace-write", preflightData.GetProperty("permissionScope").GetString());
        var fingerprint = preflightData.GetProperty("scopeFingerprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));

        using var changedDefault = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi", model = "provider/changed", variant = "balanced" });
        changedDefault.EnsureSuccessStatusCode();

        using var launchRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks")
        {
            Content = JsonContent.Create(body),
        };
        launchRequest.Headers.Add("Idempotency-Key", key);
        launchRequest.Headers.Add("X-Mohist-Launch-Origin", "web");
        launchRequest.Headers.Add("X-Mohist-Agent-Preflight", fingerprint!);
        using var rejected = await _fixture.Client.SendAsync(launchRequest);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("launch_scope_changed", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await AgentCountAsync(projectId));
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
    public async Task TaskLaunch_AllRejectedAttachmentsReturnsVerdictsAndCreatesNothing()
    {
        var projectId = await CreateProjectAsync("task-input-unusable");
        var before = await AgentCountAsync(projectId);

        using var response = await PostTaskAsync(
            projectId,
            new { attachments = new[] { "att_missing-for-task" } },
            "task-input-unusable");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("input_unusable", payload.GetProperty("code").GetString());
        var verdict = Assert.Single(payload.GetProperty("details").GetProperty("attachments").EnumerateArray());
        Assert.False(verdict.GetProperty("accepted").GetBoolean());
        Assert.Equal("NotFound", verdict.GetProperty("reason").GetString());
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
    public async Task TaskLaunch_TerminalRejectionArchivesDefinitionAndReplaysRecordedRejection()
    {
        var projectId = await CreateProjectAsync("task-terminal-rejection");
        const string key = "task-terminal-rejection-key";
        var body = new { prompt = "Terminal rejection task", model = "provider/task" };

        try
        {
            _fixture.LaunchFaults.RejectNext(
                LaunchParticipantGate.EnsureInitialLaunch,
                "simulated_terminal_rejection");

            using var first = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
            var firstPayload = await first.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("launch_rejected", firstPayload.GetProperty("code").GetString());
            Assert.Equal(
                "simulated_terminal_rejection",
                firstPayload.GetProperty("details").GetProperty("reason").GetString());

            var entries = await AgentEntriesAsync(projectId);
            var archived = Assert.Single(entries.EnumerateArray());
            Assert.Equal("archived", archived.GetProperty("status").GetString());
            var archivedName = archived.GetProperty("name").GetString();
            Assert.Equal("Terminal rejection task", archivedName);

            using var replay = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            var replayPayload = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("launch_rejected", replayPayload.GetProperty("code").GetString());
            Assert.Equal(
                "simulated_terminal_rejection",
                replayPayload.GetProperty("details").GetProperty("reason").GetString());
            Assert.Single((await AgentEntriesAsync(projectId)).EnumerateArray());

            using var namedRetry = await PostTaskAsync(
                projectId,
                new { prompt = "retry the task", name = archivedName, model = "provider/task" },
                "task-terminal-rejection-name-retry");
            Assert.Equal(HttpStatusCode.Conflict, namedRetry.StatusCode);
            Assert.Equal(
                "AGENT_NAME_CONFLICT",
                (await namedRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            using var derivedRetry = await PostTaskAsync(
                projectId,
                body,
                "task-terminal-rejection-derived-retry");
            Assert.Equal(HttpStatusCode.Created, derivedRetry.StatusCode);
            var derivedData = (await derivedRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("Terminal rejection task 2", derivedData.GetProperty("agentName").GetString());
        }
        finally
        {
            _fixture.LaunchFaults.StopRejecting(LaunchParticipantGate.EnsureInitialLaunch);
        }
    }

    [Fact]
    public async Task TaskLaunch_ArchiveFailureIsRepairedByReminderBeforeReplayedRejection()
    {
        var projectId = await CreateProjectAsync("task-archive-recovery");
        const string key = "task-archive-recovery-key";
        var body = new { prompt = "Archive recovery task", model = "provider/task" };

        try
        {
            _fixture.LaunchFaults.RejectNext(
                LaunchParticipantGate.EnsureInitialLaunch,
                "simulated_terminal_rejection");
            _fixture.LaunchFaults.FailNext(LaunchParticipantGate.ArchiveDefinition);

            using var crashed = await PostTaskAsync(projectId, body, key);
            Assert.True(
                (int)crashed.StatusCode >= 500,
                $"unexpected status {crashed.StatusCode}; archive probes={_fixture.LaunchFaults.CommandIds(LaunchParticipantGate.ArchiveDefinition).Count}");

            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.ArchiveDefinition);
            var coordinator = _fixture.Grains.GetGrain<IAgentLaunchCoordinatorGrain>(
                AgentLaunchCoordinatorCodec.KeyFor(projectId, key));
            await coordinator.ReceiveReminder(AgentLaunchCoordinatorGrain.RecoveryReminderName, default);
            Assert.True(
                _fixture.LaunchFaults.CommandIds(LaunchParticipantGate.ArchiveDefinition).Count >= 2,
                "the reminder must retry the archive participant after the injected failure");

            var entries = await AgentEntriesAsync(projectId);
            Assert.Equal("archived", Assert.Single(entries.EnumerateArray()).GetProperty("status").GetString());

            using var replay = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal(
                "launch_rejected",
                (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally
        {
            _fixture.LaunchFaults.StopRejecting(LaunchParticipantGate.EnsureInitialLaunch);
            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.ArchiveDefinition);
        }
    }

    [Fact]
    public async Task TaskLaunch_SetupPendingDoesNotArchiveCreatedDefinition()
    {
        var projectId = await CreateProjectAsync("task-setup-pending");
        const string key = "task-setup-pending-key";
        var body = new { prompt = "Setup pending task", model = "provider/task" };

        try
        {
            _fixture.LaunchFaults.FailNext(LaunchParticipantGate.EnsureInitialLaunch);
            using var pending = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
            Assert.Equal(
                "launch_setup_pending",
                (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            var pendingAgent = Assert.Single((await AgentEntriesAsync(projectId)).EnumerateArray());
            Assert.Equal("active", pendingAgent.GetProperty("status").GetString());

            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
            using var recovered = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
            var recoveredAgent = Assert.Single((await AgentEntriesAsync(projectId)).EnumerateArray());
            Assert.Equal("active", recoveredAgent.GetProperty("status").GetString());
        }
        finally
        {
            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
        }
    }

    [Fact]
    public async Task DefinitionFirst_TerminalRejectionDoesNotArchiveExistingDefinition()
    {
        var projectId = await CreateProjectAsync("definition-first-rejection");
        var agent = await CreateAgentAsync(projectId, "definition-first-agent");
        const string key = "definition-first-rejection-key";

        try
        {
            _fixture.LaunchFaults.RejectNext(
                LaunchParticipantGate.EnsureInitialLaunch,
                "simulated_terminal_rejection");

            using var rejected = await LaunchAsync(
                projectId,
                agent.Id,
                new { prompt = "definition-first rejection" },
                key);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal(
                "launch_rejected",
                (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            using var shown = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}");
            shown.EnsureSuccessStatusCode();
            Assert.Equal(
                "active",
                (await shown.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            _fixture.LaunchFaults.StopRejecting(LaunchParticipantGate.EnsureInitialLaunch);
        }
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
        return (await AgentEntriesAsync(projectId)).GetArrayLength();
    }

    private async Task<JsonElement> AgentEntriesAsync(string projectId)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents?all=true");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").Clone();
    }
}
