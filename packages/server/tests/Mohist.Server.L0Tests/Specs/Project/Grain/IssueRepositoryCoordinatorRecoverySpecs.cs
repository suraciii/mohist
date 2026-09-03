using System.Collections.Concurrent;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Project.Grain;

public partial class IssueRepositoryCoordinatorSpecs
{
    [Fact]
    public async Task LostResponse_SameCommandReplay_ReturnsAlreadyAppliedWithoutReMutation()
    {
        var (projectId, _) = await SeedProjectAsync();

        var coordinator = NewCoordinator(projectId);
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var commandId = $"create:{projectId}:{number}";

        var first = await coordinator.CreateIssueAsync(
            BuildCreatePayload(projectId, number, "web"),
            commandId,
            null);
        Assert.Equal(IssueRepositoryBindingResultCode.Applied, first.Code);

        // Replay the same commandId — the participant returns
        // AlreadyApplied so the coordinator reports it without
        // touching Issue or Project state.
        var second = await coordinator.CreateIssueAsync(
            BuildCreatePayload(projectId, number, "web"),
            commandId,
            null);
        Assert.Equal(IssueRepositoryBindingResultCode.AlreadyApplied, second.Code);

        var projectAfter = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Equal(2, projectAfter!.Repositories.Count);
    }

    [Fact]
    public async Task DeactivationAfterParticipantCommit_ReplayReturnsAlreadyApplied()
    {
        var (projectId, _) = await SeedProjectAsync();

        var coordinator = NewCoordinator(projectId);
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var commandId = $"create:{projectId}:{number}";

        var first = await coordinator.CreateIssueAsync(
            BuildCreatePayload(projectId, number, "web"),
            commandId,
            null);
        Assert.Equal(IssueRepositoryBindingResultCode.Applied, first.Code);

        // Force the coordinator activation to deactivate; the
        // persisted state (fence cleared, Issue already has the
        // receipt) survives. A fresh call must observe the
        // participant's persisted receipt and return AlreadyApplied
        // without re-mutating state.
        await TestLifecycle.Deactivate(coordinator);

        var replayed = await coordinator.CreateIssueAsync(
            BuildCreatePayload(projectId, number, "web"),
            commandId,
            null);
        Assert.Equal(IssueRepositoryBindingResultCode.AlreadyApplied, replayed.Code);

        // The Issue must exist with the binding committed: read the
        // issue via the IssueGrain's IIssueStore-backed state. A
        // missing issue would surface as KeyNotFoundException on
        // EnsureIssue().
        var issueGrain = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, number));
        var readiness = await issueGrain.GetStartReadinessAsync();
        Assert.NotNull(readiness);
    }

    [Fact]
    public async Task RepositoryRemoval_WithNonTerminalIssue_ReturnsRepositoryInUseWithoutFence()
    {
        var (projectId, _) = await SeedProjectAsync();
        await SeedIssueAsync(projectId, "web");

        var coordinator = NewCoordinator(projectId);
        var result = await coordinator.RemoveRepositoryAsync(
            new RepositoryCommandPayload.Remove(projectId, "web"),
            commandId: $"remove:web:{Guid.NewGuid():N}",
            expectedRevision: null);

        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryInUse, result.Code);

        var projectAfter = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Contains(projectAfter!.Repositories, r => r.Name == "web");
    }

    [Fact]
    public async Task RepositoryRemoval_UsesDeclaredNonAsciiNameForBlockerLookup()
    {
        var (projectId, _) = await SeedProjectAsync(secondaryName: "Å");
        await SeedIssueAsync(projectId, "Å");

        var result = await NewCoordinator(projectId).RemoveRepositoryAsync(
            new RepositoryCommandPayload.Remove(projectId, "å"),
            commandId: $"remove:å:{Guid.NewGuid():N}",
            expectedRevision: null);

        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryInUse, result.Code);
        Assert.Equal("Å", result.RepositoryName);
        var project = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Contains(project!.Repositories, repository => repository.Name == "Å");
    }

    [Fact]
    public async Task CoordinatorCreate_WithUnknownRepository_IsRejectedWithoutCreatingAnIssue()
    {
        var (projectId, _) = await SeedProjectAsync();
        var result = await NewCoordinator(projectId).CreateIssueAsync(
            BuildCreatePayload(projectId, 1, "ghost"),
            commandId: $"create:{projectId}:1",
            expectedRevision: null);

        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryUnknown, result.Code);
        var issue = _grains.GetGrain<IIssueGrain>(IssueGrainKey(projectId, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => issue.GetStartReadinessAsync());
    }

    [Fact]
    public async Task CoordinatorReopen_AfterTargetDeletion_IsRejectedWithoutReopening()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await SeedIssueAsync(projectId, "web");
        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CancelAsync();
        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("web");

        var coordinator = NewCoordinator(projectId);
        var first = await coordinator.ReopenAsync(BuildReopenPayload(projectId, issueNumber, "web"), $"reopen:{issueKey}", null);
        var second = await coordinator.ReopenAsync(BuildReopenPayload(projectId, issueNumber, "web"), $"reopen:{issueKey}:retry", null);

        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryMissingOnReopen, first.Code);
        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryMissingOnReopen, second.Code);
    }

    [Fact]
    public async Task CoordinatorCreate_WithInvalidAttachments_ClearsFenceAndAllowsSubsequentOperation()
    {
        var (projectId, _) = await SeedProjectAsync();
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();

        var createPayload = new RepositoryCommandPayload.Create(
            ProjectId: projectId,
            IssueNumber: number,
            RepositoryName: "web",
            Title: $"Issue #{number}",
            Body: null,
            Labels: null,
            Priority: null,
            Risk: null,
            IsDraft: false,
            AttachmentIds: new[] { "att_nonexistent" },
            WorkflowProfileId: null,
            PrerequisiteNumbers: null);

        await Assert.ThrowsAnyAsync<Exception>(() => NewCoordinator(projectId).CreateIssueAsync(
            createPayload,
            commandId: $"create:{projectId}:{number}",
            expectedRevision: null));

        var secondNumber = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var secondResult = await NewCoordinator(projectId).CreateIssueAsync(
            BuildCreatePayload(projectId, secondNumber, "web"),
            commandId: $"create:{projectId}:{secondNumber}",
            expectedRevision: null);

        Assert.Equal(IssueRepositoryBindingResultCode.Applied, secondResult.Code);
    }

}
