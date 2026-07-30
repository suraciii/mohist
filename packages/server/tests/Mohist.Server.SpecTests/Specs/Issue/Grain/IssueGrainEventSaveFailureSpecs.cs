using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

/// <summary>
/// Covers the fault-isolation contract for transactional event append:
/// when an event-aware save fails and the store rolls back, the in-memory
/// issue aggregate is already mutated. The activation MUST be quarantined so
/// a subsequent command on the same activation cannot persist the dirty
/// state through the no-events path. Spec: openspec/changes/issue-361.
/// </summary>
[Collection("MohistDb")]
public class IssueGrainEventSaveFailureSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueGrainEventSaveFailureSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EventAwareSaveFailure_QuarantinesActivation_SubsequentCommandMustReload()
    {
        const string projectId = "proj-fault";
        const int issueNumber = 901;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        await using (var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContext())
        {
            await SeedIssueAsync(db, projectId, issueNumber, IssueStatus.Cancelled);
        }

        var failingStore = new FailingIssueStore(
            scopeFactory: _fixture.Services,
            failEventsSaveOnce: true);

        IssueGrain grain;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            grain = CreateGrain(scope.ServiceProvider, failingStore, GrainKey.Issue(new IssueKey(projectId, issueNumber)));
            await grain.OnActivateAsync(CancellationToken.None);
        }

        // First save fails on the event-aware path. The store's transaction
        // rolls back; the activation is marked reload-required.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.UpdateAsync("Changed", null));

        // The dirty in-memory aggregate must not be salvageable through a
        // later command on this activation. EnsureIssue() must reject it.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.UpdateAsync("Changed again", null));
        Assert.Contains("must reload", second.Message);

        // The persisted row is untouched: the store rolled back, and the
        // quarantined activation never reached a second successful save.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        await using (var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContext())
        {
            var row = await db.Issues.AsNoTracking().FirstAsync(i => i.ProjectId == projectId && i.Number == issueNumber);
            var issue = IssueStore.Deserialize(row.State ?? "{}");
            Assert.NotNull(issue);
            Assert.Equal(IssueStatus.Cancelled, issue.Status);
        }
    }

    [Fact]
    public async Task EventAwareSaveFailure_QuarantinesActivation_CompleteWorkMustReload()
    {
        // CompleteWorkAsync historically bypassed EnsureIssue() and read
        // _issue directly, so after a rolled-back event-aware save it could
        // persist mutated in-memory state on the same dirty activation. The
        // reload-required guard must reject it before it touches _issue.
        const string projectId = "proj-fault";
        const int issueNumber = 902;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        await using (var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContext())
        {
            await SeedIssueAsync(db, projectId, issueNumber, IssueStatus.Cancelled);
        }

        var failingStore = new FailingIssueStore(
            scopeFactory: _fixture.Services,
            failEventsSaveOnce: true);

        IssueGrain grain;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            grain = CreateGrain(scope.ServiceProvider, failingStore, GrainKey.Issue(new IssueKey(projectId, issueNumber)));
            await grain.OnActivateAsync(CancellationToken.None);
        }

        // Quarantine the activation via a failing save.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.UpdateAsync("Changed", null));

        // The delivery path must not mutate/persist on the dirty activation.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.CompleteWorkAsync("wr-anything"));
        Assert.Contains("must reload", ex.Message);
    }

    [Fact]
    public async Task EventAwareSaveFailure_QuarantinesActivation_EpicAssignmentMustNotPersistDirtyState()
    {
        const string projectId = "proj-fault";
        const int issueNumber = 903;

        await using (var scope = _fixture.Services.CreateAsyncScope())
        await using (var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContext())
        {
            await SeedIssueAsync(db, projectId, issueNumber, IssueStatus.Cancelled);
        }

        var failingStore = new FailingIssueStore(_fixture.Services, failEventsSaveOnce: true);
        IssueGrain grain;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            grain = CreateGrain(scope.ServiceProvider, failingStore, GrainKey.Issue(new IssueKey(projectId, issueNumber)));
            await grain.OnActivateAsync(CancellationToken.None);
        }

        // issue-417: reopen now runs under the coordinator fence via
        // ReopenWithReceiptAsync (which resolves the target through the
        // project grain and so cannot be exercised from this isolated
        // IssueGrain instance). The quarantine-under-test is the save path
        // itself, so trigger the event-aware save via UpdateAsync instead —
        // the same path the sibling 902 spec uses.
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.UpdateAsync("Changed", null));
        var eventAwareSaves = failingStore.EventAwareSaveAttempts;
        var stateOnlySaves = failingStore.StateOnlySaveAttempts;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AssignEpicAsync(1));

        Assert.Contains("must reload", ex.Message);
        Assert.Equal(eventAwareSaves, failingStore.EventAwareSaveAttempts);
        Assert.Equal(stateOnlySaves, failingStore.StateOnlySaveAttempts);
    }

    private static IssueGrain CreateGrain(
        IServiceProvider services,
        IIssueStore stateStore,
        string grainKey)
    {
        var identity = GrainTestContext.Create(grainKey);
        return new IssueGrain(
            identity.Context,
            identity.Runtime,
            stateStore,
            services.GetRequiredService<IssueWorkflowProfileRegistry>(),
            services.GetRequiredService<WorkflowQuerier>(),
            services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            services.GetRequiredService<IEventStore>(),
            services.GetRequiredService<IGrainFactory>(),
            services.GetRequiredService<IBackgroundTaskLauncher>(),
            services.GetRequiredService<IssueRepositoryResolver>(),
            services.GetRequiredService<WorkflowProfileManager>(),
            services.GetRequiredService<WorkflowPromptResolver>(),
            services.GetRequiredService<ProjectWorkflowProfileManager>(),
            services.GetRequiredService<IssueVariableStore>(),
            services.GetRequiredService<AttachmentService>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IEnvironmentVariableProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<IssueGrain>>(),
            services.GetRequiredService<IWorkflowProfileProvider>());
    }

    private static async Task SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        IssueStatus status)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            WorkflowRunId = null,
            RepositoryRef = "main",
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Wraps the real <see cref="IssueStore"/>. <c>LoadAsync</c> and the
    /// no-events save delegate normally; the event-aware save throws once
    /// (one-shot fault injection) then delegates to the real store so a
    /// reloaded activation can persist after the quarantine.
    /// </summary>
    private sealed class FailingIssueStore : IIssueStore
    {
        private readonly IIssueStore _delegate;
        private int _eventsSaveFailures;

        public int EventAwareSaveAttempts { get; private set; }
        public int StateOnlySaveAttempts { get; private set; }

        public FailingIssueStore(IServiceProvider scopeFactory, bool failEventsSaveOnce)
        {
            _delegate = new IssueStore(
                scopeFactory.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
                scopeFactory.GetRequiredService<IEventStore>(),
                scopeFactory.GetRequiredService<IGrainFactory>(),
                scopeFactory.GetRequiredService<ILoggerFactory>().CreateLogger<IssueStore>(),
                scopeFactory.GetRequiredService<IBackgroundTaskLauncher>());
            _eventsSaveFailures = failEventsSaveOnce ? 1 : 0;
        }

        public Task<DomainIssue?> LoadAsync(string key) => _delegate.LoadAsync(key);

        public Task SaveAsync(string key, DomainIssue state)
        {
            StateOnlySaveAttempts++;
            return _delegate.SaveAsync(key, state);
        }

        public async Task SaveAsync(string key, DomainIssue state, IReadOnlyList<IssueEvent> events, CancellationToken ct = default)
        {
            EventAwareSaveAttempts++;
            if (_eventsSaveFailures > 0)
            {
                _eventsSaveFailures--;
                throw new InvalidOperationException("simulated event write failed");
            }
            await _delegate.SaveAsync(key, state, events, ct);
        }

        public Task DeleteAsync(string key) => _delegate.DeleteAsync(key);
        public Task<IReadOnlyList<DomainIssue>> ListAsync() => _delegate.ListAsync();
    }
}
