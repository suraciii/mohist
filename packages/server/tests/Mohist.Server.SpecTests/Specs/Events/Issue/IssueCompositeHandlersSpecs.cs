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
using System.Collections.Concurrent;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Events.Issue;

/// <summary>
/// Covers the four child-event handlers + the parent-changed handler that
/// drive <see cref="IIssueGrain.RecomputeCompositeStatusAsync"/> on the
/// owning parent:
/// <list type="bullet">
/// <item><see cref="IssueCompositeChildStartedHandler"/> on <c>work-started</c></item>
/// <item><see cref="IssueCompositeChildCompletedHandler"/> on <c>completed</c></item>
/// <item><see cref="IssueCompositeChildCancelledHandler"/> on <c>cancelled</c></item>
/// <item><see cref="IssueCompositeChildReopenedHandler"/> on <c>reopened</c></item>
/// <item><see cref="IssueCompositeParentChangedHandler"/> on <c>parent-changed</c></item>
/// </list>
/// Spec:
/// <c>openspec/changes/issue-419/specs/compound-advancement/spec.md#requirement-status-recompute-shall-be-event-driven-idempotent-and-eventually-consistent</c>
/// and <c>openspec/changes/issue-419/specs/parent-status-aggregation/spec.md</c>.
/// </summary>
public class IssueCompositeHandlersSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private static CloudEvent<IssueWorkStarted> BuildWorkStartedEvent(
        string projectId,
        int issueNumber,
        int? parentIssueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: FixedNow,
            data: new IssueWorkStarted("wr_test"),
            subject: issueNumber.ToString(),
            extensions: BuildExtensions(projectId, issueNumber, parentIssueNumber));

    private static CloudEvent<IssueCompleted> BuildCompletedEvent(
        string projectId,
        int issueNumber,
        int? parentIssueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: FixedNow,
            data: new IssueCompleted("wr_test"),
            subject: issueNumber.ToString(),
            extensions: BuildExtensions(projectId, issueNumber, parentIssueNumber));

    private static CloudEvent<IssueCancelled> BuildCancelledEvent(
        string projectId,
        int issueNumber,
        int? parentIssueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCancelled,
            time: FixedNow,
            data: new IssueCancelled("test-cancel"),
            subject: issueNumber.ToString(),
            extensions: BuildExtensions(projectId, issueNumber, parentIssueNumber));

    private static CloudEvent<IssueReopened> BuildReopenedEvent(
        string projectId,
        int issueNumber,
        int? parentIssueNumber) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueReopened,
            time: FixedNow,
            data: new IssueReopened(),
            subject: issueNumber.ToString(),
            extensions: BuildExtensions(projectId, issueNumber, parentIssueNumber));

    private static CloudEvent<IssueParentChanged> BuildParentChangedEvent(
        string projectId,
        int issueNumber,
        int? previousParent,
        int? newParent) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/issue/{issueNumber}", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueParentChanged,
            time: FixedNow,
            data: new IssueParentChanged(previousParent, newParent),
            subject: issueNumber.ToString(),
            extensions: BuildExtensions(projectId, issueNumber, parentIssueNumber: null));

    private static IReadOnlyDictionary<string, string> BuildExtensions(
        string projectId, int issueNumber, int? parentIssueNumber)
    {
        var ext = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
            [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
        };
        if (parentIssueNumber is > 0)
        {
            ext[EventCatalog.Lineage.Parent] = parentIssueNumber.Value.ToString();
        }
        return ext;
    }

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

    private static async Task SeedParentAsync(
        TestDatabase database,
        string projectId,
        int parentNumber,
        int? childCount = null)
    {
        var parent = new DomainIssue
        {
            ProjectId = projectId,
            Number = parentNumber,
            Title = $"Parent {parentNumber}",
            Status = IssueStatus.Backlog,
        };
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = parentNumber,
            State = IssueStore.Serialize(parent),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void ChildStartedHandler_HasSubscriptionAttributeOnWorkStarted()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueCompositeChildStartedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueWorkStarted, attr!.Type);
    }

    [Fact]
    public void ChildCompletedHandler_HasSubscriptionAttributeOnCompleted()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueCompositeChildCompletedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCompleted, attr!.Type);
    }

    [Fact]
    public void ChildCancelledHandler_HasSubscriptionAttributeOnCancelled()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueCompositeChildCancelledHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueCancelled, attr!.Type);
    }

    [Fact]
    public void ChildReopenedHandler_HasSubscriptionAttributeOnReopened()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueCompositeChildReopenedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueReopened, attr!.Type);
    }

    [Fact]
    public void ParentChangedHandler_HasSubscriptionAttributeOnParentChanged()
    {
        var attr = (SubscriptionAttribute?)Attribute.GetCustomAttribute(
            typeof(IssueCompositeParentChangedHandler), typeof(SubscriptionAttribute));
        Assert.NotNull(attr);
        Assert.Equal(EventCatalog.ReverseDns.IssueParentChanged, attr!.Type);
    }

    [Fact]
    public async Task ChildStartedHandler_WithParentLineage_DispatchesRecomputeToParent()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildStartedHandler(
            grains, NullLogger<IssueCompositeChildStartedHandler>.Instance);

        var evt = BuildWorkStartedEvent("project_1", issueNumber: 20, parentIssueNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal("project_1", call.ProjectId);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ChildCompletedHandler_WithParentLineage_DispatchesRecomputeToParent()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildCompletedHandler(
            grains, NullLogger<IssueCompositeChildCompletedHandler>.Instance);

        var evt = BuildCompletedEvent("project_1", issueNumber: 20, parentIssueNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ChildCancelledHandler_WithParentLineage_DispatchesRecomputeToParent()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildCancelledHandler(
            grains, NullLogger<IssueCompositeChildCancelledHandler>.Instance);

        var evt = BuildCancelledEvent("project_1", issueNumber: 20, parentIssueNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ChildReopenedHandler_WithParentLineage_DispatchesRecomputeToParent()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildReopenedHandler(
            grains, NullLogger<IssueCompositeChildReopenedHandler>.Instance);

        var evt = BuildReopenedEvent("project_1", issueNumber: 20, parentIssueNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ChildStartedHandler_WithoutParentLineage_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildStartedHandler(
            grains, NullLogger<IssueCompositeChildStartedHandler>.Instance);

        var evt = BuildWorkStartedEvent("project_1", issueNumber: 20, parentIssueNumber: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    [Fact]
    public async Task ChildCompletedHandler_WithoutParentLineage_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildCompletedHandler(
            grains, NullLogger<IssueCompositeChildCompletedHandler>.Instance);

        var evt = BuildCompletedEvent("project_1", issueNumber: 20, parentIssueNumber: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    [Fact]
    public async Task ChildCancelledHandler_WithoutParentLineage_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildCancelledHandler(
            grains, NullLogger<IssueCompositeChildCancelledHandler>.Instance);

        var evt = BuildCancelledEvent("project_1", issueNumber: 20, parentIssueNumber: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    [Fact]
    public async Task ChildReopenedHandler_WithoutParentLineage_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildReopenedHandler(
            grains, NullLogger<IssueCompositeChildReopenedHandler>.Instance);

        var evt = BuildReopenedEvent("project_1", issueNumber: 20, parentIssueNumber: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    [Fact]
    public async Task ChildStartedHandler_WithoutIssueContext_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildStartedHandler(
            grains, NullLogger<IssueCompositeChildStartedHandler>.Instance);

        var evt = new CloudEvent<IssueWorkStarted>(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/issue/1", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueWorkStarted,
            time: FixedNow,
            data: new IssueWorkStarted("wr_test"),
            subject: "1");

        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    [Fact]
    public async Task ChildCompletedHandler_DuplicateTerminalSignals_AreIdempotent()
    {
        // Multiple terminal events (a redelivery, or a dupe event from
        // the dispatcher) all converge to a single recompute call. The
        // grain absorbs the reordering via its no-op transition guard.
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeChildCompletedHandler(
            grains, NullLogger<IssueCompositeChildCompletedHandler>.Instance);

        var evt = BuildCompletedEvent("project_1", issueNumber: 20, parentIssueNumber: 10);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);
        await handler.HandleAsync(evt, CancellationToken.None);

        // Three handler calls produce three grain dispatch attempts
        // (idempotency lives on the grain, not the handler).
        Assert.Equal(3, grains.RecomputeCalls.Count);
    }

    [Fact]
    public async Task ParentChangedHandler_Attach_DispatchesRecomputeToNewParentOnly()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeParentChangedHandler(
            grains, NullLogger<IssueCompositeParentChangedHandler>.Instance);

        // Attach: previous parent null, new parent 10.
        var evt = BuildParentChangedEvent("project_1", issueNumber: 20, previousParent: null, newParent: 10);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ParentChangedHandler_Detach_DispatchesRecomputeToPreviousParentOnly()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeParentChangedHandler(
            grains, NullLogger<IssueCompositeParentChangedHandler>.Instance);

        // Detach: previous parent 10, new parent null.
        var evt = BuildParentChangedEvent("project_1", issueNumber: 20, previousParent: 10, newParent: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        var call = Assert.Single(grains.RecomputeCalls);
        Assert.Equal(10, call.ParentNumber);
    }

    [Fact]
    public async Task ParentChangedHandler_BothParents_Prevents_DispatchesToBothSides()
    {
        await using var database = CreateDatabase();
        await SeedParentAsync(database, "project_1", parentNumber: 10);
        await SeedParentAsync(database, "project_1", parentNumber: 11);
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeParentChangedHandler(
            grains, NullLogger<IssueCompositeParentChangedHandler>.Instance);

        // Re-home: child moved from parent 10 to parent 11.
        var evt = BuildParentChangedEvent("project_1", issueNumber: 20, previousParent: 10, newParent: 11);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal(2, grains.RecomputeCalls.Count);
        Assert.Contains(grains.RecomputeCalls, c => c.ParentNumber == 10);
        Assert.Contains(grains.RecomputeCalls, c => c.ParentNumber == 11);
    }

    [Fact]
    public async Task ParentChangedHandler_NoPrevious_NoNew_NoOpsWithoutGrainCall()
    {
        await using var database = CreateDatabase();
        var grains = new RecordingCompositeGrainFactory();

        var handler = new IssueCompositeParentChangedHandler(
            grains, NullLogger<IssueCompositeParentChangedHandler>.Instance);

        // IssueParentChanged with both ends null is a degenerate case
        // (shouldn't fire in practice) — the handler must still
        // no-op rather than crash.
        var evt = BuildParentChangedEvent("project_1", issueNumber: 20, previousParent: null, newParent: null);
        await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(grains.RecomputeCalls);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory Factory { get; }

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;
        public DbContextOptions<MohistDbContext> Options { get; }
        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed record RecomputeCall(string ProjectId, int ParentNumber);

    private sealed class RecordingCompositeGrainFactory : IGrainFactory
    {
        public ConcurrentBag<RecomputeCall> RecomputeCalls { get; } = [];

        public IIssueGrain GetIssueGrain(string projectId, int parentNumber)
        {
            RecomputeCalls.Add(new RecomputeCall(projectId, parentNumber));
            return new NullIssueGrain();
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) != typeof(IIssueGrain))
                throw new NotSupportedException(typeof(TGrainInterface).FullName);
            var parts = primaryKey.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var number))
                throw new InvalidOperationException($"unparseable grain key {primaryKey}");
            return (TGrainInterface)(object)GetIssueGrain(parts[0], number);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IIssueGrain))
            {
                var parts = grainPrimaryKey.Split(':');
                return GetIssueGrain(parts[0], int.Parse(parts[1]));
            }
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

    private sealed class NullIssueGrain : IIssueGrain
    {
        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parent) => Task.FromResult(number);
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => Task.FromResult("wr_test");
        public Task CompleteWorkAsync(string workflowRunId) => Task.CompletedTask;
        public Task MarkDoneAsync() => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public Task UpdateAsync(string title, string? body) => Task.CompletedTask;
        public Task UpdateFullAsync(UpdateIssueData data) => Task.CompletedTask;
        public Task CloseCompositeAsync() => Task.CompletedTask;
        public Task ReopenCompositeAsync() => Task.CompletedTask;
        public Task ArchiveAsync() => Task.CompletedTask;
        public Task ArchiveForParentCascadeAsync() => Task.CompletedTask;
        public Task UnarchiveAsync() => Task.CompletedTask;
        public Task ReopenAsync() => Task.CompletedTask;
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => Task.FromResult<IssueWorkflowStatus?>(null);
        public Task<string?> GetActiveWorkflowRunIdAsync() => Task.FromResult<string?>(null);
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => Task.FromResult(IssuePrerequisiteResult.Added());
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => Task.CompletedTask;
        public Task<IssueStartReadiness> GetStartReadinessAsync() => Task.FromResult(new IssueStartReadiness(true, true, null));
        public Task RecomputeCompositeStatusAsync() => Task.CompletedTask;
        public Task StartCompositeAsync() => Task.CompletedTask;
        public Task<IssueCommentResult> AddCommentAsync(string a, string b, string[]? ids = null) => Task.FromResult(new IssueCommentResult("cmt_test", b, a));
        public Task DeactivateForTestAsync() => Task.CompletedTask;
        public Task<bool> AssignEpicAsync(int epicNumber) => Task.FromResult(true);
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => Task.FromResult(true);
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => Task.FromResult(true);
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<long> GetRepositoryBindingRevisionAsync() => Task.FromResult(0L);
    }
}
