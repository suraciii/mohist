using System.Collections.Concurrent;
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

public abstract class IssueWorkflowCompletionHandlerTestSupport
{
    protected static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    protected static CloudEvent BuildCompletedEvent(
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
                [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
            });

    protected static CloudEvent<IssueWorkStarted> BuildWorkStartedEvent(
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

    protected static async Task SeedIssueAsync(
        TestSqliteDatabase database,
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
        await using var db = database.CreateContext();
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
    protected static IssueQuerier NewIssueQuerier(IDbContextFactory<MohistDbContext> dbFactory) =>
        new(
            dbFactory,
            projects: null!,
            configService: null!,
            effectiveProfileResolver: null!,
            projectProfileManager: null!,
            loader: null!);

    protected static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();

    protected sealed class RecordingIssueGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly FakeTimeProvider _time;
        public List<RecordedCall> Calls { get; } = [];

        public RecordingIssueGrainFactory(IDbContextFactory<MohistDbContext> dbFactory, FakeTimeProvider time)
        {
            _dbFactory = dbFactory;
            _time = time;
        }

        public IStateStore<DomainIssue> IssueStore { get; } = new InMemoryStateStore<DomainIssue>();

        public IIssueGrain GetIssueGrain(string grainKey)
        {
            var stateStore = IssueStore;
            return new CompleteWorkRecordingGrain(grainKey, stateStore, _dbFactory, _time, Calls);
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
    protected sealed class CompleteWorkRecordingGrain : IIssueGrain
    {
        private readonly string _key;
        private readonly IStateStore<DomainIssue> _stateStore;
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly FakeTimeProvider _time;
        private readonly List<RecordedCall> _calls;

        public CompleteWorkRecordingGrain(
            string key,
            IStateStore<DomainIssue> stateStore,
            IDbContextFactory<MohistDbContext> dbFactory,
            FakeTimeProvider time,
            List<RecordedCall> calls)
        {
            _key = key;
            _stateStore = stateStore;
            _dbFactory = dbFactory;
            _time = time;
            _calls = calls;
        }

        private static RecordedCall RecordedCallFor(string grainKey, string workflowRunId)
        {
            Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(grainKey, out var projectId, out var issueNumber);
            return new RecordedCall(new IssueWorkflowRef(projectId, issueNumber), workflowRunId);
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

        public Task MarkDoneAsync() => throw new NotSupportedException();

        private async Task<DomainIssue?> LoadAsync(string key)
        {
            var fromMemory = await _stateStore.LoadAsync(key);
            if (fromMemory is not null) return fromMemory;
            await using var db = await _dbFactory.CreateDbContextAsync();
            Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(key, out var projectId, out var issueNumber);
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
            Mohist.Server.Infrastructure.Orleans.ScopedGrainKeyCodec.Parse(key, out var projectId, out var issueNumber);
            var row = await db.Issues.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == issueNumber);
            if (row is null) return;
            row.State = IssueStore.Serialize(state);
            await db.SaveChangesAsync();
        }

        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null) => throw new NotSupportedException();
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task CloseCompositeAsync() => throw new NotSupportedException();
        public Task ReopenCompositeAsync() => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task ArchiveForParentCascadeAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task RecomputeCompositeStatusAsync() => throw new NotSupportedException();
        public Task StartCompositeAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string author, string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task<bool> AssignEpicAsync(int epicNumber) => throw new NotSupportedException();
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();

        public Task<string?> GetActiveWorkflowRunIdAsync() => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<long> GetRepositoryBindingRevisionAsync() => throw new NotImplementedException();
    }

    protected sealed class ThrowingIssueGrainFactory : IGrainFactory
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

    protected sealed class ThrowingIssueGrain : IIssueGrain
    {
        public Task CompleteWorkAsync(string workflowRunId) =>
            throw new InvalidOperationException("simulated grain failure");
        public Task MarkDoneAsync() => throw new NotSupportedException();
        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null) => throw new NotSupportedException();
        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(UpdateIssueData data) => throw new NotSupportedException();
        public Task CloseCompositeAsync() => throw new NotSupportedException();
        public Task ReopenCompositeAsync() => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task ArchiveForParentCascadeAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task RecomputeCompositeStatusAsync() => throw new NotSupportedException();
        public Task StartCompositeAsync() => throw new NotSupportedException();
        public Task<IssueCommentResult> AddCommentAsync(string author, string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task<bool> AssignEpicAsync(int epicNumber) => throw new NotSupportedException();
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();

        public Task<string?> GetActiveWorkflowRunIdAsync() => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string repositoryRef, string? risk, bool isDraft, string[]? attachmentIds, string? workflowProfileId, int[]? prerequisiteNumbers, int? parentIssueNumber, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(Mohist.Server.Issue.Grains.IssueChangeRepositoryCommand command, string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(string commandId, long? expectedRevision) => throw new NotImplementedException();
        public Task<long> GetRepositoryBindingRevisionAsync() => throw new NotImplementedException();
    }
}

file sealed class InMemoryStateStore<T> : IStateStore<T> where T : class
{
    private readonly ConcurrentDictionary<string, T> _data = new();

    public Task<T?> LoadAsync(string key)
    {
        _data.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task SaveAsync(string key, T state)
    {
        _data[key] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        _data.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<T>> ListAsync()
    {
        IReadOnlyList<T> result = _data.Values.ToList();
        return Task.FromResult(result);
    }
}
