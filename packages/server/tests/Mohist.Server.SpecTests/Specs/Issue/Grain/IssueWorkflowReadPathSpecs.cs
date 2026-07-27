using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

/// <summary>
/// Covers the contract that <see cref="IssueGrain.GetWorkflowStatusAsync"/> is
/// a pure query: it MUST return the bound workflow run's projected
/// state without mutating the issue's own status, and without invoking any
/// reconciliation write (the read path cannot bring the issue to
/// <c>Done</c>). Issue → <c>Done</c> transitions are owned solely by the
/// <c>com.mohist.workflow.run.completed</c> event subscription
/// (<see cref="Mohist.Server.Issue.Subscriptions.IssueWorkflowCompletionHandler"/>).
///
/// Spec: <c>openspec/changes/issue-307/specs/issue-workflow-run-reference/spec.md#workflow-status-read-path-is-a-pure-query</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueWorkflowReadPathSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly MohistDbFixture _fixture;

    public IssueWorkflowReadPathSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_InProgressIssueWithCompletedRun_ReturnsCompletedStatusWithoutTransitioningIssue()
    {
        // Arrange: InProgress issue bound to a workflow run that has
        // already reached Completed. Pre-deletion this path would have
        // reconciled the issue to Done as a side-effect of the read.
        // Post-deletion the read path MUST return the completed
        // workflow view, keep the issue Stage as in_progress, and leave
        // the persisted row untouched.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectId = $"project_readpath_completed_{Guid.NewGuid():N}";
        const int issueNumber = 1;
        var workflowRunId = $"wr_readpath_completed_{Guid.NewGuid():N}";
        await SeedIssueAsync(db, projectId, issueNumber,
            status: IssueStatus.InProgress, workflowRunId: workflowRunId);
        await SeedCompletedWorkflowRunAsync(db, workflowRunId, projectId: projectId);

        var beforeRow = await db.Issues.AsNoTracking().FirstAsync(r => r.ProjectId == projectId && r.Number == issueNumber);

        // Swap the state store for a recording one: any SaveAsync call
        // during the read path is a regression. The path is documented
        // as pure-query; load still routes through the seeded DB row.
        var stateStore = new ReadOnlyTrackingStateStore(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IGrainFactory>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<IssueStore>());
        var grain = CreateGrain(scope.ServiceProvider, stateStore, projectId, issueNumber);

        // Act
        await grain.OnActivateAsync(CancellationToken.None);
        var status = await grain.GetWorkflowStatusAsync();

        // Assert: the workflow view reflects the terminal state.
        Assert.NotNull(status);
        Assert.NotNull(status!.Workflow);
        Assert.Equal("completed", status.Workflow!.Status);
        Assert.Equal(workflowRunId, status.WorkflowRunId);

        // Assert: the issue's projected Stage is still in_progress
        // (the read path does NOT reconcile it to Done).
        Assert.Equal("in_progress", status.Stage);

        // Assert: the persisted issue row is byte-for-byte unchanged —
        // no state mutation leaked into the read path.
        var afterRow = await db.Issues.AsNoTracking().FirstAsync(r => r.ProjectId == projectId && r.Number == issueNumber);
        Assert.Equal(beforeRow.State, afterRow.State);
        Assert.Equal(beforeRow.Status, afterRow.Status);
        Assert.Equal(beforeRow.WorkflowRunId, afterRow.WorkflowRunId);

        // Assert: the read path did not trigger any state-store write.
        // Loading the issue on activation is expected; saving is not.
        Assert.Empty(stateStore.SaveCalls);
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_RepeatedCalls_NeverMutateIssueField()
    {
        // A pure read path is, by definition, idempotent across
        // repeated invocations. Open it five times and verify the
        // persisted row never changes — no accumulation of writes, no
        // tick of updatedAt, no transition to Done.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectId = $"project_readpath_repeated_{Guid.NewGuid():N}";
        const int issueNumber = 1;
        var workflowRunId = $"wr_readpath_repeated_{Guid.NewGuid():N}";
        await SeedIssueAsync(db, projectId, issueNumber,
            status: IssueStatus.InProgress, workflowRunId: workflowRunId);
        await SeedCompletedWorkflowRunAsync(db, workflowRunId, projectId: projectId);

        var stateStore = new ReadOnlyTrackingStateStore(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IGrainFactory>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<IssueStore>());
        var grain = CreateGrain(scope.ServiceProvider, stateStore, projectId, issueNumber);
        await grain.OnActivateAsync(CancellationToken.None);

        var beforeRow = await db.Issues.AsNoTracking().FirstAsync(r => r.ProjectId == projectId && r.Number == issueNumber);

        for (var i = 0; i < 5; i++)
        {
            var status = await grain.GetWorkflowStatusAsync();
            Assert.NotNull(status);
            Assert.Equal("in_progress", status!.Stage);
            Assert.Equal("completed", status.Workflow!.Status);
        }

        var afterRow = await db.Issues.AsNoTracking().FirstAsync(r => r.ProjectId == projectId && r.Number == issueNumber);
        Assert.Equal(beforeRow.State, afterRow.State);
        Assert.Equal(beforeRow.Status, afterRow.Status);
        Assert.Empty(stateStore.SaveCalls);
    }

    [Fact]
    public async Task GetWorkflowStatusAsync_NoWorkflowRun_ReturnsNullAndDoesNotMutate()
    {
        // Edge case: an issue without a bound workflow run returns
        // null. The read path must not write either branch of the
        // if-null guard.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectId = $"project_readpath_norun_{Guid.NewGuid():N}";
        const int issueNumber = 1;
        await SeedIssueAsync(db, projectId, issueNumber,
            status: IssueStatus.Backlog, workflowRunId: null);

        var stateStore = new ReadOnlyTrackingStateStore(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IGrainFactory>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<IssueStore>());
        var grain = CreateGrain(scope.ServiceProvider, stateStore, projectId, issueNumber);

        await grain.OnActivateAsync(CancellationToken.None);
        var status = await grain.GetWorkflowStatusAsync();

        Assert.Null(status);
        Assert.Empty(stateStore.SaveCalls);
    }

    private IssueGrain CreateGrain(
        IServiceProvider services,
        IIssueStore stateStore,
        string projectId,
        int issueNumber)
    {
        return IssueGrain.ForDirectConstruction(
            GrainKey.Issue(new IssueKey(projectId, issueNumber)),
            stateStore,
            services.GetRequiredService<IssueWorkflowProfileRegistry>(),
            services.GetRequiredService<WorkflowQuerier>(),
            services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            services.GetRequiredService<IEventStore>(),
            services.GetRequiredService<IGrainFactory>(),
            services.GetRequiredService<IBackgroundTaskLauncher>(),
            services.GetRequiredService<IssueRepositoryResolver>(),
            services.GetRequiredService<WorkflowProfileManager>(),
            services.GetRequiredService<ProjectWorkflowProfileManager>(),
            services.GetRequiredService<IssueWorkflowProfileManager>(),
            services.GetRequiredService<AttachmentService>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IEnvironmentVariableProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<IssueGrain>>());
    }

    private static async Task SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        IssueStatus status,
        string? workflowRunId)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            WorkflowRunId = workflowRunId,
        };
        var json = IssueStore.Serialize(issue);
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            WorkflowRunId = workflowRunId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCompletedWorkflowRunAsync(
        MohistDbContext db,
        string workflowRunId,
        string projectId)
    {
        var run = new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: "read-path-test",
                CreatedAt: FixedNow,
                Labels: null,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                }),
            Status = WorkflowRunStatus.Completed,
            CurrentStageId = "integrate",
            StartedAt = FixedNow.AddMinutes(-30),
            CompletedAt = FixedNow,
            Stages = new List<StageRun>
            {
                new()
                {
                    Id = "integrate",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Completed,
                    Initialized = true,
                    Tasks = new List<TaskRun>(),
                    Checks = new List<StageCheck>(),
                },
            },
        };
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// <see cref="IStateStore{T}"/> test double that delegates
    /// <c>LoadAsync</c> to the underlying <see cref="IssueStore"/> (so
    /// the grain can load the seeded DB row) but records every
    /// <c>SaveAsync</c> call. The pure-query read path MUST never
    /// call <c>SaveAsync</c>; any call — state-only or
    /// events-aware — is a regression.
    /// </summary>
    private sealed class ReadOnlyTrackingStateStore : IIssueStore
    {
        private readonly IIssueStore _delegate;
        private readonly List<string> _saveCalls = [];

        public ReadOnlyTrackingStateStore(
            IDbContextFactory<MohistDbContext> dbFactory,
            IGrainFactory grainFactory,
            ILogger<IssueStore> log)
        {
            _delegate = new IssueStore(dbFactory, new NoopEventStore(), grainFactory, log);
        }

        public IReadOnlyList<string> SaveCalls => _saveCalls;

        public Task<DomainIssue?> LoadAsync(string key) => _delegate.LoadAsync(key);

        public Task SaveAsync(string key, DomainIssue state)
        {
            _saveCalls.Add($"{key}@{state.Status}");
            return _delegate.SaveAsync(key, state);
        }

        public Task SaveAsync(string key, DomainIssue state, IReadOnlyList<IssueEvent> events, CancellationToken ct = default)
        {
            _saveCalls.Add($"{key}@{state.Status}+events:{events.Count}");
            return _delegate.SaveAsync(key, state, events, ct);
        }

        public Task DeleteAsync(string key) => _delegate.DeleteAsync(key);
        public Task<IReadOnlyList<DomainIssue>> ListAsync() => _delegate.ListAsync();
    }
}
