using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// WorkflowRun producer. Covers issue-361 T-003 atomicity scenarios and
/// issue-412 T-002 lineage scenarios: state and event rows commit
/// atomically; event-row write failures roll back the state transaction
/// and are not swallowed; event rows survive a crash-after-commit and
/// remain readable on a fresh <c>DbContext</c>; events stamp
/// <c>workflowrunid</c> always plus <c>projectid</c>/<c>issue</c> when the
/// run's metadata annotations carry them; stage,
/// task, check, and feedback-requested events additionally stamp
/// <c>stage</c> from structural inspection of the union variant (D2);
/// every emitted envelope satisfies the WorkflowRun producer-family rule.
/// </summary>
public class TransactionalEventAppendSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_txn";
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly NullDispatchGrainFactory _grainFactory = new();
    private EventStore _eventStore = null!;

    public TransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Epics.Add(new EpicRow
        {
            ProjectId = ProjectId,
            Number = 1,
            Title = "Epic 1",
            Description = "",
            Priority = "p2",
            Status = "running",
            CreatedAt = FixedTime,
            UpdatedAt = FixedTime,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndEventRowsTogether()
    {
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_ok", includeAnnotations: true);

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunCompleted(),
        ]);

        // Both rows visible on a fresh DbContext (i.e. they were
        // committed atomically with the state row, not staged on a
        // throwaway context).
        var stored = await _eventStore.ListAsync("wr_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.started");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.completed");

        var loaded = await store.LoadAsync("wr_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("wr_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        // ThrowingEventStore rejects the second AppendAsync call so the
        // save transaction fails mid-way. The exception must propagate
        // out of SaveAsync, and neither the state row nor any event row
        // may be persisted — there is no bare catch to swallow the
        // failure.
        var store = new WorkflowRunStore(_dbFactory, new ThrowingEventStore(), _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_fail", includeAnnotations: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(run, [
                new WorkflowRunStarted(),
                new WorkflowRunCompleted(),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.WorkflowRuns.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.WorkflowRunEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_EventRowsRemainDurableOnFreshDbContext()
    {
        // Crash-after-commit simulation: open the save transaction,
        // commit it, then re-open the database on a brand new DbContext
        // and assert the event rows are still there. No post-commit
        // publish loop remains in the store; the only durable artefact
        // is the committed row.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_crash", includeAnnotations: true);

        await store.SaveAsync(run, [new WorkflowRunCompleted()]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/workflow-runs/wr_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("com.mohist.workflow.run.completed", row.Type);
        Assert.Null(row.DispatchedAt);

        var runRow = Assert.Single(await freshDb.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowRunId == "wr_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(runRow.State));
    }

    [Fact]
    public async Task SaveAsync_RunBoundToIssue_StampsProjectIdIssueAndWorkflowRunIdOnExtensions()
    {
        // Identity stamping at write time: the run's
        // Annotations["projectId"] / ["issueNumber"] flow onto
        // every emitted WorkflowRun event's extensions, alongside the
        // always-stamped workflowrunid (the run itself is the producer).
        // A consumer can read issue lineage directly from extensions without
        // doing a reverse database lookup, and Expression subscription can
        // match on the unified `issue` key (D3 / T-002).
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_identity", includeAnnotations: true);

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunCompleted(),
        ]);

        var stored = await _eventStore.ListAsync("wr_txn_identity");
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
            Assert.Equal(ProjectId, stampedProjectId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issue", out var stampedIssue));
            Assert.Equal("1", stampedIssue);
            Assert.True(entry.Envelope.Extensions.TryGetValue("workflowrunid", out var stampedRunId));
            Assert.Equal("wr_txn_identity", stampedRunId);
            Assert.False(entry.Envelope.Extensions.ContainsKey("epic"));
        }
    }

    [Fact]
    public async Task SaveAsync_EpicAffiliatedRun_StampsEpicOnRunStageTaskAndCheckEvents()
    {
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_epic", includeAnnotations: true, epicNumber: 1);

        WorkflowEvent[] events = [
            new WorkflowRunStarted(),
            new StageStarted("build"),
            new TaskStarted("build", "task_1", "worker_1"),
            new CheckPassed("build", "lint", null),
        ];
        await store.SaveAsync(run, events);

        var stored = await _eventStore.ListAsync("wr_txn_epic");
        Assert.Equal(4, stored.Count);
        for (var i = 0; i < events.Length; i++)
        {
            var entry = stored[i];
            Assert.Equal("1", entry.Envelope.Extensions["epic"]);
            ProducerConformance.Assert(
                EventProducerFamily.WorkflowRun,
                entry.Envelope.Extensions,
                WorkflowContext(run, events[i]));
        }
    }

    [Fact]
    public async Task SaveAsync_RunWithoutIssueAnnotation_OmitsIssueAttributesButKeepsProjectIdAndWorkflowRunId()
    {
        // A WorkflowRun that is not bound to an issue (e.g. a workflow
        // started by an ad-hoc API call without an issue context) must
        // NOT stamp a phantom issue key — that extension is
        // conditional on the annotations being present. Project identity
        // and the always-stamped workflowrunid remain on the envelope
        // so routing on the run itself still works.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = new WorkflowRun
        {
            Id = "wr_txn_unbound",
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunCompleted()]);

        var stored = Assert.Single(await _eventStore.ListAsync("wr_txn_unbound"));
        Assert.True(stored.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
        Assert.Equal(ProjectId, stampedProjectId);
        Assert.True(stored.Envelope.Extensions.TryGetValue("workflowrunid", out var stampedRunId));
        Assert.Equal("wr_txn_unbound", stampedRunId);
        Assert.False(stored.Envelope.Extensions.ContainsKey("issue"));
        Assert.False(stored.Envelope.Extensions.ContainsKey("epic"));
        ProducerConformance.Assert(
            EventProducerFamily.WorkflowRun,
            stored.Envelope.Extensions,
            new(ProjectId: ProjectId, WorkflowRunId: "wr_txn_unbound"));
    }

    [Fact]
    public async Task SaveAsync_StageTaskCheckAndFeedbackEvents_StampStageInAdditionToWorkflowLineage()
    {
        // D2: stage carriage is decided structurally by whether the
        // unwrapped WorkflowEvent union variant exposes a Stage member,
        // NOT by the bus-type prefix. Stages, stage approvals, feedback
        // requested, tasks, and checks all carry the stage name onto
        // the envelope, alongside the workflow.* lineage (workflowrunid
        // + the conditionally-present projectid/issue).
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_stage", includeAnnotations: true);

        WorkflowEvent[] events = [
            new StageStarted("build"),
            new StageCompleted("build"),
            new StageFailed("review", "boom"),
            new StageApprovalRequested("merge"),
            new StageApprovalResolved("merge", ApprovalResult.Approved),
            new FeedbackRequested("code-review", "fb_1", null),
            new TaskStarted("build", "task_a", "worker_a"),
            new TaskCompleted("build", "task_a"),
            new TaskFailed("build", "task_a", "boom"),
            new CheckPassed("review", "lint", null),
            new CheckFailed("review", "lint", "boom"),
            new CheckPending("review", "lint", null),
        ];
        await store.SaveAsync(run, events);

        var stored = await _eventStore.ListAsync("wr_txn_stage");
        Assert.Equal(12, stored.Count);
        for (var i = 0; i < events.Length; i++)
        {
            var entry = stored[i];
            Assert.True(entry.Envelope.Extensions.TryGetValue("workflowrunid", out _));
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out _));
            Assert.True(entry.Envelope.Extensions.TryGetValue("stage", out var stampedStage));
            Assert.False(string.IsNullOrEmpty(stampedStage));
            ProducerConformance.Assert(
                EventProducerFamily.WorkflowRun,
                entry.Envelope.Extensions,
                WorkflowContext(run, events[i]));
        }

        var byType = stored.ToDictionary(s => s.Envelope.Type);
        Assert.Equal("build", byType["com.mohist.workflow.stage.started"].Envelope.Extensions["stage"]);
        Assert.Equal("build", byType["com.mohist.workflow.stage.completed"].Envelope.Extensions["stage"]);
        Assert.Equal("review", byType["com.mohist.workflow.stage.failed"].Envelope.Extensions["stage"]);
        Assert.Equal("merge", byType["com.mohist.workflow.stage.approval-requested"].Envelope.Extensions["stage"]);
        Assert.Equal("merge", byType["com.mohist.workflow.stage.approval-resolved"].Envelope.Extensions["stage"]);
        Assert.Equal("code-review", byType["com.mohist.workflow.feedback.requested"].Envelope.Extensions["stage"]);
        Assert.Equal("build", byType["com.mohist.workflow.task.started"].Envelope.Extensions["stage"]);
        Assert.Equal("build", byType["com.mohist.workflow.task.completed"].Envelope.Extensions["stage"]);
        Assert.Equal("build", byType["com.mohist.workflow.task.failed"].Envelope.Extensions["stage"]);
        Assert.Equal("review", byType["com.mohist.workflow.check.passed"].Envelope.Extensions["stage"]);
        Assert.Equal("review", byType["com.mohist.workflow.check.failed"].Envelope.Extensions["stage"]);
        Assert.Equal("review", byType["com.mohist.workflow.check.pending"].Envelope.Extensions["stage"]);
    }

    [Fact]
    public async Task SaveAsync_WorkflowArtifactRecordedEvent_DoesNotStampStage()
    {
        // D2: WorkflowArtifactRecorded carries no Stage member; even though
        // its bus type is workflow.*, it MUST NOT receive a `stage` stamp.
        // The base workflow.* lineage (projectid + workflowrunid) is kept.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_artifact", includeAnnotations: true);

        await store.SaveAsync(run, [
            new WorkflowArtifactRecorded("wr_txn_artifact", "task_a", "logs/build.log", FixedTime),
        ]);

        var stored = Assert.Single(await _eventStore.ListAsync("wr_txn_artifact"));
        Assert.Equal("com.mohist.workflow.artifact.recorded", stored.Envelope.Type);
        Assert.True(stored.Envelope.Extensions.TryGetValue("projectid", out _));
        Assert.True(stored.Envelope.Extensions.TryGetValue("workflowrunid", out _));
        Assert.False(stored.Envelope.Extensions.ContainsKey("stage"));
        ProducerConformance.Assert(
            EventProducerFamily.WorkflowRun,
            stored.Envelope.Extensions,
            WorkflowContext(run, new WorkflowArtifactRecorded("wr_txn_artifact", "task_a", "logs/build.log", FixedTime)));
    }

    [Fact]
    public async Task SaveAsync_NoProjectAnnotation_FailsBecauseProjectOwnershipIsRequired()
    {
        // A run that has no metadata at all (defensive) still has a
        // workflowrunid stamp because the run itself IS the producer,
        // but conditional affiliations (projectid/issue/stage)
        // are absent rather than empty strings.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = new WorkflowRun
        {
            Id = "wr_txn_no_meta",
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: FixedTime),
            Stages = [],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(run, [new WorkflowRunCompleted()]));

        Assert.Contains("projectId", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_StampedEnvelopes_CarryWorkflowProducerContext()
    {
        // Drives every workflow event family through the production path.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_conformance", includeAnnotations: true);

        WorkflowEvent[] events = [
            new WorkflowRunStarted(),
            new StageStarted("build"),
            new TaskStarted("build", "task_a", "worker_a"),
            new CheckPassed("review", "lint", null),
            new FeedbackRequested("code-review", "fb_1", null),
            new WorkflowArtifactRecorded("wr_txn_conformance", "task_a", "logs.txt", FixedTime),
        ];
        await store.SaveAsync(run, events);

        var stored = await _eventStore.ListAsync("wr_txn_conformance");
        Assert.Equal(6, stored.Count);
        for (var i = 0; i < events.Length; i++)
        {
            var entry = stored[i];
            Assert.Equal("wr_txn_conformance", entry.Envelope.Extensions[EventCatalog.Lineage.WorkflowRunId]);
            Assert.Equal("proj_txn", entry.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
            ProducerConformance.Assert(
                EventProducerFamily.WorkflowRun,
                entry.Envelope.Extensions,
                WorkflowContext(run, events[i]));
        }
    }

    [Fact]
    public async Task SaveAsync_AllWorkflowEventVariants_SatisfyWorkflowProducerFamily()
    {
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_all_variants", includeAnnotations: true, epicNumber: 7);
        WorkflowEvent[] events =
        [
            new WorkflowRunStarted(),
            new WorkflowRunResumed(),
            new WorkflowRunPaused(),
            new WorkflowRunStopped(),
            new WorkflowRunCompleted(),
            new WorkflowRunFailed("failed"),
            new StageStarted("build"),
            new StageCompleted("build"),
            new StageFailed("build", "failed"),
            new StageApprovalRequested("review"),
            new StageApprovalResolved("review", ApprovalResult.Approved),
            new FeedbackRequested("review", "feedback_1"),
            new TaskStarted("build", "task_1", "worker_1"),
            new TaskCompleted("build", "task_1"),
            new TaskFailed("build", "task_1", "failed"),
            new CheckPassed("build", "lint", null),
            new CheckFailed("build", "lint", "failed"),
            new CheckPending("build", "lint", null),
            new WorkflowArtifactRecorded("wr_txn_all_variants", "task_1", "artifact.txt", FixedTime),
        ];

        await store.SaveAsync(run, events);

        var stored = await _eventStore.ListAsync(run.Id);
        Assert.Equal(events.Length, stored.Count);
        for (var i = 0; i < events.Length; i++)
        {
            ProducerConformance.Assert(
                EventProducerFamily.WorkflowRun,
                stored[i].Envelope.Extensions,
                WorkflowContext(run, events[i]));
        }
    }

    [Fact]
    public void WorkflowRunLineage_StageOf_RecognisesAllStageBearingVariants()
    {
        // Structural inspection of the unwrapped union variant decides
        // whether a `stage` stamp is set. Pin that the helper returns
        // the stage for Stage*/StageApproval*/Feedback/Task*/Check* and
        // null for the run-lifecycle variants and WorkflowArtifactRecorded.
        Assert.True(WorkflowRunLineage.CarriesStage(new StageStarted("build")));
        Assert.Equal("build", WorkflowRunLineage.StageOf(new StageStarted("build")));
        Assert.Equal("build", WorkflowRunLineage.StageOf(new StageCompleted("build")));
        Assert.Equal("review", WorkflowRunLineage.StageOf(new StageFailed("review", "boom")));
        Assert.Equal("merge", WorkflowRunLineage.StageOf(new StageApprovalRequested("merge")));
        Assert.Equal("merge", WorkflowRunLineage.StageOf(new StageApprovalResolved("merge", ApprovalResult.Approved)));
        Assert.Equal("code-review", WorkflowRunLineage.StageOf(new FeedbackRequested("code-review", "fb_1")));
        Assert.Equal("build", WorkflowRunLineage.StageOf(new TaskStarted("build", "task_a", "worker_a")));
        Assert.Equal("build", WorkflowRunLineage.StageOf(new TaskCompleted("build", "task_a")));
        Assert.Equal("build", WorkflowRunLineage.StageOf(new TaskFailed("build", "task_a", "boom")));
        Assert.Equal("review", WorkflowRunLineage.StageOf(new CheckPassed("review", "lint", null)));
        Assert.Equal("review", WorkflowRunLineage.StageOf(new CheckFailed("review", "lint", "boom")));
        Assert.Equal("review", WorkflowRunLineage.StageOf(new CheckPending("review", "lint", null)));

        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunStarted()));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunResumed()));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunPaused()));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunStopped()));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunCompleted()));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowRunFailed("boom")));
        Assert.Null(WorkflowRunLineage.StageOf(new WorkflowArtifactRecorded("wr_1", "task_a", "logs.txt", FixedTime)));
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        // The SaveAsync(run, events) overload is the transactional
        // entry point; when no events are supplied it should still
        // commit the state row cleanly with no spurious event rows.
        var store = new WorkflowRunStore(_dbFactory, _eventStore, _grainFactory, NullLogger<WorkflowRunStore>.Instance);
        var run = BuildRun("wr_txn_state_only", includeAnnotations: true);

        await store.SaveAsync(run, []);

        var loaded = await store.LoadAsync("wr_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAsync("wr_txn_state_only"));
    }

    private static WorkflowRun BuildRun(string id, bool includeAnnotations, int? epicNumber = null)
    {
        Dictionary<string, string>? annotations = null;
        if (includeAnnotations)
        {
            annotations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = ProjectId,
                ["issueNumber"] = "1",
            };
            if (epicNumber is > 0)
                annotations["epicNumber"] = epicNumber.Value.ToString();
        }
        return new WorkflowRun
        {
            Id = id,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: annotations),
            Stages = [],
        };
    }

    private static ProducerLineageContext WorkflowContext(WorkflowRun run, WorkflowEvent evt)
    {
        var annotations = run.Metadata.Annotations;
        return new ProducerLineageContext(
            ProjectId: annotations?.GetValueOrDefault("projectId"),
            Issue: annotations?.GetValueOrDefault("issueNumber"),
            Epic: annotations?.GetValueOrDefault("epicNumber"),
            WorkflowRunId: run.Id,
            Stage: WorkflowRunLineage.StageOf(evt),
            StageRequired: WorkflowRunLineage.CarriesStage(evt));
    }

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
    /// swallow the exception and that the state transaction is rolled
    /// back.
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
            await db.WorkflowRunEvents.AddAsync(new WorkflowRunEventRow
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
