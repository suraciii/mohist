using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("PlatformIntegration")]
public class AgentSessionBindingSpecs : AgentSessionTestSupport
{
    public AgentSessionBindingSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task AgentSessionGrain_ForAgentWork_CreatesGuidSessionAndKeepsPollIdempotent()
    {
        var (_, _, work, session) = await CreateStartedAgentSessionAsync("idempotent", start: false);
        Assert.True(Guid.TryParseExact(session.Id, "N", out _));

        var repeated = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(repeated);
        Assert.Equal(session.Id, repeated.Id);
    }

    [Fact]
    public async Task RunnerAttach_DifferentPhysicalSession_ReturnsConflictAndPreservesBinding()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("attach-conflict", start: false);
        await _client.PostOkAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = "runtime-1", runtime = "opencode", expectedRuntime = "opencode", expectedRuntimeSessionId = (string?)null, workDir = "/work", processPid = 1234 });

        using var response = await _client.PostAsJsonAsync(RunnerAgentSessionAttachPath(session), new { runtimeSessionId = "runtime-2", runtime = "opencode", expectedRuntime = "opencode", expectedRuntimeSessionId = "runtime-1", workDir = "/work", processPid = 1234 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("agent_session_attach_conflict", body.RootElement.GetProperty("code").GetString());
        var sessionAfterConflict = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(session.Id)
            .GetAsync();
        Assert.NotNull(sessionAfterConflict);
        Assert.Equal("runtime-1", sessionAfterConflict.AgentSessionId);
    }

    [Fact]
    public async Task RunnerReportsTerminalSession_TerminalStatusExists_IgnoresLaterStatusChanges()
    {
        var (project, _, _, session) = await CreateStartedAgentSessionAsync("terminal-lock");

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });
        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.liveness", payload = new { status = "probing", failureReason = "late" } }
            }
        });
        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "late-failure", exitCode = 1 } }
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        // session.closed is a no-op under the activity model (issue-484);
        // the session never enters a terminal state, so it stays idle.
        Assert.Equal("idle", grainSession.Status);
    }

    [Fact]
    public async Task AgentSessionOpen_ClosedRuntimeObservation_DoesNotRebindSession()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-reuse");

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "first attempt", exitCode = 1 } }
            }
        });

        var retryRunnerId = $"{_runnerId}-retry";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var reopened = await grain.OpenAsync(new OpenAgentSessionCommand(
            retryRunnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, session.IssueNumber, session.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, reopened.Id);
        Assert.Equal("idle", reopened.Status);
        Assert.Equal(_runnerId, reopened.RunnerId);

        var nextRunnerId = $"{_runnerId}-next";
        var repeated = await grain.OpenAsync(new OpenAgentSessionCommand(
            nextRunnerId,
            "opencode",
            Metadata: WorkflowSessionMetadata(project.Id, session.IssueNumber, session.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, repeated.Id);
        Assert.Equal("idle", repeated.Status);
        Assert.Equal(_runnerId, repeated.RunnerId);
    }

    [Fact]
    public async Task RuntimeEvents_AfterFailedClosedObservation_KeepSessionActive()
    {
        var (_, _, _, session) = await CreateStartedAgentSessionAsync("runner-unregister");

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "session.closed", payload = new { status = "failed", failureReason = "Runner unregistered", exitCode = 1 } },
                new { type = "message.delta", payload = new { text = "new data" } },
            }
        });

        var grainSession = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
        Assert.NotNull(grainSession);
        // session.closed is a no-op under the activity model (issue-484)
        // and message.delta does not flip activity to active; only
        // session.input does. The session stays idle but keeps absorbing
        // runner events.
        Assert.Equal("idle", grainSession.Status);
    }

    [Fact]
    public async Task OpenAgentSession_ExistingBoundSessionKeepsRuntimeBinding()
    {
        var (project, _, work, session) = await CreateStartedAgentSessionAsync("retry-terminal");

        var opened = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, work.Issue!.IssueNumber, work.WorkflowRunId, session.SessionName, work.WorkId, work.WorkType, work.Stage, work.Title)));

        Assert.Equal(session.Id, opened.Id);
        Assert.Equal("idle", opened.Status);
        Assert.NotNull(opened.AgentSessionId);
    }

    [Fact]
    public async Task OpenAgentSession_ClosedObservationKeepsRuntimeBinding()
    {
        var (project, issue, work, session) = await CreateStartedAgentSessionAsync("named-reuse", sessionName: "check");

        await _client.PostOkAsync(RunnerSessionRuntimeEventsPath(session), new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.closed", payload = new { status = "completed", exitCode = 0 } }
            }
        });

        var opened = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id)
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, issue.Number, work.WorkflowRunId, session.SessionName, "fix-review-findings:1.1", "task", "check", "Fix review findings")));

        Assert.Equal(session.Id, opened.Id);
        Assert.Equal("idle", opened.Status);
        Assert.NotNull(opened.AgentSessionId);
    }

}
