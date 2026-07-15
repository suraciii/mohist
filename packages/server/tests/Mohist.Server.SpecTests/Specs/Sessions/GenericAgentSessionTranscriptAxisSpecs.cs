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

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
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
    public async Task GenericLaunch_PolledDispatch_FakeAgentRunThroughRuntimeEventsEndpoint_PersistsNonEmptyTranscriptTurn()
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

            var polledWork = await PollOnceAsync(_runnerId, sessionId);

            var fakeRun = await RunFakeAcpAgentThroughRuntimeEventsEndpointAsync(
                project.Id,
                sessionId,
                polledWork,
                new object[]
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
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            Assert.Equal(sessionId, fakeRun.SessionId);
            Assert.Equal(polledWork.WorkId, fakeRun.WorkId);
            Assert.Contains(RuntimeEventTypes.MessageDelta, fakeRun.EventTypes);
            Assert.Contains(RuntimeEventTypes.ToolCallStarted, fakeRun.EventTypes);
            Assert.Contains(RuntimeEventTypes.ToolCallCompleted, fakeRun.EventTypes);
            Assert.Contains(RuntimeEventTypes.UsageUpdated, fakeRun.EventTypes);

            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 4, _fixture.Grains);

            using var transcriptResponse = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, transcriptResponse.StatusCode);
            var transcriptPayload = await transcriptResponse.Content.ReadFromJsonAsync<JsonElement>();
            var transcriptData = transcriptPayload.GetProperty("data");

            Assert.True(transcriptData.GetProperty("turns").GetArrayLength() >= 1);
            Assert.True(transcriptData.GetProperty("partCount").GetInt32() >= 4);

            var turn = FindTurnByUserText(transcriptData, "transcript-axis events");
            AssertAssistantText(turn, "Hello transcript axis.");
            AssertAssistantTool(turn, "tx-tool-1");

            var usagePayload = Assert.Single(await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.Usage));
            Assert.Equal(220, usagePayload.GetProperty("inputTokens").GetInt64());
            Assert.Equal(80, usagePayload.GetProperty("outputTokens").GetInt64());
            Assert.Equal(300, usagePayload.GetProperty("totalTokens").GetInt64());
            Assert.Equal(200000, usagePayload.GetProperty("contextWindowSize").GetInt64());
            Assert.Equal(300, usagePayload.GetProperty("contextWindowUsed").GetInt64());
            Assert.Equal(0.0011, usagePayload.GetProperty("costAmount").GetDouble(), precision: 4);
            Assert.Equal("USD", usagePayload.GetProperty("costCurrency").GetString());

            var closePayload = Assert.Single(await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.SessionClosed));
            Assert.Equal("completed", closePayload.GetProperty("status").GetString());

            using var summaryResponse = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
            var summaryPayload = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
            var summaryData = summaryPayload.GetProperty("data");
            Assert.Equal(sessionId, summaryData.GetProperty("sessionId").GetString());
            Assert.Equal("completed", summaryData.GetProperty("status").GetString());
            Assert.Equal(220, summaryData.GetProperty("usage").GetProperty("inputTokens").GetInt64());
            Assert.Equal(80, summaryData.GetProperty("usage").GetProperty("outputTokens").GetInt64());
            Assert.Equal(300, summaryData.GetProperty("usage").GetProperty("totalTokens").GetInt64());
            Assert.Equal(200000, summaryData.GetProperty("usage").GetProperty("contextWindowSize").GetInt64());
            Assert.Equal(300, summaryData.GetProperty("usage").GetProperty("contextWindowUsed").GetInt64());
            Assert.Equal(0.0011, summaryData.GetProperty("usage").GetProperty("costAmount").GetDouble(), precision: 4);
            Assert.Equal("USD", summaryData.GetProperty("usage").GetProperty("costCurrency").GetString());
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

            var polledWork = await PollOnceAsync(_runnerId, sessionId);
            await RunFakeAcpAgentThroughRuntimeEventsEndpointAsync(
                project.Id,
                sessionId,
                polledWork,
                new object[]
                {
                    new { type = "session.input", payload = new { text = "transcript-axis first turn", kind = "task" } },
                    new { type = "message.delta", payload = new { content = new { text = "first reply" } } }
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 2, _fixture.Grains);

            using var firstRead = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
            var firstPayload = await firstRead.Content.ReadFromJsonAsync<JsonElement>();
            var firstData = firstPayload.GetProperty("data");
            Assert.True(firstData.GetProperty("turns").GetArrayLength() >= 1);
            var firstTurn = FindTurnByUserText(firstData, "transcript-axis first turn");
            AssertAssistantText(firstTurn, "first reply");

            using var completedRead = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, completedRead.StatusCode);
            var completedPayload = await completedRead.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("completed", completedPayload.GetProperty("data").GetProperty("status").GetString());

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
                        },
                        new
                        {
                            type = "usage.updated",
                            payload = new
                            {
                                inputTokens = 30,
                                outputTokens = 12,
                                totalTokens = 42,
                                contextWindowSize = 200000,
                                contextWindowUsed = 512,
                                costAmount = 0.0002,
                                costCurrency = "USD"
                            }
                        }
                    }
                });

            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 5, _fixture.Grains);

            using var secondRead = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript");
            Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
            var secondPayload = await secondRead.Content.ReadFromJsonAsync<JsonElement>();
            var secondData = secondPayload.GetProperty("data");
            Assert.True(secondData.GetProperty("turns").GetArrayLength() >= 2);
            Assert.True(secondData.GetProperty("partCount").GetInt32() >= 5);

            var initialTurn = FindTurnByUserText(secondData, "transcript-axis first turn");
            AssertAssistantText(initialTurn, "first reply");
            var followUpTurn = FindTurnByUserText(secondData, "transcript-axis follow-up");
            AssertAssistantText(followUpTurn, "follow-up reply");
            AssertAssistantTool(followUpTurn, "fu-tool-1");

            var usageParts = await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.Usage);
            Assert.Contains(usageParts, payload => payload.GetProperty("totalTokens").GetInt64() == 42);
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

    private async Task OpenGenericSessionAsync(string projectId, string sessionId, PollResult polledWork)
    {
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/open",
            new
            {
                workId = polledWork.WorkId,
                workType = polledWork.WorkType,
                stage = polledWork.Stage,
                title = "Agent Job",
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

    private async Task<FakeAgentRunResult> RunFakeAcpAgentThroughRuntimeEventsEndpointAsync(
        string projectId,
        string sessionId,
        PollResult polledWork,
        object[] runtimeEvents)
    {
        Assert.Equal(sessionId, polledWork.AgentSessionId);
        Assert.Equal(projectId, polledWork.ProjectId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polledWork.OwnerKind);
        Assert.Equal(string.Empty, polledWork.WorkflowRunId);
        Assert.False(string.IsNullOrWhiteSpace(polledWork.WorkId));
        Assert.False(string.IsNullOrWhiteSpace(polledWork.WorkType));
        Assert.False(string.IsNullOrWhiteSpace(polledWork.Stage));

        await OpenGenericSessionAsync(projectId, sessionId, polledWork);
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{projectId}/{sessionId}/runtime-events",
            new
            {
                workId = polledWork.WorkId,
                workType = polledWork.WorkType,
                stage = polledWork.Stage,
                runtimeEvents,
            });
        await ReportDispatchCompletedAsync(_runnerId, polledWork);

        return new FakeAgentRunResult(
            sessionId,
            polledWork.WorkId,
            polledWork.WorkType,
            polledWork.Stage,
            runtimeEvents.Select(ReadRuntimeEventType).ToArray());
    }

    private static string ReadRuntimeEventType(object runtimeEvent)
    {
        var type = runtimeEvent.GetType().GetProperty("type")?.GetValue(runtimeEvent) as string;
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("Fake runtime event is missing a type");
        return type;
    }

    private async Task ReportDispatchCompletedAsync(string runnerId, PollResult polledWork)
    {
        Assert.False(string.IsNullOrWhiteSpace(polledWork.AgentJobId));
        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(polledWork.AgentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            polledWork.WorkId,
            new Mohist.Server.Runner.Grains.WorkResult(
                Status: "completed",
                Message: "ok",
                Output: "{}",
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected completed report");
    }

    private static JsonElement FindTurnByUserText(JsonElement transcriptData, string text)
    {
        foreach (var turn in transcriptData.GetProperty("turns").EnumerateArray())
        {
            if (turn.GetProperty("user").GetProperty("text").GetString() == text)
                return turn;
        }

        throw new InvalidOperationException($"No transcript turn found for prompt '{text}'");
    }

    private static void AssertAssistantText(JsonElement turn, string text)
    {
        Assert.Contains(turn.GetProperty("assistant").EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "text"
            && (part.GetProperty("text").GetString()?.Contains(text, StringComparison.Ordinal) ?? false));
    }

    private static void AssertAssistantTool(JsonElement turn, string toolCallId)
    {
        Assert.Contains(turn.GetProperty("assistant").EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "tool"
            && part.TryGetProperty("tool", out var tool)
            && tool.GetProperty("toolCallId").GetString() == toolCallId);
    }

    private static async Task<IReadOnlyList<JsonElement>> LoadTranscriptPartPayloadsAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        string partType)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToArrayAsync();
        var payloads = await db.AgentSessionTranscriptParts
            .AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId) && p.Type == partType)
            .OrderBy(p => p.Sequence)
            .Select(p => p.PayloadJson)
            .ToArrayAsync();
        return payloads.Select(payload => JsonSerializer.Deserialize<JsonElement>(payload)).ToArray();
    }

    private async Task<PollResult> PollOnceAsync(string runnerId, string expectedSessionId)
    {
        var attempts = 50;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            PollResult? match = null;
            foreach (var data in dispatches)
            {
                var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                    && agentSessionIdElement.ValueKind != JsonValueKind.Null
                    ? agentSessionIdElement.GetString()
                    : null;
                if (match is null && polledSessionId == expectedSessionId)
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
                    match = new PollResult(
                        WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                        WorkId: workId,
                        WorkType: data.GetProperty("workType").GetString() ?? string.Empty,
                        Stage: data.GetProperty("stage").GetString() ?? string.Empty,
                        AgentJobId: agentJobId,
                        ProjectId: projectId,
                        AgentSessionId: polledSessionId,
                        OwnerKind: ownerKind);
                }
                else
                {
                    await DrainDispatchElementAsync(runnerId, data);
                }
            }

            if (match is not null) return match;
        }

        throw new InvalidOperationException($"No polled dispatch carrying AgentSessionId='{expectedSessionId}' after {attempts} attempts");
    }

    private async Task DrainRemainingDispatchAsync(string runnerId, string? expectedSessionId = null)
    {
        var attempts = 30;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            if (dispatches.Count == 0) return;
            foreach (var data in dispatches)
            {
                var polledSessionId = data.TryGetProperty("agentSessionId", out var agentSessionIdElement)
                    && agentSessionIdElement.ValueKind != JsonValueKind.Null
                    ? agentSessionIdElement.GetString()
                    : null;
                if (expectedSessionId is not null && polledSessionId != expectedSessionId)
                    return;

                await DrainDispatchElementAsync(runnerId, data);
            }
        }
    }

    private async Task DrainDispatchElementAsync(string runnerId, JsonElement data)
    {
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
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
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
        string WorkType,
        string Stage,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind);

    private sealed record FakeAgentRunResult(
        string SessionId,
        string WorkId,
        string WorkType,
        string Stage,
        IReadOnlyList<string> EventTypes);

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record ProjectRef(string Id, string Path);
    private sealed record AgentRef(string Id, string Name);
}
