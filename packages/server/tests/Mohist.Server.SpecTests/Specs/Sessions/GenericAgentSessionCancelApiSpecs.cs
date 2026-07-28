using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionCancelApiSpecs : GenericAgentSessionCancelApiTestSupport
{
    public GenericAgentSessionCancelApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Cancel_ActiveSessionRunnerReportsCancelled_ReturnsCancelledState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-ok");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("cancelled"));
        tracker.Register(_runnerId, "conn-gen-cancel-ok");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", doc.RootElement.GetProperty("data").GetProperty("state").GetString());

            // The runner received a `CancelAgentSession` invocation with
            // the unified generic session target.
            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("CancelAgentSession", invocation.Method);
            Assert.Equal("conn-gen-cancel-ok", invocation.ConnectionId);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var target = payload.GetProperty("target");
            Assert.Equal("generic", target.GetProperty("kind").GetString());
            Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
            Assert.Equal(sessionId, target.GetProperty("sessionId").GetString());
            Assert.Equal("opencode", target.GetProperty("binding").GetProperty("runtime").GetString());
            Assert.Equal(sessionId, target.GetProperty("binding").GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Cancel_StopUnconfirmed_MirrorsInterruptUnconfirmedToHttpResponse()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-unconfirmed");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("cancelled", true));
        tracker.Register(_runnerId, "conn-gen-cancel-unconfirmed");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("cancelled", data.GetProperty("state").GetString());
            Assert.True(data.GetProperty("interruptUnconfirmed").GetBoolean());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("agent-launch")]
    public async Task Cancel_ActiveTurn_PreservesSessionTranscriptForBothSources(string sourceKind)
    {
        var (project, sessionId) = await CreateCanonicalSessionForCancelAsync(sourceKind);
        var before = await ReadSessionEvidenceAsync(sessionId);
        Assert.Equal(sourceKind, before.SourceKind);
        Assert.NotEmpty(before.TranscriptParts);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("cancelled"));
        tracker.Register(_runnerId, $"conn-cancel-preserves-{sourceKind}");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", doc.RootElement.GetProperty("data").GetProperty("state").GetString());

            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("CancelAgentSession", invocation.Method);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var target = payload.GetProperty("target");
            if (sourceKind == "workflow")
            {
                Assert.Equal("workflow", target.GetProperty("kind").GetString());
                Assert.True(target.TryGetProperty("workflowRunId", out _));
                Assert.True(target.TryGetProperty("sessionName", out _));
            }
            else
            {
                Assert.Equal("generic", target.GetProperty("kind").GetString());
                Assert.Equal(sessionId, target.GetProperty("sessionId").GetString());
            }

            var queryable = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.Equal(sessionId, queryable?.Id);

            var after = await ReadSessionEvidenceAsync(sessionId);
            Assert.Equal(before.SessionId, after.SessionId);
            Assert.Equal(before.SourceKind, after.SourceKind);
            Assert.Equal(before.RuntimeSessionId, after.RuntimeSessionId);
            Assert.True(before.TranscriptTurns.SequenceEqual(after.TranscriptTurns, StringComparer.Ordinal));
            Assert.True(before.TranscriptParts.SequenceEqual(after.TranscriptParts, StringComparer.Ordinal));
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Cancel_ActiveSessionRunnerReportsNotCancellable_ReturnsNotCancellableState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-not-cancellable");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("not-cancellable"));
        tracker.Register(_runnerId, "conn-gen-cancel-not-cancellable");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // The response mirrors the runner's reported state; the API
            // never pretends success.
            Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Cancel_RunnerInvocationFails_ReturnsNotCancellableState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-transport-failure");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("CancelAgentSession", _ =>
            Task.FromException<AgentSessionCancelReply>(new InvalidOperationException("runner disconnected")));
        tracker.Register(_runnerId, "conn-gen-cancel-transport-failure");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
            Assert.Single(runnerHub.Invocations);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Cancel_AfterReset_IgnoresTerminalActivityFromPredecessorRuntime()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-reset-terminal");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, """{"activity":"idle","status":"completed","operationId":"terminal-delivery"}"""),
        }, sessionId));
        await grain.WaitForPersistenceAsync(_fixture.Persistence);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await grain.ResetAsync(new ResetAgentSessionCommand(sessionId, "runtime-replacement"));

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("cancelled"));
        tracker.Register(_runnerId, "conn-cancel-reset-terminal");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

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
    public async Task Cancel_UnknownSession_ReturnsNotFound()
    {
        var project = await CreateProjectAsync("gen-cancel-404");

        using var response = await PostGenericCancelAsync(project.Id, Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_SessionInOtherProject_ReturnsNotFound()
    {
        var (_, _, sessionIdInA, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-isolation-a");
        var projectB = await CreateProjectAsync("gen-cancel-isolation-b");

        using var response = await PostGenericCancelAsync(projectB.Id, sessionIdInA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_ActiveSessionButRunnerOffline_ReturnsNotCancellableState()
    {
        // The session is opened (so the runner has bound a RunnerId) but
        // the runner's SignalR connection is not registered: the API
        // surfaces `not-cancellable` honestly rather than faking success.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-offline");
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();

        using var response = await PostGenericCancelAsync(project.Id, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Empty(runnerHub.Invocations);
    }

    [Fact]
    public async Task Cancel_UnopenedAgentLaunchSession_ReturnsNotCancellableWithoutRequiringRuntimeBinding()
    {
        var project = await CreateProjectAsync("gen-cancel-unopened");
        var sessionId = $"cancel-unopened-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{project.Id}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                "cancel-unopened-agent",
                "cancel-unopened-agent"))));

        using var response = await PostGenericCancelAsync(project.Id, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_BoundSessionWithUnregisteredRuntime_ReturnsNotCancellable()
    {
        // The session is bound to an unregistered runtime (acp); the
        // cancel path cannot reach a live runtime session, so the API
        // honestly surfaces `not-cancellable` instead of faking a cancel.
        // (issue-484: the runtime_session_missing reset hint no longer
        // applies to the cancel path.)
        var (project, sessionId) = await CreateCanonicalSessionForCancelAsync("agent-launch", runtime: "acp");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.Register(_runnerId, "conn-gen-cancel-missing-runtime");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task Cancel_RunnerRepliesWithTerminalState_MirrorsThatTerminalState()
    {
        // The runner is allowed to return a terminal-state name in its
        // reply (e.g. it observed the session close as a side effect of
        // the cancel notification). The server mirrors that value
        // verbatim into the HTTP response.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-runner-terminal");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("failed"));
        tracker.Register(_runnerId, "conn-gen-cancel-runner-terminal");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("failed", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

}
