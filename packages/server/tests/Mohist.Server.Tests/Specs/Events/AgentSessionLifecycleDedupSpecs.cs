using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events;

[Collection("EventPublishing")]
public class AgentSessionLifecycleDedupSpecs
{
    private readonly EventPublishingIntegrationFixture _fixture;
    private readonly string _runnerId = $"dedup-runner-{Guid.NewGuid():N}";

    public AgentSessionLifecycleDedupSpecs(EventPublishingIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AttachAgentAsync_PublishesAgentSessionStartedExactlyOnce()
    {
        var session = await CreateSessionWithoutAttachAsync("dedup-attach-started");

        _fixture.RecordingPublisher.Clear();

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.ProjectId}/{session.WorkflowRunId}/{session.SessionName}/attach",
            new { agentSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "after attach" } },
            }
        });

        var started = Assert.Single(_fixture.RecordingPublisher.Published,
            p => p.Type == EventCatalog.ReverseDns.AgentSessionStarted);
        Assert.Equal("/mohist/agent-session/" + session.Id, started.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AttachThenFirstRuntimeAppend_PublishesAgentSessionStartedOnceAcrossLifecycle()
    {
        var session = await CreateSessionWithoutAttachAsync("attach-append");

        _fixture.RecordingPublisher.Clear();

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.ProjectId}/{session.WorkflowRunId}/{session.SessionName}/attach",
            new { agentSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "first runtime row" } },
            }
        });

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionStarted));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_RepeatedCalls_PublishesAgentSessionStartedExactlyOnce()
    {
        // To exercise the firstRowThisCall Started path the session must
        // still be in AgentSessionStatus.Created when the first row
        // arrives. We do NOT call /attach here — that moves the phase
        // to Running, which would skip the Started emit. The first
        // AppendRuntimeEventsAsync transitions Created → Running via
        // EnsureActive and the grain's firstRowThisCall guard fires
        // exactly once.
        var session = await CreateSessionWithoutAttachAsync("dedup-started");

        _fixture.RecordingPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "first" } },
                new { type = "agent_message_chunk", payload = new { text = "second" } },
                new { type = "tool_call", payload = new { toolCallId = "t-1", kind = "read", status = "in_progress" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "third" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "fourth" } },
            }
        });

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionStarted));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalCompleted_PublishesCompletedExactlyOnce()
    {
        var session = await CreateStartedSessionAsync("dedup-completed");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "after-terminal" } },
            }
        });

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCompleted));
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionFailed));
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCancelled));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalFailed_PublishesFailedExactlyOnce()
    {
        var session = await CreateStartedSessionAsync("dedup-failed");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "failed", exitCode: 1, failureReason: "boom");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionFailed));
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCompleted));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalCancelled_PublishesCancelledExactlyOnce()
    {
        var session = await CreateStartedSessionAsync("dedup-cancelled");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "cancelled", exitCode: 0, failureReason: "user-cancel");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCancelled));
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCompleted));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_LivenessStatusNoChange_DoesNotPublishStatusChanged()
    {
        var session = await CreateStartedSessionAsync("dedup-liveness");

        _fixture.RecordingPublisher.Clear();

        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");

        // The liveness events above all map to AgentSessionStatus.Running.
        // The domain's MarkActive only emits AgentSessionActivated on a real
        // phase change, and the grain's FanOutRealtimeAsync only publishes
        // AgentSessionStatusChanged on Activated. So no StatusChanged emits
        // are expected for repeated liveness rows with the same status.
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionStatusChanged));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_TranscriptRows_DoNotPublishToDomainBus()
    {
        var session = await CreateStartedSessionAsync("dedup-transcript");

        _fixture.RecordingPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "coder_text_chunk", payload = new { text = "thinking" } },
                new { type = "coder_thought_chunk", payload = new { content = new { text = "thought" } } },
                new { type = "coder_tool_call", payload = new { toolCallId = "t-1", kind = "read", status = "in_progress" } },
                new { type = "ralph_task_update", payload = new { taskId = "rt-1", status = "in_progress" } },
                new { type = "ralph_loop_progress", payload = new { loop = 1, total = 5 } },
                new { type = "agent_liveness_status", payload = new { status = "running" } },
                new { type = "agent_usage_update", payload = new { inputTokens = 10, outputTokens = 5 } },
                new { type = "agent_session_model_resolved", payload = new { resolvedModel = "anthropic/claude" } },
            }
        });

        // None of the eight transcript observation event types must ever
        // appear in the domain bus. They flow through ITranscriptEventPublisher
        // (out of scope for this recording publisher).
        Assert.Equal(0, _fixture.RecordingPublisher.Published.Count(p =>
            p.Type == "coder_text_chunk" ||
            p.Type == "coder_thought_chunk" ||
            p.Type == "coder_tool_call" ||
            p.Type == "ralph_task_update" ||
            p.Type == "ralph_loop_progress" ||
            p.Type == "agent_liveness_status" ||
            p.Type == "agent_usage_update" ||
            p.Type == "agent_session_model_resolved"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_RunnerTranscriptRows_PublishToTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("runner-transcript-push");

        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            events = new object[]
            {
                new { type = "agent_message_chunk", payload = new { text = "hello" } },
                new { type = "tool_call", payload = new { toolCallId = "t-1", toolName = "Read", status = "started" } },
                new { type = "tool_call_update", payload = new { toolCallId = "t-1", toolName = "Read", status = "completed" } },
            }
        });

        Assert.Collection(
            _fixture.RecordingTranscriptPublisher.Published,
            first => Assert.Equal("agent_message_chunk", first.Type),
            second => Assert.Equal("tool_call", second.Type),
            third => Assert.Equal("tool_call_update", third.Type));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalEvent_PublishesLifecycleOnDomainBus()
    {
        var session = await CreateStartedSessionAsync("dedup-terminal-publishes");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        // The terminal event row itself is persisted to
        // AgentSessionRuntimeEvents; the domain bus receives the matching
        // lifecycle domain event (AgentSessionCompleted in this case).
        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionCompleted));
        // The domain bus MUST NOT receive the transcript-style "agent_session_terminal"
        // row directly — only the mapped lifecycle event.
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType("agent_session_terminal"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_LifecycleEvents_CarryReverseDnsType()
    {
        var session = await CreateStartedSessionAsync("dedup-bus-type");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "failed", exitCode: 1, failureReason: "x");

        // Lock in the wire contract: lifecycle events are published under
        // their reverse-DNS names, not the legacy PascalCase or
        // snake_case variants.
        var failed = Assert.Single(_fixture.RecordingPublisher.Published,
            p => p.Type == EventCatalog.ReverseDns.AgentSessionFailed);
        Assert.Equal("/mohist/agent-session/" + session.Id, failed.Source);
    }

    private async Task<CreatedSession> CreateStartedSessionAsync(string name)
    {
        var session = await CreateSessionWithoutAttachAsync(name);

        await _fixture.Client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{session.ProjectId}/{session.WorkflowRunId}/{session.SessionName}/attach",
            new { agentSessionId = session.Id, workDir = "/tmp", processPid = 4321 });

        return session;
    }

    private async Task<CreatedSession> CreateSessionWithoutAttachAsync(string name)
    {
        var projectName = $"dedup-{name}-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects",
            new { name = projectName, path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _fixture.Client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues",
            new { title = $"Dedup {name}", body = "track lifecycle emits", labels = Array.Empty<string>(), priority = "p1", projectId = project.Id });

        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var sessionName = $"work-{Guid.NewGuid():N}";
        var session = await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(project.Id, workflowRunId, sessionName))
            .EnsureAsync(new EnsureAgentSessionCommand(
                project.Id, issue.Number, workflowRunId, sessionName, _runnerId, sessionName, "task", "Build", $"Dedup {name}"));

        return new CreatedSession(project.Id, workflowRunId, sessionName, session.Id);
    }

    private Task AppendEventsAsync(CreatedSession session, object body) =>
        _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/sessions/{session.ProjectId}/{session.WorkflowRunId}/{session.SessionName}/events",
            body);

    private Task AppendTerminalAsync(CreatedSession session, string status, int exitCode, string? failureReason = null) =>
        AppendEventsAsync(session, new
        {
            events = new[]
            {
                failureReason is null
                    ? (object)new { type = "agent_session_terminal", payload = new { status, exitCode } }
                    : new { type = "agent_session_terminal", payload = new { status, exitCode, failureReason } }
            }
        });

    private Task AppendLivenessAsync(CreatedSession session, string status) =>
        AppendEventsAsync(session, new
        {
            events = new[]
            {
                new { type = "agent_liveness_status", payload = new { status } }
            }
        });

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(string ProjectId, string WorkflowRunId, string SessionName, string Id);
}
