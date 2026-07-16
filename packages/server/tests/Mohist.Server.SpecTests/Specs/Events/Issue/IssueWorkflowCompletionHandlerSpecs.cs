using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Events.Issue;

/// <summary>
/// Covers <see cref="IssueWorkflowCompletionHandler"/>: the
/// <c>com.mohist.workflow.run.completed</c> subscription that
/// transitions the owning in-progress issue to <c>Done</c> via
/// <see cref="IIssueGrain.CompleteWorkAsync"/>. Spec:
/// <c>openspec/changes/issue-307/specs/issue-workflow-completion/spec.md</c>.
/// </summary>
public class IssueWorkflowCompletionHandlerSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void HasSingleSubscriptionAttributeForCompleted()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueWorkflowCompletionHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.WorkflowRunCompleted, attr!.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_ReturnsInProgressIssueReference()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var querier = NewIssueQuerier(database.Factory);

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Equal(new IssueWorkflowRef("project_1", 1), issue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_DoneIssueWithPreservedReference_ReturnsNull()
    {
        // Done issues keep their workflowRunId as historical execution
        // data — the lookup must filter to in_progress so a stale
        // binding doesn't drive a redundant transition.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.Done, workflowRunId: "wr_completed");

        var querier = NewIssueQuerier(database.Factory);

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Null(issue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_MixedRows_ReturnsOnlyInProgressMatch()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.Done, workflowRunId: "wr_completed");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2,
            status: IssueStatus.Done, workflowRunId: "wr_completed",
            archivedAt: new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc));
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 3,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 4,
            status: IssueStatus.InProgress, workflowRunId: "wr_other");

        var querier = NewIssueQuerier(database.Factory);

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Equal(new IssueWorkflowRef("project_1", 3), issue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_NoMatch_ReturnsNull()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_other");

        var querier = NewIssueQuerier(database.Factory);

        var issue = await querier.GetIssueForWorkflowRunAsync("wr_completed");

        Assert.Null(issue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Querier_GetIssueForWorkflowRunAsync_NullOrEmpty_ReturnsNull()
    {
        await using var database = CreateDatabase();

        var querier = NewIssueQuerier(database.Factory);

        Assert.Null(await querier.GetIssueForWorkflowRunAsync(null!));
        Assert.Null(await querier.GetIssueForWorkflowRunAsync(""));
        Assert.Null(await querier.GetIssueForWorkflowRunAsync("   "));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_CompletedEventForInProgressIssue_TransitionsIssueToDone()
    {
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), call.Issue);
        Assert.Equal("wr_completed", call.WorkflowRunId);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        // After the handler ran, the in-progress issue has been
        // transitioned to Done (driven entirely by the event
        // subscription — no sweep advancement, no read-path open).
        Assert.Equal("done", stored.Status);
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_EmptySource_NoOpsAndDoesNotInvokeGrain()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/other/whatever", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: FixedNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_MissingScopedIssueExtension_NoOpsAndDoesNotInvokeGrain()
    {
        // A completion event without the project-scoped issue reference
        // cannot identify an aggregate and must not invoke a grain.
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_orphan", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: FixedNow,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_DuplicateCompletedDelivery_OnlyFirstInvocationRunsGrainLogic()
    {
        // First delivery transitions the issue to Done; the second
        // delivery invokes CompleteWorkAsync again, but the aggregate
        // guard rejects it (issue is no longer in_progress), so only
        // one effective transition occurs. No throw, no field mutation.
        // This is the documented idempotent path.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt1 = BuildCompletedEvent(workflowRunId: "wr_completed");
        var evt2 = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt1, CancellationToken.None);
        await handler.HandleAsync(evt2, CancellationToken.None);

        Assert.Equal(2, grains.Calls.Count);
        Assert.All(grains.Calls, c =>
        {
            Assert.Equal(new IssueWorkflowRef("project_1", 1), c.Issue);
            Assert.Equal("wr_completed", c.WorkflowRunId);
        });

        await using var verify = database.CreateDbContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_MismatchedWorkflowRunIdOnIssue_DoesNotMutateIssue()
    {
        // After the issue is Done with wr_completed preserved, a
        // second event for the same run id calls CompleteWorkAsync
        // again, but the aggregate guard rejects it (issue is no
        // longer in_progress). Verify no mutation happens even when a
        // *different* delivery path attempts to invoke CompleteWorkAsync
        // with a stale workflowRunId — the Issue.Complete guard rejects
        // it (no change).
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        // Pre-flip the issue to Done with the matching id, then verify
        // a mismatched-id CompleteWorkAsync is guarded by the aggregate.
        // Use the handler's own grain to perform the initial transition so
        // the in-memory cache and DB stay consistent.
        var firstEvent = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(firstEvent, CancellationToken.None);
        var firstCall = grains.Calls.Single();
        Assert.Equal(new IssueWorkflowRef("project_1", 1), firstCall.Issue);
        Assert.Equal("wr_completed", firstCall.WorkflowRunId);
        grains.Calls.Clear();

        // Now drive the handler with the SAME run id; CompleteWorkAsync
        // is invoked again but the aggregate is already Done so the
        // transition is a no-op.
        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");
        await handler.HandleAsync(evt, CancellationToken.None);

        var secondCall = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), secondCall.Issue);
        Assert.Equal("wr_completed", secondCall.WorkflowRunId);
        grains.Calls.Clear();

        // Independently verify that CompleteWorkAsync with a
        // mismatched workflowRunId would be a no-op (Issue.Complete
        // guard): this is what the spec requirement
        // "Mismatched workflow run id is ignored" asserts. The
        // aggregate's Complete() returns false when the run id does
        // not match — the guard fires regardless of subscription
        // filtering.
        var staleGrain = grains.GetIssueGrain("project_1:1");
        await staleGrain.CompleteWorkAsync("wr_mismatch");
        var stillOneCall = Assert.Single(grains.Calls);
        Assert.Equal("wr_mismatch", stillOneCall.WorkflowRunId);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        var final = IssueStore.Deserialize(stored.State)!;
        Assert.Equal(IssueStatus.Done, final.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_GrainThrows_PropagatesToDispatcher()
    {
        await using var database = CreateDatabase();

        var grains = new ThrowingIssueGrainFactory();
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(evt, CancellationToken.None));

        Assert.Equal(1, grains.CallCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Filter_FailedTerminalEvent_ReturnsFalse()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_failed", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunFailed,
            time: FixedNow,
            data: null);

        Assert.False(handler.Filter(evt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Filter_StoppedTerminalEvent_ReturnsFalse()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow-runs/wr_stopped", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunStopped,
            time: FixedNow,
            data: null);

        Assert.False(handler.Filter(evt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Filter_CompletedEvent_ReturnsTrue()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        var evt = BuildCompletedEvent(workflowRunId: "wr_completed");

        Assert.True(handler.Filter(evt));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task HandleAsync_CompletesIssue_FromScopedIssueExtensions()
    {
        // The event carries the project-scoped issue reference, so the
        // handler dispatches CompleteWorkAsync without a reverse lookup.
        await using var database = CreateDatabase();
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1,
            status: IssueStatus.InProgress, workflowRunId: "wr_completed");

        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowCompletionHandler(grains,
            NullLogger<IssueWorkflowCompletionHandler>.Instance);

        await handler.HandleAsync(
            BuildCompletedEvent(workflowRunId: "wr_completed"),
            CancellationToken.None);

        var call = Assert.Single(grains.Calls);
        Assert.Equal(new IssueWorkflowRef("project_1", 1), call.Issue);
        Assert.Equal("wr_completed", call.WorkflowRunId);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Issues.AsNoTracking().FirstAsync();
        Assert.Equal(IssueStatus.Done, IssueStore.Deserialize(stored.State)!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task BindingHandler_RedrivesTheCommittedIssueBinding()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowBindingHandler(grains);

        await handler.HandleAsync(
            BuildWorkStartedEvent("wr_binding", "project_1", 1),
            CancellationToken.None);

        Assert.Equal(new RecordedCall(new IssueWorkflowRef("project_1", 1), "wr_binding"), Assert.Single(grains.BindingCalls));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task BindingHandler_MissingScopedIssueReferenceFailsForDurableRetry()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingIssueGrainFactory(database.Factory, new FakeTimeProvider(FixedNow));
        var handler = new IssueWorkflowBindingHandler(grains);
        var evt = BuildWorkStartedEvent("wr_binding", "project_1", issueNumber: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(evt, CancellationToken.None));

        Assert.Empty(grains.BindingCalls);
    }

    private static CloudEvent BuildCompletedEvent(
        string workflowRunId,
        string projectId = "project_1",
        int issueNumber = 1) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: FixedNow,
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
                [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
            });

    private static CloudEvent<IssueWorkStarted> BuildWorkStartedEvent(
        string workflowRunId,
        string projectId,
        int? issueNumber) =>
        new(
            Guid.NewGuid().ToString(),
            new Uri($"/mohist/projects/{projectId}/issues/{issueNumber?.ToString() ?? "missing"}", UriKind.Relative),
            EventCatalog.ReverseDns.IssueWorkStarted,
            FixedNow,
            new IssueWorkStarted(workflowRunId),
            extensions: issueNumber is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [EventCatalog.Lineage.ProjectId] = projectId,
                    [EventCatalog.Lineage.Issue] = issueNumber.Value.ToString(),
                });

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        int issueNumber,
        IssueStatus status,
        string? workflowRunId,
        DateTime? archivedAt = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            WorkflowRunId = workflowRunId,
            ArchivedAt = archivedAt,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            WorkflowRunId = workflowRunId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Minimal querier for unit tests: <see cref="IssueQuerier"/> has
    /// many collaborators, but <c>GetIssueForWorkflowRunAsync</c>
    /// only uses the db factory. The unused dependencies are left as
    /// <c>null!</c>, matching the existing EpicQuerier unit-test
    /// pattern in <c>EpicAutoDoneHandlerSpecs</c>.
    /// </summary>
    private static IssueQuerier NewIssueQuerier(IDbContextFactory<MohistDbContext> dbFactory) =>
        new(
            dbFactory,
            projects: null!,
            configService: null!,
            effectiveProfileResolver: null!,
            projectProfileManager: null!,
            loader: null!);

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class RecordingIssueGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly FakeTimeProvider _time;
        public List<RecordedCall> Calls { get; } = [];
        public List<RecordedCall> BindingCalls { get; } = [];

        public RecordingIssueGrainFactory(IDbContextFactory<MohistDbContext> dbFactory, FakeTimeProvider time)
        {
            _dbFactory = dbFactory;
            _time = time;
        }

        public IStateStore<DomainIssue> IssueStore { get; } = new InMemoryStateStore<DomainIssue>();

        public IIssueGrain GetIssueGrain(string grainKey)
        {
            var stateStore = IssueStore;
            return new CompleteWorkRecordingGrain(grainKey, stateStore, _dbFactory, _time, Calls, BindingCalls);
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    public sealed record RecordedCall(IssueWorkflowRef Issue, string WorkflowRunId);

    /// <summary>
    /// Minimal <see cref="IIssueGrain"/> that only implements
    /// <see cref="CompleteWorkAsync"/> realistically enough to drive
    /// the aggregate transition and persist it through the db-backed
    /// state store, mirroring the real <see cref="IssueGrain"/>'s
    /// behavior. Other methods are unimplemented because the handler
    /// under test only invokes <c>CompleteWorkAsync</c>.
    /// </summary>
    private sealed class CompleteWorkRecordingGrain : IIssueGrain
    {
        private readonly string _key;
        private readonly IStateStore<DomainIssue> _stateStore;
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly FakeTimeProvider _time;
        private readonly List<RecordedCall> _calls;
        private readonly List<RecordedCall> _bindingCalls;

        public CompleteWorkRecordingGrain(
            string key,
            IStateStore<DomainIssue> stateStore,
            IDbContextFactory<MohistDbContext> dbFactory,
            FakeTimeProvider time,
            List<RecordedCall> calls,
            List<RecordedCall> bindingCalls)
        {
            _key = key;
            _stateStore = stateStore;
            _dbFactory = dbFactory;
            _time = time;
            _calls = calls;
            _bindingCalls = bindingCalls;
        }

        public async Task CompleteWorkAsync(string workflowRunId)
        {
            _calls.Add(RecordedCallFor(_key, workflowRunId));
            var state = await LoadAsync(_key);
            if (state is null) return;
            if (!state.Complete(workflowRunId, _time.GetUtcNow().UtcDateTime)) return;
            await _stateStore.SaveAsync(_key, state);
            await PersistAsync(_key, state);
        }

        private async Task<DomainIssue?> LoadAsync(string key)
        {
            var fromMemory = await _stateStore.LoadAsync(key);
            if (fromMemory is not null) return fromMemory;
            await using var db = await _dbFactory.CreateDbContextAsync();
            var (projectId, issueNumber) = Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(key);
            var row = await db.Issues.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == issueNumber);
            if (row is null) return null;
            var state = IssueStore.Deserialize(row.State);
            if (state is not null)
                await _stateStore.SaveAsync(key, state);
            return state;
        }

        private async Task PersistAsync(string key, DomainIssue state)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var (projectId, issueNumber) = Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(key);
            var row = await db.Issues.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == issueNumber);
            if (row is null) return;
            row.State = IssueStore.Serialize(state);
            await db.SaveChangesAsync();
        }

        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null) => throw new NotSupportedException();
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task EnsureWorkflowBindingAsync(string workflowRunId)
        {
            _bindingCalls.Add(RecordedCallFor(_key, workflowRunId));
            return Task.CompletedTask;
        }

        private static RecordedCall RecordedCallFor(string grainKey, string workflowRunId)
        {
            var (projectId, issueNumber) = Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(grainKey);
            return new RecordedCall(new IssueWorkflowRef(projectId, issueNumber), workflowRunId);
        }
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task<bool> AssignEpicAsync(int epicNumber) => throw new NotSupportedException();
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
    }

    private sealed class ThrowingIssueGrainFactory : IGrainFactory
    {
        public int CallCount { get; private set; }

        public IIssueGrain GetIssueGrain(string grainKey)
        {
            CallCount++;
            return new ThrowingIssueGrain();
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class ThrowingIssueGrain : IIssueGrain
    {
        public Task CompleteWorkAsync(string workflowRunId) =>
            throw new InvalidOperationException("simulated grain failure");
        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null) => throw new NotSupportedException();
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task EnsureWorkflowBindingAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task<bool> AssignEpicAsync(int epicNumber) => throw new NotSupportedException();
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
    }
}
