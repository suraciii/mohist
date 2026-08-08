using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionCanonicalFollowupApiSpecs : GenericAgentSessionFollowupApiTestSupport
{
    public GenericAgentSessionCanonicalFollowupApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task IssueScopedFollowupRoute_StillEmitsBothTopLevelAndTargetFields()
    {
        // Acceptance: the issue-scoped route must remain reachable AND its
        // payload must still populate `workflowRunId`/`sessionName` for
        // older runners. The unified `target` field is added on top so the
        // newer runner can route by kind, but the legacy fields stay.
        var (project, issue, workflowRunId, sessionName, sessionId) = await CreateWorkflowSessionAsync("gen-issue-scoped-shape");

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-issue-scoped-shape");
        try
        {
            using var response = await _client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/followup",
                new { text = "ship it" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var responseData = responseDoc.RootElement.GetProperty("data");
            Assert.Equal(sessionId, responseData.GetProperty("sessionId").GetString());
            Assert.Equal("accepted", responseData.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(responseData.GetProperty("inputId").GetString()));
            Assert.False(string.IsNullOrEmpty(responseData.GetProperty("turnId").GetString()));

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal("ReceiveFollowup", sent.Method);

            var payload = JsonSerializer.SerializeToElement(sent.Arguments.Single());
            Assert.Equal("ship it", payload.GetProperty("text").GetString());

            var target = payload.GetProperty("target");
            Assert.Equal("workflow", target.GetProperty("kind").GetString());
            Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
            Assert.Equal(workflowRunId, target.GetProperty("workflowRunId").GetString());
            Assert.Equal(sessionName, target.GetProperty("sessionName").GetString());
            Assert.Equal("opencode", target.GetProperty("binding").GetProperty("runtime").GetString());
            Assert.Equal(sessionId, target.GetProperty("binding").GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task IssueSessionMetadata_ExposesFollowupInputAndTurnStatus()
    {
        var (project, issue, _, sessionName, sessionId) = await CreateWorkflowSessionAsync("issue-followup-observation");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-issue-followup-observation");
        try
        {
            using var followup = await _client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/followup",
                new { text = "show follow-up status" });
            var followupData = (await followup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var inputId = followupData.GetProperty("inputId").GetString();
            var turnId = followupData.GetProperty("turnId").GetString();
            var payload = JsonSerializer.SerializeToElement(Assert.Single(runnerHub.Invocations).Arguments.Single());
            var operationId = payload.GetProperty("operationId").GetString();

            using var queued = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}");
            var queuedData = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("accepted", queuedData.GetProperty("inputs")[0].GetProperty("acceptance").GetString());
            Assert.Equal("queued", queuedData.GetProperty("turns")[0].GetProperty("status").GetString());

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new[] { new AgentSessionRuntimeEventInput(
                    RuntimeEventTypes.SessionInput,
                    $$"""{"text":"show follow-up status","kind":"followup","source":"agent-session-followup","operationId":"{{operationId}}"}""") },
                sessionId));
            await persistence.WaitAsync();

            using var executing = await _client.GetAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}");
            var executingData = (await executing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(inputId, executingData.GetProperty("inputs")[0].GetProperty("id").GetString());
            Assert.Equal(turnId, executingData.GetProperty("turns")[0].GetProperty("id").GetString());
            Assert.Equal("executing", executingData.GetProperty("turns")[0].GetProperty("status").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

}
