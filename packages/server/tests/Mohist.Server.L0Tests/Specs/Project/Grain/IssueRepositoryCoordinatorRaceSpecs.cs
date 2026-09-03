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
    public async Task Create_RaceDelete_FirstWins_DeleteWaits_AndBlocksBecauseBindingExists()
    {
        var (projectId, _) = await SeedProjectAsync();

        var coordinator = NewCoordinator(projectId);
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var createCommandId = $"create:{projectId}:{number}";
        var createPayload = BuildCreatePayload(projectId, number, "web");

        var fenceGate = InstallParticipantProbe();
        using var _ = CoordinatorProbe.Install((kind, pid, cmd) =>
            (kind, cmd) == (CoordinatorProbeKind.Create, createCommandId)
                ? SignalAndBlockAsync(fenceGate.FencePersisted, fenceGate.ReleaseParticipant.Task)
                : Task.CompletedTask);

        var createTask = coordinator.CreateIssueAsync(createPayload, createCommandId, null);

        // Allow the coordinator to persist the fence and reach the
        // participant-blocked probe point.
        await WaitForAsync(fenceGate.FencePersisted.Task, TimeSpan.FromSeconds(5));

        // Release the create so it can commit; once committed, the
        // binding is in place and a subsequent deletion must observe
        // it via the blocker query.
        fenceGate.ReleaseParticipant.SetResult();
        var createResult = await createTask;
        Assert.Equal(IssueRepositoryBindingResultCode.Applied, createResult.Code);

        // Deletion arriving after the create commits: the issue is
        // bound to "web", so the blocker query fires and the
        // deletion is rejected with RepositoryInUse.
        var deleteResult = await coordinator.RemoveRepositoryAsync(
            new RepositoryCommandPayload.Remove(projectId, "web"),
            commandId: $"remove:web:{Guid.NewGuid():N}",
            expectedRevision: null);
        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryInUse, deleteResult.Code);

        var projectAfter = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Contains(projectAfter!.Repositories, r => r.Name == "web");
    }

    [Fact]
    public async Task Reassign_RaceDelete_ReassignBlockedByPostStart_LeavesBindingUnchanged()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueId, issueNumber) = await SeedIssueAsync(projectId, "web");

        // Start the workflow so the binding becomes locked. The
        // start path resolves the target declaration atomically with
        // setting HasWorkflowStarted, so the post-start reassignment
        // cannot race the deletion.
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.StartWorkAsync();

        var coordinator = NewCoordinator(projectId);
        // "server" is the project's default repository (seeded by
        // SeedProjectAsync), so the change target resolves to a
        // declared repository and the participant's unknown-rejection
        // path stays closed.
        var changePayload = BuildChangePayload(projectId, issueNumber, "server");
        var changeResult = await coordinator.ChangeRepositoryAsync(
            changePayload,
            commandId: $"change:{issueId}:{Guid.NewGuid():N}",
            expectedRevision: null);

        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryLocked, changeResult.Code);

        // Deletion is allowed: the issue is in-progress so the
        // blocker query fires and the deletion is rejected with
        // RepositoryInUse without mutating Project state.
        var deleteResult = await coordinator.RemoveRepositoryAsync(
            new RepositoryCommandPayload.Remove(projectId, "web"),
            commandId: $"remove:web:{Guid.NewGuid():N}",
            expectedRevision: null);
        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryInUse, deleteResult.Code);

        var projectAfter = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Contains(projectAfter!.Repositories, r => r.Name == "web");
    }

    [Fact]
    public async Task Reopen_RaceDelete_DeleteBlocks_BecauseReopenRestoresBinding()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueId, issueNumber) = await SeedIssueAsync(projectId, "web");

        // Drive the issue to terminal cancelled so a reopen can
        // restore the binding.
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CancelAsync();

        var coordinator = NewCoordinator(projectId);
        var reopenCommandId = $"reopen:{issueId}:{Guid.NewGuid():N}";
        var reopenPayload = BuildReopenPayload(projectId, issueNumber, "web");

        var fenceGate = InstallParticipantProbe();
        using var _ = CoordinatorProbe.Install((kind, pid, cmd) =>
            (kind, cmd) == (CoordinatorProbeKind.Reopen, reopenCommandId)
                ? SignalAndBlockAsync(fenceGate.FencePersisted, fenceGate.ReleaseParticipant.Task)
                : Task.CompletedTask);

        var reopenTask = coordinator.ReopenAsync(reopenPayload, reopenCommandId, null);
        await WaitForAsync(fenceGate.FencePersisted.Task, TimeSpan.FromSeconds(5));

        // Release the reopen so it can commit; once the reopen
        // commits, the issue returns to backlog with its binding,
        // and a subsequent deletion must observe the in-flight
        // binding via the blocker query and reject with
        // RepositoryInUse.
        fenceGate.ReleaseParticipant.SetResult();
        var reopenResult = await reopenTask;
        Assert.Equal(IssueRepositoryBindingResultCode.Applied, reopenResult.Code);

        // Deletion arriving after the reopen commits: the issue is
        // backlog and bound to "web", so the blocker query fires
        // and the deletion is rejected.
        var deleteResult = await coordinator.RemoveRepositoryAsync(
            new RepositoryCommandPayload.Remove(projectId, "web"),
            commandId: $"remove:web:{Guid.NewGuid():N}",
            expectedRevision: null);
        Assert.Equal(IssueRepositoryBindingResultCode.RepositoryInUse, deleteResult.Code);

        var projectAfter = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Contains(projectAfter!.Repositories, r => r.Name == "web");
    }

}
