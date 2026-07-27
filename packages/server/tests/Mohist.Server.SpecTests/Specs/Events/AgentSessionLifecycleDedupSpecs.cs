using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Collection("EventPublishing")]
public class AgentSessionLifecycleDedupSpecs
{
    private readonly EventPublishingIntegrationFixture _fixture;
    private readonly string _runnerId = $"dedup-runner-{Guid.NewGuid():N}";

    public AgentSessionLifecycleDedupSpecs(EventPublishingIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AttachPhysicalSessionAsync_PersistsRuntimeBoundExactlyOnce()
    {
        var session = await CreateSessionWithoutAttachAsync("dedup-attach-started");

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
            new { runtimeSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "after attach" } },
            }
        });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        var bound = Assert.Single(
            stored,
            s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        Assert.Equal("/mohist/agent-session/" + session.Id, bound.Envelope.Source.ToString());
    }

    [Fact]
    public async Task AttachThenFirstRuntimeAppend_PersistsRuntimeBoundOnceAcrossRuntimeEvents()
    {
        var session = await CreateSessionWithoutAttachAsync("attach-append");

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
            new { runtimeSessionId = session.Id, workDir = "/tmp", processPid = 4321 });
        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "first runtime row" } },
            }
        });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(1, stored.Count(s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_WithoutAttach_DoesNotPersistRuntimeBound()
    {
        // RuntimeBound means a real physical ACP session was attached. Plain
        // runtime events must not invent an ACP session id or emit it.
        var session = await CreateSessionWithoutAttachAsync("dedup-started");

        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "first" } },
                new { type = "message.delta", payload = new { text = "second" } },
                new { type = "tool_call.started", payload = new { toolCallId = "t-1", kind = "read", status = "in_progress" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "third" } },
            }
        });
        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "fourth" } },
            }
        });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(0, stored.Count(s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalActivityIsDeliveryIdempotentWithoutDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-completed");

        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);
        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "message.delta", payload = new { text = "after-terminal" } },
            }
        });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, s => Assert.NotEqual("session.activity", s.Envelope.Type));

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var parts = await (
            from part in db.AgentSessionTranscriptParts
            join turn in db.AgentSessionTranscriptTurns on part.TurnId equals turn.Id
            where turn.SessionId == session.Id && part.Type == TranscriptPartTypes.SessionActivity
            select part)
            .ToListAsync();
        var activity = Assert.Single(parts);
        Assert.Equal(2, activity.RawEventCount);
        Assert.Equal("completed", activity.PayloadStatus);
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_FailedClosedObservationDoesNotPersistFailedDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-failed");

        await AppendTerminalAsync(session, status: "failed", exitCode: 1, failureReason: "boom");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, s => Assert.NotEqual("session.activity", s.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_CancelledClosedObservationDoesNotPersistCancelledDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-cancelled");

        await AppendTerminalAsync(session, status: "cancelled", exitCode: 0, failureReason: "user-cancel");
        await AppendTerminalAsync(session, status: "completed", exitCode: 0);

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, s => Assert.NotEqual("session.activity", s.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_LivenessDoesNotPersistDomainStatusEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-liveness");

        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");
        await AppendLivenessAsync(session, status: "running");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        // Only the RuntimeBound event from the attach may be present.
        Assert.All(stored, s => Assert.NotEqual("session.liveness", s.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_TranscriptRows_DoNotPersistAsDomainEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-transcript");

        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
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

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();

        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        // None of the eight transcript runtime event types must ever
        // appear as persisted domain events. They flow through
        // ITranscriptEventPublisher (out of scope for the event store).
        Assert.Equal(0, stored.Count(s =>
            s.Envelope.Type == "message.delta" ||
            s.Envelope.Type == "reasoning.delta" ||
            s.Envelope.Type == "tool_call.started" ||
            s.Envelope.Type == "ralph_task_update" ||
            s.Envelope.Type == "ralph_loop_progress" ||
            s.Envelope.Type == "session.liveness" ||
            s.Envelope.Type == "usage.updated" ||
            s.Envelope.Type == "model.resolved"));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_RunnerTranscriptRows_PublishToTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("runner-transcript-push");

        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
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

    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalActivityPublishesOnlyTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("dedup-terminal-publishes");

        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                new { type = "session.activity", payload = new { activity = "idle", status = "completed", operationId = "op-dedup" } }
            }
        });

        Assert.Single(_fixture.RecordingTranscriptPublisher.Published, p => p.Type == "session.activity");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();
        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(0, stored.Count(s => s.Envelope.Type == "session.activity"));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_UsageAndModelEventsPersistReverseDnsType()
    {
        var session = await CreateStartedSessionAsync("dedup-bus-type");

        await AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
            runtimeEvents = new object[]
            {
                new { type = "usage.updated", payload = new { inputTokens = 1 } },
                new { type = "model.resolved", payload = new { resolvedModel = "anthropic/claude" } },
            }
        });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        await grain.FlushForTestAsync();
        var eventStore = _fixture.Services.GetRequiredService<IEventStore>();
        var stored = await eventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Contains(stored,
            s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded
                 && s.Envelope.Source.ToString() == "/mohist/agent-session/" + session.Id);
        Assert.Contains(stored,
            s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionModelChanged
                 && s.Envelope.Source.ToString() == "/mohist/agent-session/" + session.Id);
    }

    private async Task<CreatedSession> CreateStartedSessionAsync(string name)
    {
        var session = await CreateSessionWithoutAttachAsync(name);

        await _fixture.Client.PostOkAsync(RunnerAgentSessionAttachPath(session),
            new { runtimeSessionId = session.Id, workDir = "/tmp", processPid = 4321 });

        return session;
    }

    private async Task<CreatedSession> CreateSessionWithoutAttachAsync(string name)
    {
        var projectName = $"dedup-{name}-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _fixture.Client.PostOkAsync($"/api/projects/{project.Id}/repositories",
            new { name = "main", gitUrl = "https://example.com/repo.git", baseBranch = "main", setDefault = true });
        var issue = await _fixture.Client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues",
            new { title = $"Dedup {name}", body = "track lifecycle emits", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id });

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
            runtimeSessionId = session.Id,
            runtimeEvents = new[]
            {
                failureReason is null
                    ? (object)new { type = "session.activity", payload = new { activity = "idle", status, exitCode, operationId = "terminal-delivery" } }
                    : new { type = "session.activity", payload = new { activity = "idle", status, exitCode, failureReason, operationId = "terminal-delivery" } }
            }
        });

    private Task AppendLivenessAsync(CreatedSession session, string status) =>
        AppendEventsAsync(session, new
        {
            runtimeSessionId = session.Id,
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

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(string ProjectId, string WorkflowRunId, string SessionName, string Id);
}
