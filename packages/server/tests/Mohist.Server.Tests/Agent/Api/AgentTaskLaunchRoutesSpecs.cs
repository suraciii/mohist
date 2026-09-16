using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

[Collection("LaunchIntegration")]
[Trait("level", "L1")]
public sealed class AgentTaskLaunchRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentTaskLaunchRoutesSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
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
        Assert.NotEqual("not-configured", agentData.GetProperty("executability").GetProperty("state").GetString());

        using var replay = await PostTaskAsync(projectId, body, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayData = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        foreach (var field in new[] { "agentId", "agentName", "jobId", "sessionId", "inputId", "turnId", "workspaceId", "targetId", "origin", "status", "sessionUrl", "transcriptUrl", "jobUrl", "observationUrl" })
            Assert.True(
                string.Equals(firstData.GetProperty(field).GetString(), replayData.GetProperty(field).GetString(), StringComparison.Ordinal),
                field);

        using var changedHint = await PostTaskAsync(
            projectId,
            new
            {
                prompt = "Implement the task-first route",
                name = "task-route-agent",
                runtime = "pi",
                model = "provider/changed",
                variant = "balanced",
            },
            key);
        Assert.Equal(HttpStatusCode.Conflict, changedHint.StatusCode);
        var conflict = await changedHint.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", conflict.GetProperty("code").GetString());
        Assert.Equal(key, conflict.GetProperty("details").GetProperty("idempotencyKey").GetString());

        using var changedVariant = await PostTaskAsync(
            projectId,
            new
            {
                prompt = "Implement the task-first route",
                name = "task-route-agent",
                runtime = "pi",
                model = "provider/task",
                variant = "high",
            },
            key);
        Assert.Equal(HttpStatusCode.Conflict, changedVariant.StatusCode);
        var variantConflict = await changedVariant.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", variantConflict.GetProperty("code").GetString());
        Assert.Equal(key, variantConflict.GetProperty("details").GetProperty("idempotencyKey").GetString());

        using var agents = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents?all=true");
        agents.EnsureSuccessStatusCode();
        var agentEntries = (await agents.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Single(
            agentEntries.EnumerateArray(),
            entry => entry.GetProperty("origin").GetString() == AgentOrigins.Project);
    }

    [Fact]
    public async Task TaskLaunch_CarriesReasoningEffortIntoDefinitionAndSnapshot()
    {
        var projectId = await CreateProjectAsync("task-effort");
        const string key = "task-effort-key";

        using var response = await PostTaskAsync(
            projectId,
            new
            {
                prompt = "use the canonical effort",
                runtime = "pi",
                model = "provider/task",
                reasoningEffort = "xhigh",
            },
            key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        using var agent = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{data.GetProperty("agentId").GetString()}");
        agent.EnsureSuccessStatusCode();
        var config = (await agent.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("agentConfig");
        Assert.Equal("xhigh", config.GetProperty("reasoningEffort").GetString());

        // The accepted launch snapshot froze the effort beside model and
        // variant; nothing reinterprets it from a Variant.
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(data.GetProperty("jobId").GetString()!);
        var frozen = await job.GetRuntimeSnapshotAsync();
        Assert.Equal("xhigh", frozen.ExecutionDefinition?.ReasoningEffort);
        Assert.Equal("provider/task", frozen.ExecutionDefinition?.Model);
    }

    [Fact]
    public async Task TaskLaunch_ChangedReasoningEffortConflictsOnReplay()
    {
        var projectId = await CreateProjectAsync("task-effort-conflict");
        const string key = "task-effort-conflict-key";
        var body = new
        {
            prompt = "keep this effort",
            model = "provider/task",
            reasoningEffort = "high",
        };

        using var first = await PostTaskAsync(projectId, body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var changedEffort = await PostTaskAsync(
            projectId,
            new
            {
                prompt = "keep this effort",
                model = "provider/task",
                reasoningEffort = "max",
            },
            key);
        Assert.Equal(HttpStatusCode.Conflict, changedEffort.StatusCode);
        var conflict = await changedEffort.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("launch_idempotency_conflict", conflict.GetProperty("code").GetString());
        Assert.Equal(key, conflict.GetProperty("details").GetProperty("idempotencyKey").GetString());

        Assert.Equal(1, await AgentCountAsync(projectId));
    }

    [Fact]
    public async Task TaskLaunch_PreflightProjectsScopeAndLaunchRejectsScopeDrift()
    {
        var projectId = await CreateProjectAsync("task-preflight");

        const string key = "task-preflight-key";
        var body = new
        {
            prompt = "confirm the execution scope",
            context = new { repository = "main" },
            runtime = "pi",
            model = "provider/original",
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
        Assert.Equal("provider/original", preflightData.GetProperty("execution").GetProperty("model").GetString());
        Assert.Equal("project-workspace-write", preflightData.GetProperty("permissionScope").GetString());
        var fingerprint = preflightData.GetProperty("scopeFingerprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));

        using var launchRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks")
        {
            Content = JsonContent.Create(new
            {
                prompt = "confirm the execution scope",
                context = new { repository = "main" },
                runtime = "pi",
                model = "provider/changed",
            }),
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
    public async Task TaskLaunch_ReplaysAcceptedOutcomeBeforeCheckingStalePreflightScope()
    {
        var projectId = await CreateProjectAsync("task-preflight-replay");
        const string key = "task-preflight-replay-key";
        var body = new { prompt = "replay the confirmed task", model = "provider/original" };
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
        var fingerprint = (await preflight.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("scopeFingerprint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));

        using var firstRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks")
        {
            Content = JsonContent.Create(body),
        };
        firstRequest.Headers.Add("Idempotency-Key", key);
        firstRequest.Headers.Add("X-Mohist-Launch-Origin", "web");
        firstRequest.Headers.Add("X-Mohist-Agent-Preflight", fingerprint!);
        using var first = await _fixture.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        // A replay resumes the accepted outcome before the scope check, so a
        // stale preflight header cannot turn the retry into a rejection.
        using var replayRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-tasks")
        {
            Content = JsonContent.Create(body),
        };
        replayRequest.Headers.Add("Idempotency-Key", key);
        replayRequest.Headers.Add("X-Mohist-Launch-Origin", "web");
        replayRequest.Headers.Add("X-Mohist-Agent-Preflight", "stale-scope-fingerprint");
        using var replay = await _fixture.Client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayData = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(firstData.GetProperty("agentId").GetString(), replayData.GetProperty("agentId").GetString());
        Assert.Equal(firstData.GetProperty("sessionId").GetString(), replayData.GetProperty("sessionId").GetString());
        Assert.Equal(firstData.GetProperty("jobId").GetString(), replayData.GetProperty("jobId").GetString());
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

        using var malformedEffort = await PostTaskAsync(
            projectId,
            new { prompt = "task", model = "provider/task", reasoningEffort = "none" },
            "task-effort-invalid");
        Assert.Equal(HttpStatusCode.BadRequest, malformedEffort.StatusCode);
        var effortError = await malformedEffort.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", effortError.GetProperty("code").GetString());
        Assert.Contains("reasoningEffort", effortError.GetProperty("error").GetString(), StringComparison.Ordinal);
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
    public async Task TaskLaunch_NoExecutionHints_CreatesAgentWithUnsetExecutionAndResolvedSnapshot()
    {
        var projectId = await CreateProjectAsync("task-no-hints");
        const string key = "task-no-hints-key";

        using var response = await PostTaskAsync(projectId, new { prompt = "Run with Runtime defaults" }, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        using var agent = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agents/{data.GetProperty("agentId").GetString()}");
        agent.EnsureSuccessStatusCode();
        var agentPayload = (await agent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var config = agentPayload.GetProperty("agentConfig");
        Assert.False(config.TryGetProperty("runtime", out _), "the definition carries no unsupplied Runtime");
        Assert.False(config.TryGetProperty("model", out _), "the definition carries no unsupplied Model");
        Assert.False(config.TryGetProperty("variant", out _));

        var effective = agentPayload.GetProperty("effectiveExecutionConfig");
        Assert.Equal("pi", effective.GetProperty("runtime").GetString());
        Assert.False(effective.TryGetProperty("model", out var effectiveModel) && effectiveModel.ValueKind != JsonValueKind.Null);

        // The accepted launch records the Runtime the Server resolved, and a
        // null Model means the Runtime chooses at dispatch.
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(data.GetProperty("jobId").GetString()!);
        var frozen = await job.GetRuntimeSnapshotAsync();
        Assert.Equal("pi", frozen.ExecutionDefinition?.Runtime);
        Assert.Null(frozen.ExecutionDefinition?.Model);
    }

    [Fact]
    public async Task TaskLaunch_TerminalRejectionArchivesDefinitionAndReturnsHttpContract()
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
            var archived = Assert.Single(ProjectEntries(entries));
            Assert.Equal("archived", archived.GetProperty("status").GetString());
            var archivedName = archived.GetProperty("name").GetString();
            Assert.Equal("Terminal rejection task", archivedName);

            using var namedRetry = await PostTaskAsync(
                projectId,
                new { prompt = "retry the task", name = archivedName, model = "provider/task" },
                "task-terminal-rejection-name-retry");
            Assert.Equal(HttpStatusCode.Conflict, namedRetry.StatusCode);
            Assert.Equal(
                "AGENT_NAME_CONFLICT",
                (await namedRetry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
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
            Assert.Equal("archived", Assert.Single(ProjectEntries(entries)).GetProperty("status").GetString());

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

            var pendingAgent = Assert.Single(ProjectEntries(await AgentEntriesAsync(projectId)));
            Assert.Equal("active", pendingAgent.GetProperty("status").GetString());

            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
            using var recovered = await PostTaskAsync(projectId, body, key);
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
            var recoveredAgent = Assert.Single(ProjectEntries(await AgentEntriesAsync(projectId)));
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
    public async Task TaskLaunch_AcceptsNumericEpicContext()
    {
        var projectId = await CreateProjectAsync("task-epic-context");
        var epicNumber = await CreateEpicAsync(projectId, "Task context epic");

        using var response = await PostTaskAsync(
            projectId,
            new
            {
                prompt = "launch in an epic context",
                model = "provider/task",
                context = new { epicNumber },
            },
            "task-epic-context-key");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static IEnumerable<JsonElement> ProjectEntries(JsonElement entries) =>
        entries.EnumerateArray()
            .Where(entry => entry.GetProperty("origin").GetString() == AgentOrigins.Project);

    private async Task<int> AgentCountAsync(string projectId)
    {
        // The list read merges unshadowed built-in Workflow Agents; these
        // assertions count only the Project's stored definitions.
        return (await AgentEntriesAsync(projectId)).EnumerateArray()
            .Count(entry => entry.GetProperty("origin").GetString() == AgentOrigins.Project);
    }

    private async Task<JsonElement> AgentEntriesAsync(string projectId)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents?all=true");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").Clone();
    }
}
