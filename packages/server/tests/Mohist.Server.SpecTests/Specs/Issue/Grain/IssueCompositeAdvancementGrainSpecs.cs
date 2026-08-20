using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

/// <summary>
/// Retains the Orleans, persistence, and event-delivery boundaries for
/// composite advancement. Pure child-selection and state-transition
/// matrices are owned by unit tests.
/// <list type="bullet">
/// <item>start fan-out: a parent's <c>StartCompositeAsync</c> calls
///   <c>StartWorkAsync</c> on every startable child in parallel.</item>
/// <item>recompute idempotency: redelivering
///   <c>RecomputeCompositeStatusAsync</c> converges the parent to the same
///   state and emits no duplicate
///   <see cref="IssueCompositeStatusChanged"/> events.</item>
/// <item>restart recompute: a fresh activation reloads the parent and resumes
///   fan-out from its persisted child snapshot.</item>
/// </list>
/// </summary>
[Collection("IssueCompositeIntegration")]
public class IssueCompositeAdvancementGrainSpecs
{
    private readonly IsolatedMohistIntegrationFixture _fixture;

    public IssueCompositeAdvancementGrainSpecs(IsolatedMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private IGrainFactory Grains => _fixture.Grains;
    private IServiceProvider Services => _fixture.Services;

    [Fact]
    public async Task StartCompositeAsync_FansOutStartToEveryStartableChild_Concurrently()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var children = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var child = await CreateIssueAsync(projectId, $"Child {i}", isDraft: false);
            await AttachChildAsync(projectId, child, parent.Number);
            children.Add(child.Number);
        }

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        await parentGrain.StartCompositeAsync();

        await DispatchEventsAsync();
        foreach (var childNumber in children)
        {
            var view = await GetIssueReadModelAsync(projectId, childNumber);
            Assert.NotNull(view);
            Assert.Equal("in_progress", view!.Status);
            Assert.NotNull(view.WorkflowRunId);
        }
    }

    [Fact]
    public async Task StartCompositeAsync_OnDraftParent_ThrowsDraftBlocker()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent Draft", isDraft: true);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        var ex = await Assert.ThrowsAsync<IssueStartBlockedException>(
            () => parentGrain.StartCompositeAsync());
        Assert.IsType<IssueStartBlocker.Draft>(ex.Blocker);
    }

    [Fact]
    public async Task StartCompositeAsync_OnParentWithZeroChildren_NoOpsAndDoesNotThrow()
    {
        // The issue-419 design tolerates a vanishing-children race: the
        // recompute triggered by the parent-changed event for the last
        // detach may engage StartCompositeAsync after the children
        // snapshot is already empty. The grain must no-op rather than
        // throw — subsequent aggregate transitions are guarded by their
        // own empty-snapshot check.
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        await parentGrain.StartCompositeAsync();

        // Parent stays Backlog: there are no children to drive it to
        // InProgress, and StartCompositeAsync must not throw.
        var view = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.NotNull(view);
        Assert.Equal("backlog", view!.Status);
        Assert.Null(view.WorkflowRunId);
    }

    [Fact]
    public async Task RecomputeCompositeStatusAsync_IsIdempotent_AcrossRedeliveries()
    {
        // Drive a recompute that produces a status-change event, then
        // redeliver the same recompute multiple times. The aggregate's
        // no-op-if-already-at-target guard means the second and third
        // deliveries emit no duplicate events.
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var childA = await CreateIssueAsync(projectId, "A", isDraft: false);
        var childB = await CreateIssueAsync(projectId, "B", isDraft: false);
        await AttachChildAsync(projectId, childA, parent.Number);
        await AttachChildAsync(projectId, childB, parent.Number);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        var aGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, childA.Number)));
        var bGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, childB.Number)));

        // Start the parent first so it transitions to InProgress via
        // composite advancement. Each child gets its own workflow run.
        await parentGrain.StartCompositeAsync();
        await DispatchEventsAsync();

        var wrA = await aGrain.GetActiveWorkflowRunIdAsync();
        var wrB = await bGrain.GetActiveWorkflowRunIdAsync();
        Assert.NotNull(wrA);
        Assert.NotNull(wrB);

        await aGrain.CompleteWorkAsync(wrA!);
        await bGrain.CompleteWorkAsync(wrB!);

        await DispatchEventsAsync();

        // First recompute: parent transitions InProgress -> Done. Event emitted.
        await parentGrain.RecomputeCompositeStatusAsync();
        // Subsequent recomputes: parent already Done, no transition.
        await parentGrain.RecomputeCompositeStatusAsync();
        await parentGrain.RecomputeCompositeStatusAsync();

        var events = await LoadIssueEventsAsync(projectId, parent.Number);
        var statusChangeCount = events.Count(e =>
            string.Equals(e.Envelope.Type, EventCatalog.ReverseDns.IssueCompositeStatusChanged, StringComparison.Ordinal));
        Assert.Equal(1, statusChangeCount);
    }

    [Fact]
    public async Task RecomputeCompositeStatusAsync_OnBacklogParent_NoFanOut()
    {
        // Attaching a child to a Backlog parent must NOT start the child —
        // the user must explicitly run `mo issue start`. The fan-out is
        // gated on target == InProgress (design D6 step 4).
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        // Recompute before attach: nothing to do.
        await parentGrain.RecomputeCompositeStatusAsync();

        await AttachChildAsync(projectId, child, parent.Number);
        await DispatchEventsAsync();

        // Recompute triggered by parent-changed: parent is still Backlog,
        // target stays Backlog, no fan-out.
        await parentGrain.RecomputeCompositeStatusAsync();

        var childView = await GetIssueReadModelAsync(projectId, child.Number);
        Assert.Equal("backlog", childView!.Status);
        Assert.Null(childView.WorkflowRunId);

        var parentView = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.Equal("backlog", parentView!.Status);
    }

    [Fact]
    public async Task RecomputeCompositeStatusAsync_OnParentWithZeroChildren_NoOps()
    {
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        // No children exist; recompute is a no-op.
        await parentGrain.RecomputeCompositeStatusAsync();

        var view = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.Equal("backlog", view!.Status);
    }

    [Fact]
    public async Task StartWorkAsync_OnParent_AggregatesAndFansOut()
    {
        // Regression guard: StartWorkAsync on a parent must route into
        // StartCompositeAsync (no is_parent blocker).
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        var grain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        var returnedWrId = await grain.StartWorkAsync();

        // The composite path returns an empty string for the run id
        // (the parent never owns one).
        Assert.Equal(string.Empty, returnedWrId);

        await DispatchEventsAsync();
        var parentView = await GetIssueReadModelAsync(projectId, parent.Number);
        Assert.Equal("in_progress", parentView!.Status);
        Assert.Null(parentView.WorkflowRunId);

        var childView = await GetIssueReadModelAsync(projectId, child.Number);
        Assert.Equal("in_progress", childView!.Status);
        Assert.NotNull(childView.WorkflowRunId);
    }

    [Fact]
    public async Task StartCompositeAsync_RestartAfterActivationLoss_RecoversFromSnapshot()
    {
        // Design D6 risk note: a parent grain activation loss between
        // status change and fan-out must be recovered by the next
        // recompute. This spec simulates that: it creates a parent that
        // already transitioned to InProgress (via a first activation),
        // then drives a fresh recompute call on the same parent number
        // (which is a fresh activation if the prior one had been
        // deactivated). The recompute must converge the parent to its
        // aggregated status and fan-out newly-startable children.
        var projectId = await CreateProjectAsync();
        var parent = await CreateIssueAsync(projectId, "Parent", isDraft: false);
        var child = await CreateIssueAsync(projectId, "Child", isDraft: false);
        await AttachChildAsync(projectId, child, parent.Number);

        var parentGrain = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));

        await parentGrain.StartCompositeAsync();
        await parentGrain.Deactivate();

        // Fresh activation loads the parent from the store (status
        // already InProgress) and runs recompute. The fan-out engages
        // because target == InProgress.
        var freshParent = Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, parent.Number)));
        await freshParent.RecomputeCompositeStatusAsync();

        await DispatchEventsAsync();
        var childView = await GetIssueReadModelAsync(projectId, child.Number);
        Assert.Equal("in_progress", childView!.Status);
        Assert.NotNull(childView.WorkflowRunId);
    }

    private async Task<string> CreateProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = Grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync($"mohist-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return id;
    }

    private async Task<(int Number, string IssueKey)> CreateIssueAsync(string projectId, string title, bool isDraft)
    {
        var number = await Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = Grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, title, null, null, null, isDraft: isDraft);
        return (number, issueKey);
    }

    private async Task AttachChildAsync(string projectId, (int Number, string IssueKey) child, int parentNumber)
    {
        var grain = Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, child.Number)));
        await grain.UpdateFullAsync(new UpdateIssueData(
            PresentFields: new HashSet<string>(StringComparer.Ordinal) { nameof(UpdateIssueData.ParentIssueNumber) },
            ParentIssueNumber: parentNumber));
    }

    private async Task DispatchEventsAsync()
    {
        await Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        using var scope = Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetAsync(projectId, number);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> LoadIssueEventsAsync(string projectId, int issueNumber)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventStore>();
        return await store.ListIssueEventsAsync(projectId, issueNumber, 200);
    }
}
