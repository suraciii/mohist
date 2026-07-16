using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// AgentSession producer. Covers issue-361 T-005 scenarios: session
/// state and emitted lifecycle events commit atomically; an event-row
/// write failure rolls back the state transaction and propagates (no
/// swallow-InvalidOperationException or log-and-swallow catch remains);
/// durable event rows survive a crash-after-commit and remain readable
/// on a fresh <c>DbContext</c>. Lifecycle events are stamped with
/// <c>subject = session id</c> and the <c>/mohist/agent-session/{id}</c>
/// source, and per design.md#OQ1 all six lifecycle types are persisted
/// (no filtering). Also covers issue-412 T-005 lineage stamping: every
/// <c>agent-session.*</c> envelope carries <c>projectid</c> and
/// <c>sessionid</c> from the session's own <c>Metadata.Labels</c>;
/// agent-origin sessions additionally stamp <c>agentid</c>;
/// workflow/issue-origin sessions stamp <c>issue</c>, <c>workflowrunid</c>,
/// and <c>stage</c>; absent affiliations are omitted, never an empty
/// value (D6); and every emitted envelope satisfies the catalog's
/// declared required lineage attributes via the conformance helper.
/// </summary>
[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class AgentSessionTransactionalEventAppendSpecs : IAsyncLifetime
{
    private static readonly DateTime FixedTime = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly NullDispatchGrainFactory _grainFactory = new();
    private EventStore _eventStore = null!;

    public AgentSessionTransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=agent-session-transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbFactory = new Factory(_options);

        MigratedSqliteTemplate.CopyTo(_keeper);
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndLifecycleEventRowsTogether()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_ok");

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        Assert.Contains(stored, s => s.Envelope.Type == EventCatalog.ReverseDns.AgentSessionUsageRecorded);

        var loaded = await store.LoadAsync("agent_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("agent_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        var store = new AgentSessionStore(_dbFactory, new ThrowingEventStore(), _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_fail");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(session.Id, session, [
                new AgentSessionRuntimeBound("acp-1", null),
                new AgentSessionUsageRecorded(new AgentUsageSummary()),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.AgentSessionEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_LifecycleEventRowsRemainDurableOnFreshDbContext()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_crash");

        await store.SaveAsync(session.Id, session, [new AgentSessionRuntimeBound("acp-1", null)]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.AgentSessionEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/agent-session/agent_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(EventCatalog.ReverseDns.AgentSessionRuntimeBound, row.Type);
        Assert.Null(row.DispatchedAt);
        Assert.Equal("agent_txn_crash", row.Subject);

        var sessionRow = Assert.Single(await freshDb.AgentSessions.AsNoTracking()
            .Where(r => r.Id == "agent_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(sessionRow.State));
    }

    [Fact]
    public async Task SaveAsync_StampsSourceAndSubjectOnEveryLifecycleEnvelope()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_identity");

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
            new AgentSessionModelChanged("anthropic/claude"),
            new AgentSessionContextCompacted(null, null, null, "summary", "summary text", TestTime.UtcDateTime),
            new AgentSessionContextExhausted("context_exhaustion", 96d, 960, 1000, TestTime.UtcDateTime),
            new AgentSessionContextHealthUpdated("yellow", 65d, 650, 1000, TestTime.UtcDateTime),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_identity");
        Assert.Equal(6, stored.Count);
        foreach (var entry in stored)
        {
            Assert.Equal("/mohist/agent-session/agent_txn_identity", entry.Envelope.Source.ToString());
            Assert.Equal("agent_txn_identity", entry.Envelope.Subject);
        }
    }

    [Fact]
    public async Task SaveAsync_AgentLaunchSession_StampsProjectIdSessionIdAndAgentId()
    {
        // T-005 / D6: an agent-launch session whose labels carry project
        // id, agent id, and the agent source kind stamps projectid,
        // sessionid, and agentid on every agent-session.* envelope. The
        // stamp source is the session's own Metadata.Labels — no
        // cross-aggregate query.
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_agent_launch", BuildAgentLaunchLabels(
            projectId: "proj_agent_launch",
            agentId: "agent_lineage_1"));

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_agent_launch");
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.Equal("proj_agent_launch", entry.Envelope.Extensions["projectid"]);
            Assert.Equal("agent_txn_agent_launch", entry.Envelope.Extensions["sessionid"]);
            Assert.Equal("agent_lineage_1", entry.Envelope.Extensions["agentid"]);
        }
    }

    [Fact]
    public async Task SaveAsync_AgentLaunchSessionWithIssueContext_OmitsIssueLineage()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_agent_launch_issue", BuildAgentLaunchLabels(
            projectId: "proj_agent_launch",
            agentId: "agent_lineage_1",
            issueNumber: 42));

        await store.SaveAsync(session.Id, session, [new AgentSessionRuntimeBound("acp-1", null)]);

        var stored = Assert.Single(await _eventStore.ListAgentSessionEventsAsync(session.Id));
        Assert.False(stored.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Issue));
        Assert.False(stored.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.WorkflowRunId));
        Assert.False(stored.Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Stage));
    }

    [Fact]
    public async Task SaveAsync_WorkflowOriginSession_StampsIssueWorkflowRunIdAndStageFromLabels()
    {
        // T-005 / D6: a workflow-origin session whose labels carry the
        // issue number, workflow run id, and stage name additionally
        // stamps issue, workflowrunid, and stage — but never agentid,
        // which is reserved for agent-launch sessions.
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_workflow", BuildWorkflowOriginLabels(
            projectId: "proj_workflow_origin",
            workflowRunId: "wr_lineage_42",
            issueNumber: 42,
            stage: "build"));

        await store.SaveAsync(session.Id, session, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
        ]);

        var stored = await _eventStore.ListAgentSessionEventsAsync("agent_txn_workflow");
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.Equal("proj_workflow_origin", entry.Envelope.Extensions["projectid"]);
            Assert.Equal("agent_txn_workflow", entry.Envelope.Extensions["sessionid"]);
            Assert.Equal("42", entry.Envelope.Extensions["issue"]);
            Assert.Equal("wr_lineage_42", entry.Envelope.Extensions["workflowrunid"]);
            Assert.Equal("build", entry.Envelope.Extensions["stage"]);
            Assert.False(entry.Envelope.Extensions.ContainsKey("agentid"));
        }
    }

    [Fact]
    public async Task SaveAsync_WorkflowOriginSessionWithoutIssueNumber_OmitsIssueKey()
    {
        // T-005 / D6: absent affiliation is omitted, never an empty
        // value. A workflow session whose labels lack the issue-number
        // label does NOT stamp `issue` (the key is entirely absent).
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_workflow_no_issue", BuildWorkflowOriginLabels(
            projectId: "proj_workflow_no_issue",
            workflowRunId: "wr_no_issue",
            issueNumber: null,
            stage: "build"));

        await store.SaveAsync(session.Id, session, [new AgentSessionRuntimeBound("acp-1", null)]);

        var stored = Assert.Single(await _eventStore.ListAgentSessionEventsAsync("agent_txn_workflow_no_issue"));
        Assert.False(stored.Envelope.Extensions.ContainsKey("issue"));
        Assert.Equal("wr_no_issue", stored.Envelope.Extensions["workflowrunid"]);
        Assert.Equal("build", stored.Envelope.Extensions["stage"]);
    }

    [Fact]
    public async Task SaveAsync_SessionWithoutProjectIdLabel_FailsBecauseProjectOwnershipIsRequired()
    {
        // T-005 / D6: a session whose Metadata.Labels is empty (or
        // carries no project-id label) does NOT stamp projectid.
        // sessionid is still stamped from the session's own id; absent
        // affiliation is omitted, never an empty value.
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_no_project", new AgentSessionMetadata());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(session.Id, session, [new AgentSessionRuntimeBound("acp-1", null)]));

        Assert.Contains("project-id", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_StampedEnvelopes_CarrySessionProducerContext()
    {
        // Drives both an agent-launch and a workflow-origin session through
        // the production path. Each producer stamps its own session context.
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);

        var agentLaunch = BuildSession("agent_txn_conformance_agent", BuildAgentLaunchLabels(
            projectId: "proj_conf_agent",
            agentId: "agent_conf"));
        await store.SaveAsync(agentLaunch.Id, agentLaunch, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionUsageRecorded(new AgentUsageSummary()),
            new AgentSessionContextHealthUpdated("green", 40d, 400, 1000, FixedTime),
        ]);

        var workflowOrigin = BuildSession("agent_txn_conformance_workflow", BuildWorkflowOriginLabels(
            projectId: "proj_conf_workflow",
            workflowRunId: "wr_conf",
            issueNumber: 7,
            stage: "review"));
        await store.SaveAsync(workflowOrigin.Id, workflowOrigin, [
            new AgentSessionRuntimeBound("acp-1", null),
            new AgentSessionContextExhausted("context_exhaustion", 96d, 960, 1000, FixedTime),
        ]);

        var agentEvents = await _eventStore.ListAgentSessionEventsAsync("agent_txn_conformance_agent");
        var workflowEvents = await _eventStore.ListAgentSessionEventsAsync("agent_txn_conformance_workflow");
        Assert.Equal(3, agentEvents.Count);
        Assert.Equal(2, workflowEvents.Count);

        foreach (var entry in agentEvents.Concat(workflowEvents))
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId));
            Assert.False(string.IsNullOrWhiteSpace(projectId));
            Assert.True(entry.Envelope.Extensions.TryGetValue(EventCatalog.Lineage.SessionId, out var sessionId));
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
        }
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_state_only", new AgentSessionMetadata());

        await store.SaveAsync(session.Id, session, []);

        var loaded = await store.LoadAsync("agent_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAgentSessionEventsAsync("agent_txn_state_only"));
    }

    [Fact]
    public async Task SaveAsync_OnlyNullEventsWithoutProjectLabel_StillCommitsStateRow()
    {
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_state_only_no_project", new AgentSessionMetadata());

        await store.SaveAsync(session.Id, session, [null!]);

        Assert.NotNull(await store.LoadAsync(session.Id));
        Assert.Empty(await _eventStore.ListAgentSessionEventsAsync(session.Id));
    }

    [Fact]
    public async Task SaveAsync_NoEventsOverload_RemainsForCallerWithNoPendingEvents()
    {
        // The grain takes the events-aware overload when there are
        // pending events, but the no-events overload of SaveAsync must
        // still exist for callers that haven't recorded any domain
        // events (e.g. an OpenAsync that does not transition). It
        // continues to commit the state row cleanly.
        var store = new AgentSessionStore(_dbFactory, _eventStore, _grainFactory, NullLogger<AgentSessionStore>.Instance);
        var session = BuildSession("agent_txn_no_events_path");

        await store.SaveAsync(session.Id, session);

        var loaded = await store.LoadAsync("agent_txn_no_events_path");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAgentSessionEventsAsync("agent_txn_no_events_path"));
    }

    private static AgentSession BuildSession(string id, AgentSessionMetadata? metadata = null)
    {
        var session = new AgentSession
        {
            Id = id,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
            Metadata = metadata ?? new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = "proj_default_session",
            }),
        };
        session.Status = session.Status with
        {
            CreatedAt = TestTime.UtcDateTime,
            LastDataAt = TestTime.UtcDateTime,
        };
        return session;
    }

    private static AgentSessionMetadata BuildAgentLaunchLabels(string projectId, string agentId, int? issueNumber = null) =>
        GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
            ProjectId: projectId,
            AgentId: agentId,
            AgentName: $"{agentId}-name",
            IssueNumber: issueNumber));

    private static AgentSessionMetadata BuildWorkflowOriginLabels(
        string projectId,
        string workflowRunId,
        int? issueNumber,
        string? stage) =>
        WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext(
            ProjectId: projectId,
            WorkflowRunId: workflowRunId,
            SessionName: "sess-name",
            IssueNumber: issueNumber,
            Stage: stage));

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in for transactional
    /// unit specs. The dispatcher is a no-op grain reference; producers
    /// only need to call DispatchNowAsync without exceptions. Lets the
    /// store exercise its post-commit poke code path without spinning up
    /// an Orleans silo.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
    /// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
    /// Lets the post-commit poke fire without an Orleans silo.
    /// </summary>
    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
    }

    /// <summary>
    /// <see cref="IEventStore"/> that throws on the second append in a
    /// save transaction, simulating an event-row write failure (e.g.
    /// constraint violation). Used to verify that the store does NOT
    /// swallow the exception and that the session state transaction
    /// is rolled back. Mirrors the WorkflowRunStore and IssueStore
    /// equivalents from T-003/T-004.
    /// </summary>
    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2)
            {
                throw new InvalidOperationException("simulated event write failed");
            }
            await db.AgentSessionEvents.AddAsync(new AgentSessionEventRow
            {
                Id = _callCount,
                Source = envelope.Source.ToString(),
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson = "{}",
            }, ct);
        }

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
