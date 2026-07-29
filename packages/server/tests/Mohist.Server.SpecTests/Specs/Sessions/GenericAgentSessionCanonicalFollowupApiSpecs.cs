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
    public async Task CanonicalFollowupRoute_WorkflowSession_UsesWorkflowTarget()
    {
        var (project, _, workflowRunId, sessionName, sessionId) = await CreateWorkflowSessionAsync("canonical-workflow-shape");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
            var persisted = await querier.ResolveCanonicalFollowupTargetAsync(project.Id, sessionId);
            Assert.NotNull(persisted);
            Assert.Equal("opencode", persisted!.Runtime);
            Assert.Equal(WorkDirFor(project.Id), persisted.WorkDir);
            Assert.Equal(sessionId, persisted.RuntimeSessionId);
            Assert.Equal(_runnerId, persisted.RunnerId);
        }
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-canonical-workflow-shape");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ship through canonical route" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(sessionId, responseDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString());

            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("ReceiveFollowup", invocation.Method);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var target = payload.GetProperty("target");
            Assert.Equal("workflow", target.GetProperty("kind").GetString());
            Assert.Equal(workflowRunId, target.GetProperty("workflowRunId").GetString());
            Assert.Equal(sessionName, target.GetProperty("sessionName").GetString());
            var binding = target.GetProperty("binding");
            Assert.True(binding.ValueKind == JsonValueKind.Object, payload.GetRawText());
            Assert.Equal(sessionId, binding.GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Followup_AfterReset_IgnoresTerminalActivityFromPredecessorRuntime()
    {
        var (project, sessionId, firstRuntimeSessionId) = await CreateIdleGenericSessionAsync("followup-reset-terminal");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, """{"activity":"idle","status":"completed","operationId":"terminal-delivery"}"""),
        }, firstRuntimeSessionId));
        await persistence.WaitAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await grain.ResetAsync(new ResetAgentSessionCommand(firstRuntimeSessionId, "runtime-replacement"));

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(true));
        tracker.Register(_runnerId, "conn-followup-reset-terminal");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "continue on the replacement" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = JsonSerializer.SerializeToElement(Assert.Single(runnerHub.Invocations).Arguments.Single());
            Assert.Equal("runtime-replacement", payload.GetProperty("target").GetProperty("binding").GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task IdleFollowupReservation_BlocksRecoveryUntilDeliveryCompletes()
    {
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("followup-recovery-race");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new TaskCompletionSource<RunnerFollowupDeliveryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("ReceiveFollowup", _ =>
        {
            started.TrySetResult();
            return delivery.Task;
        });
        tracker.Register(_runnerId, "conn-followup-recovery-race");
        try
        {
            var followup = PostGenericFollowupAsync(project.Id, sessionId, new { text = "start and hold" });
            await started.Task;

            using var compact = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/compact", content: null);
            using var reset = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/reset", content: null);

            Assert.Equal(HttpStatusCode.Conflict, compact.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
            Assert.Equal("session_active", JsonDocument.Parse(await compact.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.Equal("session_active", JsonDocument.Parse(await reset.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(runnerHub.Invocations, invocation => invocation.Method == "SessionCommand");

            delivery.SetResult(new RunnerFollowupDeliveryResult(true));
            using var accepted = await followup;
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task OfflineRunnerFollowup_AcceptsAndQueues_BlockingCompactUntilTerminal()
    {
        // Per the new accept semantics (D4): a runner-offline result
        // no longer reverts acceptance. The input is persisted and the
        // turn stays queued; Compact/Reset is blocked by the non-terminal
        // follow-up turn until the session.activity idle event for the
        // matching operationId marks it terminal.
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("followup-offline-accept");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.Register(_runnerId, "conn-followup-offline-accept");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ping while offline" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
            Assert.False(string.IsNullOrEmpty(data.GetProperty("turnId").GetString()));

            // The non-terminal follow-up turn blocks Compact.
            using var compact = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/compact", content: null);
            Assert.Equal(HttpStatusCode.Conflict, compact.StatusCode);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

}
