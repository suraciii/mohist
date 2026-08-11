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

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionTranscriptAxisSpecs : GenericAgentSessionTranscriptAxisTestSupport
{
    public GenericAgentSessionTranscriptAxisSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

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
        var workspaceName = await CreateRunnerHomeWorkspaceAsync(
            project.Id,
            _runnerId,
            "transcript-axis-launch");

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(
                project.Id,
                agent.Id,
                new { prompt = "transcript-axis launch", context = new { workspace = workspaceName } });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var polledWork = await PollOnceAsync(jobId, _runnerId, sessionId);

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
        var workspaceName = await CreateRunnerHomeWorkspaceAsync(
            project.Id,
            _runnerId,
            "transcript-axis-events");

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(
                project.Id,
                agent.Id,
                new { prompt = "transcript-axis events", context = new { workspace = workspaceName } });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var polledWork = await PollOnceAsync(jobId, _runnerId, sessionId);
            var persistence = _fixture.Persistence.Checkpoint(sessionId);

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

            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 4, persistence);

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

            // Under the activity model (issue-484) the AgentJob terminal
            // close is persisted as a `session.activity` transcript part
            // (the legacy `session.closed` part is gone). The close fact
            // (status) still rides on the part payload.
            var closePayload = Assert.Single(await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.SessionActivity));
            Assert.Equal("completed", closePayload.GetProperty("status").GetString());

            using var summaryResponse = await _fixture.Client.GetAsync(
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
            var summaryPayload = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
            var summaryData = summaryPayload.GetProperty("data");
            Assert.Equal(sessionId, summaryData.GetProperty("sessionId").GetString());
            // The summary now exposes `activity` (idle/active/unknown) rather
            // than a terminal `status`. A completed close returns the session
            // to idle activity.
            Assert.Equal("idle", summaryData.GetProperty("activity").GetString());
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
        var workspaceName = await CreateRunnerHomeWorkspaceAsync(
            project.Id,
            _runnerId,
            "transcript-axis-followup");

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(
                project.Id,
                agent.Id,
                new { prompt = "transcript-axis first turn", context = new { workspace = workspaceName } });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var polledWork = await PollOnceAsync(jobId, _runnerId, sessionId);
            var firstPersistence = _fixture.Persistence.Checkpoint(sessionId);
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
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 2, firstPersistence);

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
            // The first turn's dispatch was reported completed (the helper
            // reports the AgentJob result), which delivers a terminal close
            // and returns the session to idle activity under the activity
            // model (issue-484). The summary exposes `activity` rather than
            // a terminal `status`.
            Assert.Equal("idle", completedPayload.GetProperty("data").GetProperty("activity").GetString());

            var followupPersistence = _fixture.Persistence.Checkpoint(sessionId);
            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeSessionId = sessionId,
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

            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 5, followupPersistence);

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

    [Fact]
    public async Task GenericTranscript_RuntimeSessionFilter_ReturnsOnlySelectedBindingTurns()
    {
        var project = await CreateProjectAsync("transcript-runtime-filter");
        var sessionId = $"transcript-runtime-filter-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                "transcript-filter-agent",
                "transcript-filter-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-first", WorkDir: $"/workspaces/{project.Id}"));
        var firstPersistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionInput, """{"text":"first runtime turn","kind":"task"}"""),
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.MessageDelta, """{"text":"first runtime reply"}"""),
        }, "runtime-first"));
        // Under the activity model (issue-484) the session is authoritative-
        // ly active after the input/reply pair and no longer auto-idles by
        // time window. Reset requires an idle session, so emit an explicit
        // `session.activity:{idle}` turn-end signal before rebinding.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, """{"activity":"idle"}"""),
        }, "runtime-first"));
        await firstPersistence.WaitAsync();
        await grain.ResetAsync(new ResetAgentSessionCommand("runtime-first", "runtime-second"));
        var secondPersistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionInput, """{"text":"second runtime turn","kind":"followup"}"""),
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.MessageDelta, """{"text":"second runtime reply"}"""),
        }, "runtime-second"));
        await secondPersistence.WaitAsync();

        using var firstResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript?runtimeSessionId=runtime-first");
        using var secondResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/agent-sessions/{sessionId}/transcript?runtimeSessionId=runtime-second");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var firstPayload = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondPayload = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("first runtime turn", Assert.Single(firstPayload.GetProperty("data").GetProperty("turns").EnumerateArray())
            .GetProperty("user").GetProperty("text").GetString());
        Assert.Equal("runtime-first", firstPayload.GetProperty("data").GetProperty("turns")[0]
            .GetProperty("user").GetProperty("runtimeSessionId").GetString());
        var secondTurn = FindTurnByUserText(secondPayload.GetProperty("data"), "second runtime turn");
        Assert.Equal("second runtime turn", secondTurn.GetProperty("user").GetProperty("text").GetString());
        Assert.Equal("runtime-second", secondTurn
            .GetProperty("user").GetProperty("runtimeSessionId").GetString());
    }

    [Fact]
    public async Task CompactAfterResetWithoutInput_IsStoredOnReplacementRuntime()
    {
        var project = await CreateProjectAsync("transcript-recovery-runtime");
        var sessionId = $"transcript-recovery-runtime-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                "transcript-recovery-agent",
                "transcript-recovery-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-first", WorkDir: $"/workspaces/{project.Id}"));
        var firstPersistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionInput, """{"text":"first runtime turn","kind":"task"}"""),
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.MessageDelta, """{"text":"first runtime reply"}"""),
        }, "runtime-first"));
        // Under the activity model (issue-484) the session no longer auto-
        // idles by time window; Reset requires an idle session, so emit an
        // explicit `session.activity:{idle}` turn-end signal first.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, """{"activity":"idle"}"""),
        }, "runtime-first"));
        await firstPersistence.WaitAsync();

        await grain.ResetAsync(new ResetAgentSessionCommand("runtime-first", "runtime-second"));
        var secondPersistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.Compaction, """{"strategy":"summary"}"""),
        }, "runtime-second"));
        await secondPersistence.WaitAsync();

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var turns = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .ToDictionaryAsync(turn => turn.Id, turn => turn.RuntimeSessionId);
        var compactionTurnIds = await db.AgentSessionTranscriptParts
            .Where(part => part.Type == TranscriptPartTypes.Compaction && turns.Keys.Contains(part.TurnId))
            .Select(part => part.TurnId)
            .ToListAsync();

        var compactionRuntimes = compactionTurnIds.Select(turnId => turns[turnId]).Distinct().ToArray();
        Assert.Single(compactionRuntimes);
        Assert.Equal("runtime-second", compactionRuntimes[0]);
    }

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
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(project.Id, agent.Id, new { prompt = "session-id only" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(sessionId, WorkDir: $"/workspaces/{project.Id}"));
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

            await _fixture.Client.PostOkAsync(
                $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/runtime-events",
                new
                {
                    runtimeSessionId = sessionId,
                    runtimeEvents = new object[]
                    {
                        new { type = "session.input", payload = new { text = "session-id only", kind = "task" } },
                        new { type = "message.delta", payload = new { content = new { text = "hello" } } }
                    }
                });

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 1, persistence);

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

}
