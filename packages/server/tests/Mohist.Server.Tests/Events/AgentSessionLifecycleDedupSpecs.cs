using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Events;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public sealed class AgentSessionLifecycleDedupSpecs
{
    private const string RunnerId = "event-dedup-runner";
    private readonly ComponentWorkflowGrainFixture _fixture;

    public AgentSessionLifecycleDedupSpecs(ComponentWorkflowGrainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AttachPhysicalSessionAsync_PersistsRuntimeBoundExactlyOnce()
    {
        var session = await CreateSessionWithoutAttachAsync("dedup-attach-started");
        var grain = Grain(session);

        await AttachAsync(session);
        await AppendEventsAsync(session, ("message.delta", new { text = "after attach" }));

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        var bound = Assert.Single(
            stored,
            item => item.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        Assert.Equal("/mohist/agent-session/" + session.Id, bound.Envelope.Source.ToString());
    }

    [Fact]
    public async Task AttachThenFirstRuntimeAppend_PersistsRuntimeBoundOnceAcrossRuntimeEvents()
    {
        var session = await CreateSessionWithoutAttachAsync("attach-append");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AttachAsync(session);
        await AppendEventsAsync(session, ("message.delta", new { text = "first runtime row" }));
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(
            1,
            stored.Count(item => item.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_WithoutAttach_DoesNotPersistRuntimeBound()
    {
        var session = await CreateSessionWithoutAttachAsync("dedup-started");

        await AppendEventsAsync(
            session,
            ("message.delta", new { text = "first" }),
            ("message.delta", new { text = "second" }),
            ("tool_call.started", new { toolCallId = "t-1", kind = "read", status = "in_progress" }));
        await AppendEventsAsync(session, ("message.delta", new { text = "third" }));
        await AppendEventsAsync(session, ("message.delta", new { text = "fourth" }));

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(
            0,
            stored.Count(item => item.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalActivityIsDeliveryIdempotentWithoutDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-completed");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendTerminalAsync(session, "completed", 0);
        await AppendTerminalAsync(session, "completed", 0);
        await AppendEventsAsync(session, ("message.delta", new { text = "after-terminal" }));
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, item => Assert.NotEqual("session.activity", item.Envelope.Type));

        await using var db = new MohistDbContext(_fixture.DbOptions);
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
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendTerminalAsync(session, "failed", 1, "boom");
        await AppendTerminalAsync(session, "completed", 0);
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, item => Assert.NotEqual("session.activity", item.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_CancelledClosedObservationDoesNotPersistCancelledDomainEvent()
    {
        var session = await CreateStartedSessionAsync("dedup-cancelled");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendTerminalAsync(session, "cancelled", 0, "user-cancel");
        await AppendTerminalAsync(session, "completed", 0);
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, item => Assert.NotEqual("session.activity", item.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_LivenessDoesNotPersistDomainStatusEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-liveness");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendLivenessAsync(session);
        await AppendLivenessAsync(session);
        await AppendLivenessAsync(session);
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.All(stored, item => Assert.NotEqual("session.liveness", item.Envelope.Type));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_TranscriptRows_DoNotPersistAsDomainEvents()
    {
        var session = await CreateStartedSessionAsync("dedup-transcript");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendEventsAsync(
            session,
            ("message.delta", new { text = "thinking" }),
            ("reasoning.delta", new { content = new { text = "thought" } }),
            ("tool_call.started", new { toolCallId = "t-1", kind = "read", status = "in_progress" }),
            ("ralph_task_update", new { taskId = "rt-1", status = "in_progress" }),
            ("ralph_loop_progress", new { loop = 1, total = 5 }),
            ("session.liveness", new { status = "running" }),
            ("usage.updated", new { inputTokens = 10, outputTokens = 5 }),
            ("model.resolved", new { resolvedModel = "anthropic/claude" }));
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(
            0,
            stored.Count(item => item.Envelope.Type is
                "message.delta" or
                "reasoning.delta" or
                "tool_call.started" or
                "ralph_task_update" or
                "ralph_loop_progress" or
                "session.liveness" or
                "usage.updated" or
                "model.resolved"));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_RunnerTranscriptRows_PublishToTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("runner-transcript-push");
        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendEventsAsync(
            session,
            ("message.delta", new { text = "hello" }),
            ("tool_call.started", new { toolCallId = "t-1", toolName = "Read", status = "started" }),
            ("tool_call.updated", new { toolCallId = "t-1", toolName = "Read", status = "completed" }));

        Assert.Collection(
            _fixture.RecordingTranscriptPublisher.Published,
            first => Assert.Equal("message.delta", first.Type),
            second => Assert.Equal("tool_call.started", second.Type),
            third => Assert.Equal("tool_call.updated", third.Type));
        Assert.All(
            _fixture.RecordingTranscriptPublisher.ProjectIds,
            projectId => Assert.Equal(session.ProjectId, projectId));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_TerminalActivityPublishesOnlyTranscriptChannel()
    {
        var session = await CreateStartedSessionAsync("dedup-terminal-publishes");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        _fixture.RecordingTranscriptPublisher.Clear();

        await AppendEventsAsync(
            session,
            ("session.activity", new { activity = "idle", status = "completed", operationId = "op-dedup" }));

        Assert.Single(
            _fixture.RecordingTranscriptPublisher.Published,
            item => item.Type == "session.activity");
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Equal(0, stored.Count(item => item.Envelope.Type == "session.activity"));
    }

    [Fact]
    public async Task AppendRuntimeEventsAsync_UsageAndModelEventsPersistReverseDnsType()
    {
        var session = await CreateStartedSessionAsync("dedup-bus-type");
        var grain = Grain(session);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);

        await AppendEventsAsync(
            session,
            ("usage.updated", new { inputTokens = 1 }),
            ("model.resolved", new { resolvedModel = "anthropic/claude" }));
        await persistence.WaitAsync();

        var stored = await _fixture.EventStore.ListAgentSessionEventsAsync(session.Id);
        Assert.Contains(
            stored,
            item => item.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded
                && item.Envelope.Source.ToString() == "/mohist/agent-session/" + session.Id);
        Assert.Contains(
            stored,
            item => item.Envelope.Type == EventCatalog.ReverseDns.AgentSessionModelChanged
                && item.Envelope.Source.ToString() == "/mohist/agent-session/" + session.Id);
    }

    private IAgentSessionGrain Grain(CreatedSession session) =>
        _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);

    private async Task AttachAsync(CreatedSession session)
    {
        await Grain(session).AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            session.Id,
            WorkDir: "/tmp",
            ProcessPid: 4321));
    }

    private async Task<CreatedSession> CreateStartedSessionAsync(string name)
    {
        var session = await CreateSessionWithoutAttachAsync(name);
        await AttachAsync(session);
        return session;
    }

    private async Task<CreatedSession> CreateSessionWithoutAttachAsync(string name)
    {
        var projectId = $"dedup-project-{Guid.NewGuid():N}";
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var sessionId = $"dedup-session-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId,
                "opencode",
                Metadata: WorkflowSessionMetadata(projectId, workflowRunId, name, sessionId)));
        return new CreatedSession(projectId, workflowRunId, name, sessionId);
    }

    private async Task AppendEventsAsync(
        CreatedSession session,
        params (string Type, object Payload)[] events)
    {
        var inputs = events
            .Select(item => new AgentSessionRuntimeEventInput(
                item.Type,
                JsonSerializer.Serialize(item.Payload, CloudEvent.JsonOptions)))
            .ToArray();
        await Grain(session).AppendRuntimeEventsAsync(
            new AppendAgentSessionRuntimeEventsCommand(inputs, session.Id));
    }

    private Task AppendTerminalAsync(
        CreatedSession session,
        string status,
        int exitCode,
        string? failureReason = null) =>
        AppendEventsAsync(
            session,
            ("session.activity", failureReason is null
                ? new { activity = "idle", status, exitCode, operationId = "terminal-delivery" }
                : new { activity = "idle", status, exitCode, failureReason, operationId = "terminal-delivery" }));

    private Task AppendLivenessAsync(CreatedSession session) =>
        AppendEventsAsync(session, ("session.liveness", new { status = "running" }));

    private static AgentSessionMetadata WorkflowSessionMetadata(
        string projectId,
        string workflowRunId,
        string sessionName,
        string sessionId) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, "1")
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
            .WithLabel("mohist.io/agent-id", "agent-1")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkId, sessionId)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkType, "task")
            .WithLabel(AgentSessionQueryMetadataKeys.Stage, "build")
            .WithAnnotation(AgentSessionQueryMetadataKeys.Title, sessionName);

    private sealed record CreatedSession(
        string ProjectId,
        string WorkflowRunId,
        string SessionName,
        string Id);
}
