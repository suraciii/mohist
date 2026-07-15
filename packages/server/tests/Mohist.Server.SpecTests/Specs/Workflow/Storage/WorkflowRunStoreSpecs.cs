using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

/// <summary>
/// Unit specs for <see cref="WorkflowRunStore"/> covering issue-361 T-003:
/// the store now stamps both <c>projectid</c> and <c>issueid</c> onto the
/// emitted WorkflowRun CloudEvent (read from
/// <see cref="WorkflowRunMetadata.Annotations"/>), appends the event row in
/// the same EF Core transaction as the run state, and lets an event-row
/// write failure roll back the state transaction instead of swallowing it.
/// </summary>
public sealed class FakeWorkflowRunStoreDbContextFactory : IDbContextFactory<MohistDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeWorkflowRunStoreDbContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        MigratedSqliteTemplate.CopyTo(_connection);
    }

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class WorkflowRunStoreSpecs
{
    private const string ProjectId = "proj_workflow_store";
    private const string IssueId = "issue_ws_1";
    private const string WorkflowRunId = "wr_ws_1";
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithProjectAnnotation_StampsProjectIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveInitialAsync_RejectsStaleIssueLineageThenStampsTheReloadedEpicSnapshot()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        await using (var seed = factory.CreateDbContext())
        {
            seed.Issues.Add(new IssueRow
            {
                IssueId = IssueId,
                State = "{}",
                LineageVersion = 1,
            });
            await seed.SaveChangesAsync();
        }

        var staleRun = CreateRun("wr_initial_stale", epicId: null);
        var staleGuard = new WorkflowStartLineageGuard(IssueId, 1);

        await using (var link = factory.CreateDbContext())
        {
            await IssueStore.StageEpicAffiliationAsync(link, IssueId, "epic_committed");
            await link.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<WorkflowStartLineageChangedException>(
            () => store.SaveInitialAsync(staleRun, [new WorkflowRunStarted()], staleGuard));
        Assert.Empty(await eventStore.ListAsync(staleRun.Id));

        var reloadedRun = CreateRun("wr_initial_reloaded", epicId: "epic_committed");
        await store.SaveInitialAsync(
            reloadedRun,
            [new WorkflowRunStarted()],
            new WorkflowStartLineageGuard(IssueId, 2));

        var started = Assert.Single(await eventStore.ListAsync(reloadedRun.Id));
        Assert.Equal("epic_committed", started.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveInitialAsync_GuardedRunIsNotAssignableUntilItsIssueBindingActivatesIt()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);
        await using (var seed = factory.CreateDbContext())
        {
            seed.Issues.Add(new IssueRow
            {
                IssueId = IssueId,
                State = "{}",
                LineageVersion = 1,
            });
            await seed.SaveChangesAsync();
        }

        var run = CreateRun("wr_prebind", epicId: null);
        await store.SaveInitialAsync(run, [new WorkflowRunStarted()], new WorkflowStartLineageGuard(IssueId, 1));

        var querier = new WorkflowRunQuerier(factory);
        Assert.Empty(await querier.FindAssignableAsync(ProjectId));

        run.ActivateForDispatch(FixedTime);
        await store.SaveAsync(run);

        Assert.Equal([run.Id], await querier.FindAssignableAsync(ProjectId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithIssueAnnotation_StampsIssueIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.True(stored.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
        Assert.Equal(IssueId, stampedIssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithIssueAnnotation_StampsIssueNumberAsUnifiedIssueKey()
    {
        // D3/T-002: the user-visible issue number is stamped under the
        // protocol name `issue` (replacing the legacy `issueno`); this
        // is the key subscription expressions will match on, so the
        // producer side must drop any reference to the old name.
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: FixedTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.True(stored.Envelope.Extensions.TryGetValue("issue", out var stampedIssue));
        Assert.Equal("1", stampedIssue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithoutProjectAnnotation_FailsBecauseProjectOwnershipIsRequired()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

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

        Assert.Contains("projectId", ex.Message);
        Assert.Empty(await eventStore.ListAsync(WorkflowRunId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithEvents_PersistsStateAndEventRowsInSameTransaction()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                }),
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_UsesCurrentEpicSnapshotAfterLinkAndUnlink()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);
        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: FixedTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunStarted()]);

        await using (var link = factory.CreateDbContext())
        {
            await WorkflowRunStore.StageEpicAffiliationAsync(link, run.Id, "epic_workflow");
            await link.SaveChangesAsync();
        }
        run = (await store.LoadAsync(run.Id))!;
        await store.SaveAsync(run, [new WorkflowRunResumed()]);

        await using (var unlink = factory.CreateDbContext())
        {
            await WorkflowRunStore.StageEpicAffiliationAsync(unlink, run.Id, null);
            await unlink.SaveChangesAsync();
        }
        run = (await store.LoadAsync(run.Id))!;
        await store.SaveAsync(run, [new WorkflowRunPaused()]);

        var events = await eventStore.ListAsync(run.Id);
        Assert.False(events[0].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));
        Assert.Equal("epic_workflow", events[1].Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(events[2].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadAsync_LegacyExhaustedRecovery_RetryPersistsFreshRecoveryRound()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadAsync_LegacySameDefinitionAcrossStages_PreservesIndependentRecoveryRounds()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);
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
            [new RecoveryHandlerDefinition("promise=FAIL", [new TaskDefinition("fix", "Fix", "spec/fix")], RetrySelf: true)]),
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

    private static WorkflowRun CreateRun(string workflowRunId, string? epicId)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = ProjectId,
            ["issueId"] = IssueId,
            ["issueNumber"] = "1",
        };
        if (epicId is not null)
            annotations["epicId"] = epicId;

        return new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(null, FixedTime, Annotations: annotations),
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
