using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

/// <summary>
/// Unit specs for <see cref="WorkflowRunStore"/> covering issue-361 T-003:
/// the store stamps the project-scoped Issue context onto the
/// emitted WorkflowRun CloudEvent (read from
/// <see cref="WorkflowRunMetadata.Annotations"/>), appends the event row in
/// the same EF Core transaction as the run state, and lets an event-row
/// write failure roll back the state transaction instead of swallowing it.
/// </summary>
public class WorkflowRunStoreSpecs
{
    private const string ProjectId = "proj_workflow_store";
    private const int IssueNumber = 1;
    private const string WorkflowRunId = "wr_ws_1";
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private static WorkflowRunStore CreateStore(IDbContextFactory<MohistDbContext> factory, IEventStore eventStore) =>
        new(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance, TestServices.BackgroundTasks);

    [Fact]
    public async Task SaveAsync_WithProjectAnnotation_StampsProjectIdOnPersistedEventExtensions()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                ProjectId: ProjectId,
                IssueNumber: IssueNumber),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        var envelope = stored.Envelope;
        Assert.Equal("com.mohist.workflow.run.failed", envelope.Type);
        Assert.True(envelope.Extensions.TryGetValue("projectid", out var projectId));
        Assert.Equal(ProjectId, projectId);
        // workflowrunid is always stamped (D2/issue-412 T-002): the run
        // itself is the producer, so every emitted workflow.* envelope
        // carries its run id on extensions.
        Assert.True(envelope.Extensions.TryGetValue("workflowrunid", out var stampedRunId));
        Assert.Equal(WorkflowRunId, stampedRunId);
    }

    [Fact]
    public async Task SaveAsync_UsesTheWorkflowOwnedLineageSnapshot()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);

        var run = CreateRun("wr_owned_lineage", epicNumber: 2);
        await store.SaveAsync(run, [new WorkflowRunStarted()]);

        var started = Assert.Single(await eventStore.ListAsync(run.Id));
        Assert.Equal("2", started.Envelope.Extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public async Task SaveAsync_NewTerminalRun_DoesNotPersistProfileBackingKey()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = CreateRun("wr_terminal_insert", epicNumber: null);
        run.Status = WorkflowRunStatus.Completed;
        run.WorkflowProfileId = "delivery/review";

        await store.SaveAsync(run);

        await using var db = new MohistDbContext(database.Options);
        var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == run.Id);
        Assert.Null(row.WorkflowProfileIdKey);
    }

    [Fact]
    public async Task DeletionBlocker_ReadsCanonicalProfileIdFromStoreStateWhenBackingKeyIsMissing()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = CreateRun("wr_profile_blocker", epicNumber: null);
        run.Status = WorkflowRunStatus.Completed;
        run.WorkflowProfileId = "delivery/review";

        await store.SaveAsync(run);

        await using (var db = new MohistDbContext(database.Options))
        {
            var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == run.Id);
            var state = JsonNode.Parse(row.State)!.AsObject();
            state["status"] = "running";
            row.State = state.ToJsonString();
            await db.SaveChangesAsync();
        }

        var blockers = await new WorkflowProfileDeletionBlockerQuery(factory)
            .GetBlockersAsync(ProjectId, "delivery/review");

        Assert.Contains(blockers.ActiveRuns, blocker => blocker.WorkflowRunId == run.Id);
    }

    [Fact]
    public async Task SaveAsync_WithIssueContext_StampsIssueNumberOnPersistedEventExtensions()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                ProjectId: ProjectId,
                IssueNumber: IssueNumber),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.Equal(IssueNumber.ToString(), stored.Envelope.Extensions[EventCatalog.Lineage.Issue]);
    }

    [Fact]
    public async Task SaveAsync_PreservesUserAnnotationsWithoutMixingLineageIntoTheBag()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var store = CreateStore(factory, new EventStore(factory, NullLogger<EventStore>.Instance));
        var run = CreateRun("wr_user_annotations", epicNumber: 7);
        run.Metadata = run.Metadata with
        {
            Annotations = new Dictionary<string, string>(StringComparer.Ordinal) { ["owner"] = "human" },
        };

        await store.SaveAsync(run);

        var loaded = await store.LoadAsync(run.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ProjectId, loaded!.Metadata.ProjectId);
        Assert.Equal(IssueNumber, loaded.Metadata.IssueNumber);
        Assert.Equal(7, loaded.Metadata.EpicNumber);
        Assert.Equal("human", loaded.Metadata.Annotations!["owner"]);
        Assert.DoesNotContain("projectId", loaded.Metadata.Annotations.Keys);
        Assert.DoesNotContain("issueNumber", loaded.Metadata.Annotations.Keys);
        Assert.DoesNotContain("epicNumber", loaded.Metadata.Annotations.Keys);
    }

    [Fact]
    public async Task SaveAsync_WithoutProjectAnnotation_FailsBecauseProjectOwnershipIsRequired()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow),
            Stages = [],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(run, [new WorkflowRunFailed("failed")]));

        Assert.Contains("project context", ex.Message);
        Assert.Empty(await eventStore.ListAsync(WorkflowRunId));
    }

    [Fact]
    public async Task SaveAsync_WithEvents_PersistsStateAndEventRowsInSameTransaction()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                    ProjectId: ProjectId,
                    IssueNumber: IssueNumber),
            Stages = [],
        };

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunFailed("boom"),
        ]);

        var stored = await eventStore.ListAsync(WorkflowRunId);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.started");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.failed");

        var loaded = await store.LoadAsync(WorkflowRunId);
        Assert.NotNull(loaded);
        Assert.Equal(WorkflowRunId, loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_UsesCurrentEpicSnapshotAfterLinkAndUnlink()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: FixedTime,
                    ProjectId: ProjectId,
                    IssueNumber: IssueNumber),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunStarted()]);

        run.Metadata = run.Metadata with { EpicNumber = 2 };
        await store.SaveAsync(run, [new WorkflowRunResumed()]);

        run.Metadata = run.Metadata with { EpicNumber = null };
        await store.SaveAsync(run, [new WorkflowRunPaused()]);

        var events = await eventStore.ListAsync(run.Id);
        Assert.False(events[0].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));
        Assert.Equal("2", events[1].Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.False(events[2].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    [Fact]
    public async Task LoadAsync_LegacyExhaustedRecovery_RetryPersistsFreshRecoveryRound()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = CreateLegacyExhaustedRecoveryRun();

        await using (var db = factory.CreateDbContext())
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = run.Id,
                State = ToLegacyRecoveryState(run),
            });
            await db.SaveChangesAsync();
        }

        var loaded = await store.LoadAsync(run.Id);
        Assert.NotNull(loaded);
        var historical = loaded!.CurrentStage().Tasks;
        Assert.Equal(new int?[] { 2, 1, 0 }, historical.Select(task => task.RecoveryRemaining).ToArray());
        Assert.All(historical, task => Assert.Equal(2, task.Recovery!.Budget));

        loaded.Retry(DateTimeOffset.UnixEpoch);
        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync(run.Id);
        Assert.NotNull(reloaded);
        var attempts = reloaded!.CurrentStage().Tasks;
        Assert.Equal(new int?[] { 2, 1, 0, null }, attempts.Select(task => task.RecoveryRemaining).ToArray());
        Assert.All(attempts, task => Assert.Equal(2, task.Recovery!.Budget));

        await using var persistedDb = factory.CreateDbContext();
        var persisted = await persistedDb.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == run.Id);
        using var document = JsonDocument.Parse(persisted.State);
        var freshAttempt = document.RootElement.GetProperty("stages")[0].GetProperty("tasks")
            .EnumerateArray().Single(task => task.GetProperty("id").GetString() == "review.4");
        Assert.True(freshAttempt.TryGetProperty("recoveryRemaining", out var recoveryRemaining));
        Assert.Equal(JsonValueKind.Null, recoveryRemaining.ValueKind);
    }

    [Fact]
    public async Task LoadAsync_LegacySameDefinitionAcrossStages_PreservesIndependentRecoveryRounds()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = CreateStore(factory, eventStore);
        var run = CreateLegacySameDefinitionAcrossStagesRun();

        await using (var db = factory.CreateDbContext())
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = run.Id,
                State = ToLegacyRecoveryState(run),
            });
            await db.SaveChangesAsync();
        }

        var loaded = await store.LoadAsync(run.Id);
        Assert.NotNull(loaded);
        var firstStageTask = Assert.Single(loaded!.Stages.Single(stage => stage.Id == "plan").Tasks);
        var failedStage = loaded.Stages.Single(stage => stage.Id == "check");
        var failedTask = Assert.Single(failedStage.Tasks);
        Assert.Equal(2, firstStageTask.Recovery!.Budget);
        Assert.Equal(2, firstStageTask.RecoveryRemaining);
        Assert.Equal(5, failedTask.Recovery!.Budget);
        Assert.Equal(5, failedTask.RecoveryRemaining);

        loaded.Retry(DateTimeOffset.UnixEpoch);
        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync(run.Id);
        Assert.NotNull(reloaded);
        var retriedStage = reloaded!.Stages.Single(stage => stage.Id == "check");
        var freshRetry = Assert.Single(retriedStage.Tasks, task => task.Id == "review.2");
        Assert.Equal(5, freshRetry.Recovery!.Budget);
        Assert.Null(freshRetry.RecoveryRemaining);

        var dispatch = Assert.IsType<WorkflowTaskWork>(reloaded.NextWork());
        Assert.Equal(5, dispatch.Recovery!.Budget);
        Assert.Null(dispatch.RecoveryRemaining);
    }

    private static WorkflowRun CreateLegacyExhaustedRecoveryRun()
    {
        var failedTask = LegacyAttempt(3, 0, TaskRunStatus.Failed);
        var failure = new FailureDetails(FailureReason.TaskFailed, "check", failedTask.Id, Message: "recovery exhausted");
        return new WorkflowRun
        {
            Id = "wr_legacy_recovery",
            Metadata = new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Failed,
            CurrentStageId = "check",
            Failure = failure,
            Stages =
            [
                new StageRun
                {
                    Id = "check",
                    Attempt = 1,
                    RequiresApproval = false,
                    Initialized = true,
                    Status = StageRunStatus.Failed,
                    Failure = failure,
                    Tasks =
                    [
                        LegacyAttempt(1, 2, TaskRunStatus.Completed),
                        LegacyAttempt(2, 1, TaskRunStatus.Completed),
                        failedTask,
                    ],
                    Checks = [],
                },
            ],
        };
    }

    private static WorkflowRun CreateLegacySameDefinitionAcrossStagesRun()
    {
        var failedTask = LegacyAttempt(1, 5, TaskRunStatus.Failed);
        var failure = new FailureDetails(FailureReason.TaskFailed, "check", failedTask.Id, Message: "recovery exhausted");
        return new WorkflowRun
        {
            Id = "wr_legacy_stage_scoped_recovery",
            Metadata = new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Failed,
            CurrentStageId = "check",
            Failure = failure,
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Initialized = true,
                    Status = StageRunStatus.Completed,
                    Tasks = [LegacyAttempt(1, 2, TaskRunStatus.Completed)],
                    Checks = [],
                },
                new StageRun
                {
                    Id = "check",
                    Attempt = 1,
                    RequiresApproval = false,
                    Initialized = true,
                    Status = StageRunStatus.Failed,
                    Failure = failure,
                    Tasks = [failedTask],
                    Checks = [],
                },
            ],
        };
    }

    private static TaskRun LegacyAttempt(int attempt, int budget, TaskRunStatus status) => new()
    {
        Id = $"review.{attempt}",
        DefinitionId = "review",
        Attempt = attempt,
        Title = "Review",
        Uses = "spec/review",
        Status = status,
        Recovery = new RecoveryDefinition(
            budget,
            [new RecoveryHandlerDefinition("output.promise=FAIL", [new TaskDefinition("fix", "Fix", "spec/fix")], RetrySelf: true)]),
    };

    private static string ToLegacyRecoveryState(WorkflowRun run)
    {
        var root = JsonNode.Parse(JSON.Serialize(run))!.AsObject();
        foreach (var stage in root["stages"]!.AsArray())
        {
            foreach (var task in stage!["tasks"]!.AsArray())
                task!.AsObject().Remove("recoveryRemaining");
        }
        return root.ToJsonString();
    }

    private static WorkflowRun CreateRun(string workflowRunId, int? epicNumber)
    {
        return new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(null, FixedTime, ProjectId: ProjectId, IssueNumber: IssueNumber, EpicNumber: epicNumber),
            Stages = [],
        };
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
}
