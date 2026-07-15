using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Collection("EpicRecovery")]
public class EpicIssueAffiliationDispatcherSpecs
{
    private const string ProjectId = "project_recovery";
    private const string IssueId = "issue_recovery";
    private readonly EpicRecoveryFixture _fixture;

    public EpicIssueAffiliationDispatcherSpecs(EpicRecoveryFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task DispatchAsync_ReappliesCurrentMembershipWhenOldDeliveryResumesAfterNewApply()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedEpicAsync(ProjectId, "epic_old", "running");
        await _fixture.SeedEpicAsync(ProjectId, "epic_new", "running");
        await _fixture.SeedIssueAsync(ProjectId, IssueId, 1, IssueStatus.Backlog);
        await _fixture.SeedLinkAsync(ProjectId, "epic_old", IssueId, 1);
        await _fixture.SeedActiveLinkAsync(ProjectId, "epic_old", IssueId, 1);
        var firstCommandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grains = new RaceAffiliationGrainFactory(
            _fixture.DbFactory,
            IssueId,
            firstCommandStarted,
            releaseFirstCommand);
        var dispatcher = new EpicIssueAffiliationDispatcher(grains, _fixture.DbFactory);
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = ProjectId,
        };

        var oldDelivery = dispatcher.DispatchAsync("old-event", extensions, IssueId, CancellationToken.None);
        await firstCommandStarted.Task;

        await ReplaceMembershipAsync("epic_old", "epic_new");
        await dispatcher.DispatchAsync("new-event", extensions, IssueId, CancellationToken.None);

        releaseFirstCommand.SetResult();
        await oldDelivery;

        Assert.Equal("epic_new", await _fixture.GetIssueEpicIdAsync(ProjectId, IssueId));
        Assert.Equal(["epic_old", "epic_new", "epic_new"], grains.Commands);
        Assert.Equal(["epic_new", "epic_old", "epic_new"], grains.Writes);
    }

    private async Task ReplaceMembershipAsync(string oldEpicId, string newEpicId)
    {
        await using var db = _fixture.CreateDbContext();
        var oldLinks = await db.EpicIssues
            .Where(row => row.ProjectId == ProjectId && row.EpicId == oldEpicId && row.IssueId == IssueId)
            .ToListAsync();
        var active = await db.EpicActiveIssues
            .Where(row => row.ProjectId == ProjectId && row.IssueId == IssueId)
            .ToListAsync();
        db.EpicIssues.RemoveRange(oldLinks);
        db.EpicActiveIssues.RemoveRange(active);
        db.EpicIssues.Add(new EpicIssueRow
        {
            ProjectId = ProjectId,
            EpicId = newEpicId,
            IssueId = IssueId,
            IssueNumber = 1,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
        });
        db.EpicActiveIssues.Add(new EpicActiveIssueRow
        {
            ProjectId = ProjectId,
            EpicId = newEpicId,
            IssueId = IssueId,
            IssueNumber = 1,
            CreatedAt = _fixture.TimeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    private sealed class RaceAffiliationGrainFactory : IGrainFactory
    {
        private readonly RaceAffiliationIssueGrain _issue;

        public RaceAffiliationGrainFactory(
            IDbContextFactory<MohistDbContext> dbFactory,
            string issueId,
            TaskCompletionSource firstCommandStarted,
            TaskCompletionSource releaseFirstCommand)
        {
            _issue = new RaceAffiliationIssueGrain(
                dbFactory,
                issueId,
                firstCommandStarted,
                releaseFirstCommand);
        }

        public IReadOnlyList<string?> Commands => _issue.Commands;
        public IReadOnlyList<string?> Writes => _issue.Writes;

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)_issue;
            throw new NotSupportedException(typeof(TGrainInterface).Name);
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
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
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

    private sealed class RaceAffiliationIssueGrain : IIssueGrain
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly string _issueId;
        private readonly TaskCompletionSource _firstCommandStarted;
        private readonly TaskCompletionSource _releaseFirstCommand;

        public RaceAffiliationIssueGrain(
            IDbContextFactory<MohistDbContext> dbFactory,
            string issueId,
            TaskCompletionSource firstCommandStarted,
            TaskCompletionSource releaseFirstCommand)
        {
            _dbFactory = dbFactory;
            _issueId = issueId;
            _firstCommandStarted = firstCommandStarted;
            _releaseFirstCommand = releaseFirstCommand;
        }

        public List<string?> Commands { get; } = [];
        public List<string?> Writes { get; } = [];

        public async Task SetEpicAffiliationAsync(string? epicId)
        {
            Commands.Add(epicId);
            if (Commands.Count == 1)
            {
                _firstCommandStarted.SetResult();
                await _releaseFirstCommand.Task;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.Issues.SingleAsync(issue => issue.IssueId == _issueId);
            var issue = IssueStore.Deserialize(row.State) ?? throw new InvalidOperationException("Issue state was missing.");
            issue.SetEpicId(epicId);
            row.State = IssueStore.Serialize(issue);
            await db.SaveChangesAsync();
            Writes.Add(epicId);
        }

        public Task<string> StartWorkAsync(WorkflowProjectContext? project = null) => throw new NotSupportedException();
        public Task EnsureWorkflowBindingAsync(string workflowRunId) => throw new NotSupportedException();
        public Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null) => throw new NotSupportedException();
        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
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
    }
}
