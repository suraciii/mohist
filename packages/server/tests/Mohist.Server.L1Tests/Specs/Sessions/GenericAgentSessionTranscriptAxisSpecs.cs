using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Sessions;

[Collection("SessionControlIntegration")]
[Trait("level", "L1")]
public class GenericAgentSessionTranscriptAxisSpecs : GenericAgentSessionTranscriptAxisTestSupport
{
    public GenericAgentSessionTranscriptAxisSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GenericLaunch_RuntimeEventsPersistObservableTranscriptAndSummary()
    {
        var project = await CreateProjectAsync("transcript-axis-transcript");
        var agent = await CreateAgentAsync(project.Id, "transcript-axis-events-agent");
        var workspaceName = await RegisterRunnerWithHomeWorkspaceAsync(project.Id, "transcript-axis-events");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(
            project.Id,
            agent.Id,
            new { prompt = "transcript-axis events", context = new { workspace = workspaceName } });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
        var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

        var polledWork = await ClaimPreparedDispatchAsync(jobId, _runnerId, sessionId);
        Assert.Equal(string.Empty, polledWork.WorkflowRunId);
        Assert.Equal(sessionId, polledWork.AgentSessionId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polledWork.OwnerKind);
        Assert.Equal(project.Id, polledWork.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(polledWork.AgentJobId));
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
        var transcriptData = (await transcriptResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var turn = FindTurnByUserText(transcriptData, "transcript-axis events");
        AssertAssistantText(turn, "Hello transcript axis.");
        AssertAssistantTool(turn, "tx-tool-1");

        var usagePayload = Assert.Single(
            await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.Usage));
        Assert.Equal(300, usagePayload.GetProperty("totalTokens").GetInt64());
        Assert.Equal("USD", usagePayload.GetProperty("costCurrency").GetString());

        var closePayload = Assert.Single(
            await LoadTranscriptPartPayloadsAsync(dbFactory, sessionId, TranscriptPartTypes.SessionActivity));
        Assert.Equal("completed", closePayload.GetProperty("status").GetString());

        using var summaryResponse = await _fixture.Client.GetAsync(
            $"/api/projects/{project.Id}/agent-sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summaryData = (await summaryResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(sessionId, summaryData.GetProperty("sessionId").GetString());
        Assert.Equal("idle", summaryData.GetProperty("activity").GetString());
        Assert.Equal(300, summaryData.GetProperty("usage").GetProperty("totalTokens").GetInt64());
    }
}
