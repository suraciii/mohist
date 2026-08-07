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

    [Fact]
    public async Task InitialTurnTerminal_SchedulesQueuedWorkflowFollowup()
    {
        var (project, _, _, sessionName, sessionId) = await CreateWorkflowSessionAsync("initial-terminal-followup");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("ReceiveFollowup", _ =>
        {
            started.TrySetResult();
            return new RunnerFollowupDeliveryResult(true);
        });
        tracker.Register(_runnerId, "conn-initial-terminal-followup");
        try
        {
            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
                InputId: "initial-input",
                TurnId: "initial-turn",
                Prompt: "initial prompt",
                Source: "agent-launch",
                JobId: "initial-job"));
            await grain.MarkInitialTurnExecutingAsync("initial-job");
            var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: "continue after launch",
                Source: "agent-session-followup",
                IdempotencyKey: "initial-terminal-followup"));

            await grain.MarkInitialTurnTerminalAsync("initial-job", AgentTurnStatus.Completed, null);
            await started.Task;

            var payload = JsonSerializer.SerializeToElement(Assert.Single(runnerHub.Invocations).Arguments.Single());
            Assert.Equal(followup.OperationId, payload.GetProperty("operationId").GetString());
            Assert.Equal("continue after launch", payload.GetProperty("text").GetString());
            Assert.Equal(sessionName, payload.GetProperty("target").GetProperty("sessionName").GetString());
            Assert.Equal(project.Id, payload.GetProperty("target").GetProperty("projectId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task AgentConnectionInitialExecution_BindsRunnerId_AndIdleFollowupIsDelivered()
    {
        var project = await CreateProjectAsync("agent-connection-open-followup");
        var sessionId = $"agent-connection-followup-{project.Id}";
        var workDir = WorkDirFor(project.Id);
        var metadata = new AgentSessionMetadata(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = project.Id,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
                [GenericAgentSessionMetadata.AgentId] = "connection-agent",
                [GenericAgentSessionMetadata.AgentName] = "Connection Agent",
            });
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "connection-initial-input",
            TurnId: "connection-initial-turn",
            Prompt: "initial prompt",
            Source: "agent-connection",
            JobId: "connection-initial-job",
            Metadata: metadata,
            Runtime: "opencode",
            WorkDir: workDir));

        using var open = await _client.PostAsJsonAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
            new { workDir });
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        var opened = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        Assert.Equal(_runnerId, opened.RunnerId);

        using var attach = await _client.PostAsJsonAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/attach",
            new { runtimeSessionId = "connection-runtime", workDir });
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode);

        await grain.MarkInitialTurnExecutingAsync("connection-initial-job");
        await grain.MarkInitialTurnTerminalAsync("connection-initial-job", AgentTurnStatus.Completed, null);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(true));
        tracker.Register(_runnerId, "conn-agent-connection-followup");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "continue" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("accepted", responseDoc.RootElement.GetProperty("data").GetProperty("status").GetString());

            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("ReceiveFollowup", invocation.Method);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var binding = payload.GetProperty("target").GetProperty("binding");
            Assert.Equal(_runnerId, binding.GetProperty("runnerId").GetString());
            Assert.Equal("connection-runtime", binding.GetProperty("runtimeSessionId").GetString());
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
    public async Task FollowupAcceptedDuringClaimedDelivery_QueuesAndDeliversNextTurn()
    {
        var (project, sessionId, runtimeSessionId) = await CreateIdleGenericSessionAsync("followup-claimed-delivery");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        var firstDeliveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelivery = new TaskCompletionSource<RunnerFollowupDeliveryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("ReceiveFollowup", _ =>
        {
            deliveries++;
            if (deliveries == 1)
            {
                firstDeliveryStarted.TrySetResult();
                return firstDelivery.Task;
            }

            return new RunnerFollowupDeliveryResult(true);
        });
        tracker.Register(_runnerId, "conn-followup-claimed-delivery");
        try
        {
            var first = PostGenericFollowupAsync(project.Id, sessionId, new { text = "first input" }, "claimed-delivery-first");
            await firstDeliveryStarted.Task;

            using var second = await PostGenericFollowupAsync(
                project.Id,
                sessionId,
                new { text = "second input" },
                "claimed-delivery-second");
            var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Single(runnerHub.Invocations);

            firstDelivery.SetResult(new RunnerFollowupDeliveryResult(true));
            using var firstResponse = await first;
            var firstData = (await firstResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.NotEqual(firstData.GetProperty("turnId").GetString(), secondData.GetProperty("turnId").GetString());

            var firstPayload = JsonSerializer.SerializeToElement(runnerHub.Invocations[0].Arguments.Single());
            var firstOperationId = firstPayload.GetProperty("operationId").GetString();
            Assert.Equal("first input", firstPayload.GetProperty("text").GetString());

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new[]
                {
                    new AgentSessionRuntimeEventInput(
                        RuntimeEventTypes.SessionInput,
                        $$"""{"text":"first input","kind":"followup","operationId":"{{firstOperationId}}"}"""),
                    new AgentSessionRuntimeEventInput(
                        RuntimeEventTypes.SessionActivity,
                        $$"""{"activity":"idle","status":"completed","operationId":"{{firstOperationId}}"}"""),
                },
                runtimeSessionId));

            await using var scope = _fixture.Services.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<AgentSessionFollowupDispatcher>();
            await dispatcher.DispatchNextAsync(project.Id, sessionId, CancellationToken.None);

            Assert.Equal(2, runnerHub.Invocations.Count);
            var secondPayload = JsonSerializer.SerializeToElement(runnerHub.Invocations[1].Arguments.Single());
            Assert.Equal("second input", secondPayload.GetProperty("text").GetString());
        }
        finally
        {
            firstDelivery.TrySetResult(new RunnerFollowupDeliveryResult(true));
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task CancelledFollowupDelivery_ReleasesClaimForSameKeyRetry()
    {
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("followup-cancelled-delivery");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelivery = new TaskCompletionSource<RunnerFollowupDeliveryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("ReceiveFollowup", _ =>
        {
            attempt++;
            started.TrySetResult();
            return attempt == 1 ? firstDelivery.Task : new RunnerFollowupDeliveryResult(true);
        });
        tracker.Register(_runnerId, "conn-followup-cancelled-delivery");
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/{project.Id}/agent-sessions/{sessionId}/followup")
            {
                Content = JsonContent.Create(new { text = "retry after cancellation" }),
            };
            request.Headers.Add("Idempotency-Key", "cancelled-delivery-key");

            var first = _client.SendAsync(request, cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            using var retry = await PostGenericFollowupAsync(
                project.Id,
                sessionId,
                new { text = "retry after cancellation" },
                "cancelled-delivery-key");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(2, attempt);
        }
        finally
        {
            firstDelivery.TrySetResult(new RunnerFollowupDeliveryResult(true));
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
