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
using Mohist.Server.Tests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Sessions;

[Collection("MohistIntegration")]
public class GenericAgentSessionTranscriptAxisSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"generic-transcript-{Guid.NewGuid():N}";

    public GenericAgentSessionTranscriptAxisSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response;
        }
        catch
        {
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericLaunch_PolledDispatch_CarriesMintedAgentSessionIdAndNoWorkflowRunId()
    {
        var project = await CreateProjectAsync("transcript-axis-launch");
        var agent = await CreateAgentAsync(project.Id, "transcript-axis-agent");

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{_runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 2 });

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/agents/{agent.Id}/sessions",
                new { prompt = "transcript-axis launch" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var polledWork = await PollOnceAsync(_runnerId, sessionId);

            Assert.False(string.IsNullOrWhiteSpace(polledWork.WorkId));
            Assert.Equal(string.Empty, polledWork.WorkflowRunId);
            Assert.Equal(sessionId, polledWork.AgentSessionId);
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polledWork.OwnerKind);
            Assert.Equal(project.Id, polledWork.ProjectId);
            Assert.False(string.IsNullOrWhiteSpace(polledWork.AgentJobId));
        }
        finally
        {
            await DrainRemainingDispatchAsync(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericLaunch_RuntimeEvents_DeliveredToSessionId_PersistNonEmptyTranscriptTurn()
    {
        var project = await CreateProjectAsync("transcript-axis-transcript");
        var agent = await CreateAgentAsync(project.Id, "transcript-axis-events-agent");

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{_runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 2 });

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/agents/{agent.Id}/sessions",
                new { prompt = "transcript-axis events" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            await DrainRemainingDispatchAsync(_runnerId, sessionId);

            await OpenGenericSessionAsync(project.Id, sessionId);

            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeEvents = new object[]
                    {
                        new { type = "session.input", payload = new { text = "transcript-axis events", kind = "task" } },
                        new { type = "message.delta", payload = new { content = new { text = "Hello transcript axis." } } },
                        new
                        {
                            type = "tool_call.started",
                            payload = new
                            {
                                toolCallId = "tx-tool-1",
                                toolName = "Read",
                                kind = "read",
                                status = "in_progress",
                                title = "Read README",
                                rawInput = new { filePath = "README.md" }
                            }
                        },
                        new
                        {
                            type = "tool_call.completed",
                            payload = new
                            {
                                toolCallId = "tx-tool-1",
                                toolName = "Read",
                                kind = "read",
                                status = "completed",
                                title = "Read README",
                                rawInput = new { filePath = "README.md" },
                                rawOutput = new { text = "README contents" }
                            }
                        },
                        new
                        {
                            type = "usage.updated",
                            payload = new
                            {
                                inputTokens = 220,
                                outputTokens = 80,
                                totalTokens = 300,
                                contextWindowSize = 200000,
                                contextWindowUsed = 300,
                                costAmount = 0.0011,
                                costCurrency = "USD"
                            }
                        }
                    }
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 4, _fixture.Grains);

            using var transcriptResponse = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, transcriptResponse.StatusCode);
            var transcriptPayload = await transcriptResponse.Content.ReadFromJsonAsync<JsonElement>();
            var transcriptData = transcriptPayload.GetProperty("data");

            Assert.True(transcriptData.GetProperty("turns").GetArrayLength() >= 1);
            Assert.True(transcriptData.GetProperty("partCount").GetInt32() >= 4);

            var firstTurn = transcriptData.GetProperty("turns")[0];
            Assert.Equal("transcript-axis events", firstTurn.GetProperty("user").GetProperty("text").GetString());
            var assistant = firstTurn.GetProperty("assistant");
            Assert.True(assistant.GetArrayLength() >= 2);

            bool sawMessage = false;
            bool sawTool = false;
            foreach (var part in assistant.EnumerateArray())
            {
                var type = part.GetProperty("type").GetString();
                if (type == "text") sawMessage = true;
                if (type == "tool") sawTool = true;
            }
            Assert.True(sawMessage, "expected at least one text assistant part on the generic turn");
            Assert.True(sawTool, "expected at least one tool assistant part on the generic turn");

            using var summaryResponse = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
            var summaryPayload = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
            var summaryData = summaryPayload.GetProperty("data");
            Assert.Equal(sessionId, summaryData.GetProperty("sessionId").GetString());
        }
        finally
        {
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericLaunch_FollowUpRuntimeEvents_AppendNonEmptyTranscriptContent()
    {
        var project = await CreateProjectAsync("transcript-axis-followup");
        var agent = await CreateAgentAsync(project.Id, "transcript-axis-followup-agent");

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{_runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 2 });

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/agents/{agent.Id}/sessions",
                new { prompt = "transcript-axis first turn" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            await DrainRemainingDispatchAsync(_runnerId, sessionId);
            await OpenGenericSessionAsync(project.Id, sessionId);

            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeEvents = new object[]
                    {
                        new { type = "session.input", payload = new { text = "transcript-axis first turn", kind = "task" } },
                        new { type = "message.delta", payload = new { content = new { text = "first reply" } } }
                    }
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 1, _fixture.Grains);

            using var firstRead = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
            var firstPayload = await firstRead.Content.ReadFromJsonAsync<JsonElement>();
            var firstData = firstPayload.GetProperty("data");
            Assert.True(firstData.GetProperty("turns").GetArrayLength() >= 1);
            var firstTurn = firstData.GetProperty("turns")[0];
            Assert.Equal("transcript-axis first turn", firstTurn.GetProperty("user").GetProperty("text").GetString());
            Assert.True(firstTurn.GetProperty("assistant").GetArrayLength() >= 1);

            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeEvents = new object[]
                    {
                        new { type = "session.input", payload = new { text = "transcript-axis follow-up", kind = "task" } },
                        new { type = "message.delta", payload = new { content = new { text = "follow-up reply" } } },
                        new
                        {
                            type = "tool_call.started",
                            payload = new
                            {
                                toolCallId = "fu-tool-1",
                                toolName = "Bash",
                                kind = "execute",
                                status = "in_progress",
                                title = "Run command",
                                rawInput = new { command = "ls" }
                            }
                        },
                        new
                        {
                            type = "tool_call.completed",
                            payload = new
                            {
                                toolCallId = "fu-tool-1",
                                toolName = "Bash",
                                kind = "execute",
                                status = "completed",
                                title = "Run command",
                                rawInput = new { command = "ls" },
                                rawOutput = new { stdout = "ok" }
                            }
                        }
                    }
                });

            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 2, _fixture.Grains);

            using var secondRead = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
            var secondPayload = await secondRead.Content.ReadFromJsonAsync<JsonElement>();
            var secondData = secondPayload.GetProperty("data");
            Assert.True(secondData.GetProperty("turns").GetArrayLength() >= 1);
            Assert.True(secondData.GetProperty("partCount").GetInt32() >= 2);

            bool sawText = false;
            bool sawTool = false;
            foreach (var turn in secondData.GetProperty("turns").EnumerateArray())
            {
                foreach (var part in turn.GetProperty("assistant").EnumerateArray())
                {
                    var type = part.GetProperty("type").GetString();
                    if (type == "text") sawText = true;
                    if (type == "tool") sawTool = true;
                }
            }
            Assert.True(sawText, "expected at least one text assistant part across follow-up events");
            Assert.True(sawTool, "expected at least one tool assistant part across follow-up events");
        }
        finally
        {
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericTranscript_IsReachable_SolelyBySessionId_WithoutWorkflowRunIdLookup()
    {
        var project = await CreateProjectAsync("transcript-axis-session-id-only");
        var agent = await CreateAgentAsync(project.Id, "transcript-axis-session-id-only-agent");

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{_runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{_runnerId}", new { slots = 2 });

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/agents/{agent.Id}/sessions",
                new { prompt = "session-id only" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeEvents = new object[]
                    {
                        new { type = "session.input", payload = new { text = "session-id only", kind = "task" } },
                        new { type = "message.delta", payload = new { content = new { text = "hello" } } }
                    }
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 1, _fixture.Grains);

            using var transcript = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, transcript.StatusCode);
            var transcriptPayload = await transcript.Content.ReadFromJsonAsync<JsonElement>();
            var transcriptData = transcriptPayload.GetProperty("data");
            Assert.True(transcriptData.GetProperty("turns").GetArrayLength() >= 1);

            using var issueSessionProbe = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/issues/0/sessions/nonexistent/transcript");
            Assert.NotEqual(HttpStatusCode.OK, issueSessionProbe.StatusCode);

            using var projectSessionList = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, projectSessionList.StatusCode);
        }
        finally
        {
        }
    }

    private async Task OpenGenericSessionAsync(string projectId, string sessionId)
    {
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "agent-job",
                stage = "agent",
                title = "transcript axis session",
            });
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/attach",
            new
            {
                agentSessionId = sessionId,
                workDir = projectId,
                processPid = 4321,
            });
    }

    private async Task<PollResult> PollOnceAsync(string runnerId, string expectedSessionId)
    {
        var attempts = 50;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            var raw = await poll.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = JsonDocument.Parse(raw);
            var data = doc.RootElement;
            var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                && agentSessionIdElement.ValueKind != JsonValueKind.Null
                ? agentSessionIdElement.GetString()
                : null;
            if (polledSessionId == expectedSessionId)
            {
                var workId = data.GetProperty("workId").GetString() ?? string.Empty;
                var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement) && agentJobIdElement.ValueKind != JsonValueKind.Null
                    ? agentJobIdElement.GetString()
                    : null;
                var projectId = data.TryGetProperty("projectId", out var projectIdElement) && projectIdElement.ValueKind != JsonValueKind.Null
                    ? projectIdElement.GetString()
                    : null;
                var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement) && ownerKindElement.ValueKind != JsonValueKind.Null
                    ? ownerKindElement.GetString()
                    : null;
                return new PollResult(
                    WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                    WorkId: workId,
                    AgentJobId: agentJobId,
                    ProjectId: projectId,
                    AgentSessionId: polledSessionId,
                    OwnerKind: ownerKind);
            }

            await DrainDispatchAsync(runnerId, raw);
        }

        throw new InvalidOperationException($"No polled dispatch carrying AgentSessionId='{expectedSessionId}' after {attempts} attempts");
    }

    private async Task DrainRemainingDispatchAsync(string runnerId, string? expectedSessionId = null)
    {
        var attempts = 30;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            if (poll.StatusCode != HttpStatusCode.OK) return;
            var raw = await poll.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var doc = JsonDocument.Parse(raw);
            var data = doc.RootElement;

            var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                && agentSessionIdElement.ValueKind != JsonValueKind.Null
                ? agentSessionIdElement.GetString()
                : null;
            if (expectedSessionId is not null && polledSessionId != expectedSessionId)
                return;

            await DrainDispatchAsync(runnerId, raw);
        }
    }

    private async Task DrainDispatchAsync(string runnerId, string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement;
        var workId = data.GetProperty("workId").GetString();
        var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement) && ownerKindElement.ValueKind != JsonValueKind.Null
            ? ownerKindElement.GetString()
            : null;

        if (!string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            return;

        var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement) && agentJobIdElement.ValueKind != JsonValueKind.Null
            ? agentJobIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(agentJobId) || string.IsNullOrWhiteSpace(workId))
            return;

        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            workId!,
            new Mohist.Server.Runner.Grains.WorkResult(
                Status: "completed",
                Message: "ok",
                Output: "{}",
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected drain report");
    }

    private async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"generic-transcript-{Guid.NewGuid():N}";
        if (projectName.Length > 63) projectName = projectName[..63];
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = projectName,
            path = Directory.GetCurrentDirectory(),
            baseBranch = "main",
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
        });
        return new ProjectRef(project.Id, project.Path);
    }

    private async Task<AgentRef> CreateAgentAsync(string projectId, string agentName)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = agentName,
                description = $"description for {agentName}",
                instructions = $"instructions for {agentName}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, agentName);
    }

    private sealed record PollResult(
        string WorkflowRunId,
        string WorkId,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind);

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record ProjectRef(string Id, string Path);
    private sealed record AgentRef(string Id, string Name);
}
