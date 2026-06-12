using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services.Sessions;
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
    public async Task AttachPhysicalSessionAsync_PublishesRuntimeBoundExactlyOnce()
    {
        var session = await CreateSessionWithoutAttachAsync("dedup-attach-started");

        _fixture.RecordingPublisher.Clear();

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
            new { agentSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "after attach" } },
            }
        });

        var bound = Assert.Single(_fixture.RecordingPublisher.Published,
            p => p.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        Assert.Equal("/mohist/agent-session/" + session.Id, bound.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AttachThenFirstRuntimeAppend_PublishesRuntimeBoundOnceAcrossRuntimeEvents()
    {
        var session = await CreateSessionWithoutAttachAsync("attach-append");

        _fixture.RecordingPublisher.Clear();

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
            new { agentSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "first runtime row" } },
            }
        });

        Assert.Equal(1, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_WithoutAttach_DoesNotPublishRuntimeBound()
    {
        // RuntimeBound means a real physical ACP session was attached. Plain
        // runtime events must not invent an ACP session id or emit it.
        var session = await CreateSessionWithoutAttachAsync("dedup-started");

        _fixture.RecordingPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "first" } },
                new { type = "message.delta", payload = new { text = "second" } },
                new { type = "tool_call.started", payload = new { toolCallId = "t-1", kind = "read", status = "in_progress" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "third" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "fourth" } },
            }
        });

        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType(EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_SessionClosedDoesNotPublishTerminalDomainEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-completed");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "after-terminal" } },
            }
        });

        Assert.Empty(_fixture.RecordingPublisher.Published);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_FailedClosedObservationDoesNotPublishFailedDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-failed");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "failed", exitCode: 1, failureReason: "boom");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        Assert.Empty(_fixture.RecordingPublisher.Published);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_CancelledClosedObservationDoesNotPublishCancelledDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-cancelled");

        _fixture.RecordingPublisher.Clear();

        await AppendTerminalAsync(session, status: "cancelled", exitCode: 0, failureReason: "user-cancel");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        Assert.Empty(_fixture.RecordingPublisher.Published);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_LivenessDoesNotPublishDomainStatusEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-liveness");

        _fixture.RecordingPublisher.Clear();

        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");

        Assert.Empty(_fixture.RecordingPublisher.Published);
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
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "thinking" } },
                new { type = "reasoning.delta", payload = new { content = new { text = "thought" } } },
                new { type = "tool_call.started", payload = new { toolCallId = "t-1", kind = "read", status = "in_progress" } },
                new { type = "ralph_task_update", payload = new { taskId = "rt-1", status = "in_progress" } },
                new { type = "ralph_loop_progress", payload = new { loop = 1, total = 5 } },
                new { type = "session.liveness", payload = new { status = "running" } },
                new { type = "usage.updated", payload = new { inputTokens = 10, outputTokens = 5 } },
                new { type = "model.resolved", payload = new { resolvedModel = "anthropic/claude" } },
            }
        });

        // None of the eight transcript runtime event types must ever
        // appear in the domain bus. They flow through ITranscriptEventPublisher
        // (out of scope for this recording publisher).
        Assert.Equal(0, _fixture.RecordingPublisher.Published.Count(p =>
            p.Type == "message.delta" ||
            p.Type == "reasoning.delta" ||
            p.Type == "tool_call.started" ||
            p.Type == "ralph_task_update" ||
            p.Type == "ralph_loop_progress" ||
            p.Type == "session.liveness" ||
            p.Type == "usage.updated" ||
            p.Type == "model.resolved"));
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
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "hello" } },
                new { type = "tool_call.started", payload = new { toolCallId = "t-1", toolName = "Read", status = "started" } },
                new { type = "tool_call.updated", payload = new { toolCallId = "t-1", toolName = "Read", status = "completed" } },
            }
        });

        Assert.Collection(
            _fixture.RecordingTranscriptPublisher.Published,
            first => Assert.Equal("message.delta", first.Type),
            second => Assert.Equal("tool_call.started", second.Type),
            third => Assert.Equal("tool_call.updated", third.Type));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_SessionClosedPublishesOnlyTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("dedup-terminal-publishes");

        _fixture.RecordingPublisher.Clear();
        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        Assert.Empty(_fixture.RecordingPublisher.Published);
        Assert.Single(_fixture.RecordingTranscriptPublisher.Published, p => p.Type == "session.closed");
        Assert.Equal(0, _fixture.RecordingPublisher.CountOfType("session.closed"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task AppendRuntimeEventsAsync_UsageAndModelEventsCarryReverseDnsType()
    {
        var session = await CreateStartedSessionAsync("dedup-bus-type");

        _fixture.RecordingPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            runtimeEvents = new object[]
            {
                new { type = "usage.updated", payload = new { inputTokens = 1 } },
                new { type = "model.resolved", payload = new { resolvedModel = "anthropic/claude" } },
            }
        });

        Assert.Contains(_fixture.RecordingPublisher.Published,
            p => p.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded
                 && p.Source == "/mohist/agent-session/" + session.Id);
        Assert.Contains(_fixture.RecordingPublisher.Published,
            p => p.Type == EventCatalog.ReverseDns.AgentSessionModelChanged
                 && p.Source == "/mohist/agent-session/" + session.Id);
    }

    private async Task<CreatedSession> CreateStartedSessionAsync(string name)
    {
        var session = await CreateSessionWithoutAttachAsync(name);

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
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
            .GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"))
            .OpenAsync(new OpenAgentSessionCommand(
                _runnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(project.Id, issue.Number, workflowRunId, sessionName, sessionName, "task", "Build", $"Dedup {name}")));

        return new CreatedSession(project.Id, workflowRunId, sessionName, session.Id);
    }

    private Task AppendEventsAsync(CreatedSession session, object body) =>
        _fixture.Client.PostOkAsync(RunnerAgentSessionRuntimeEventsPath(session),
            body);

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    private Task AppendTerminalAsync(CreatedSession session, string status, int exitCode, string? failureReason = null) =>
        AppendEventsAsync(session, new
        {
            runtimeEvents = new[]
            {
                failureReason is null
                    ? (object)new { type = "session.closed", payload = new { status, exitCode } }
                    : new { type = "session.closed", payload = new { status, exitCode, failureReason } }
            }
        });

    private Task AppendLivenessAsync(CreatedSession session, string status) =>
        AppendEventsAsync(session, new
        {
            runtimeEvents = new[]
            {
                new { type = "session.liveness", payload = new { status } }
            }
        });

    private static AgentSessionMetadata WorkflowSessionMetadata(
        string projectId,
        int issueNumber,
        string workflowRunId,
        string sessionName,
        string? workId,
        string? workType,
        string? stage,
        string? title) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkId, workId)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkType, workType)
            .WithLabel(AgentSessionQueryMetadataKeys.Stage, stage)
            .WithAnnotation(AgentSessionQueryMetadataKeys.Title, title);

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(string ProjectId, string WorkflowRunId, string SessionName, string Id);
}
