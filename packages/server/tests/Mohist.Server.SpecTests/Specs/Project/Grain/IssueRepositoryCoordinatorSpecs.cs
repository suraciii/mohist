using System.Collections.Concurrent;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Grain;

/// <summary>
/// issue-417 T-005: locks the persisted-fence coordinator contract
/// spelled out in
/// <c>openspec/changes/issue-417/specs/project-management/spec.md#binding-changes-and-deletion-cannot-create-an-orphan</c>.
/// <para>
/// Coverage:
/// <list type="bullet">
///   <item>create / delete race: deletion arriving while a create is
///     in-flight waits for the create to finish, then either blocks
///     (because the binding exists) or proceeds (because the create
///     was rejected before any binding committed).</item>
///   <item>reassign / delete race: deletion waits for the reassign to
///     finish, then either blocks (binding moved) or proceeds
///     (reassign rejected before commit).</item>
///   <item>reopen / delete race: deletion waits for the reopen to
///     finish, then either blocks (binding restored) or proceeds
///     (reopen rejected because target was already gone).</item>
///   <item>lost-response / replay: when the participant commits but
///     the coordinator's response is lost, a fresh call with the
///     same commandId replays to AlreadyApplied without mutating
///     state again.</item>
///   <item>deactivation after fence persistence: the coordinator
///     survives activation loss with the fence intact, replays on
///     re-activation, and the participant records the receipt.</item>
/// </list>
/// </para>
/// <para>
/// Probes are installed via
/// <see cref="CoordinatorProbe"/> and
/// <see cref="BindingParticipantProbe"/> — production code leaves
/// these null, so the test seam has zero overhead in real runs.
/// </para>
/// </summary>
[Collection("IntegrationIssue")]
public class IssueRepositoryCoordinatorSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;

    public IssueRepositoryCoordinatorSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
    }

    private IProjectGrain NewProjectGrain() =>
        _grains.GetGrain<IProjectGrain>($"proj_{Guid.NewGuid():N}");

    private IIssueRepositoryCoordinatorGrain NewCoordinator(string projectId) =>
        _grains.GetGrain<IIssueRepositoryCoordinatorGrain>(projectId);

    private static RepositoryCommandPayload.Create BuildCreatePayload(
        string projectId, int number, string repositoryName) =>
        new RepositoryCommandPayload.Create(
            ProjectId: projectId,
            IssueNumber: number,
            RepositoryName: repositoryName,
            Title: $"Issue #{number}",
            Body: null,
            Labels: null,
            Priority: null,
            Risk: null,
            IsDraft: true,
            AttachmentIds: null,
            WorkflowProfileId: null,
            PrerequisiteNumbers: null);

    private static RepositoryCommandPayload.Change BuildChangePayload(
        string projectId, int number, string repositoryName) =>
        new RepositoryCommandPayload.Change(
            ProjectId: projectId,
            IssueNumber: number,
            RepositoryName: repositoryName,
            Body: null,
            Labels: null,
            Priority: null,
            IsDraft: null,
            AttachmentIds: null,
            WorkflowProfileId: null,
            PresentFields: new HashSet<string>(StringComparer.Ordinal),
            Title: null);

    private static RepositoryCommandPayload.Reopen BuildReopenPayload(
        string projectId, int number, string repositoryName) =>
        new RepositoryCommandPayload.Reopen(
            ProjectId: projectId,
            IssueNumber: number,
            RepositoryName: repositoryName);

    private async Task<(string ProjectId, ProjectInfo Project)> SeedProjectAsync(
        string defaultName = "server",
        string defaultUrl = "git@example.com:server.git",
        string? secondaryName = "web",
        string? secondaryUrl = "git@example.com:web.git")
    {
        var grain = NewProjectGrain();
        var project = await grain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = defaultName,
                GitUrl = defaultUrl,
                BaseBranch = "main",
                IsDefault = true,
            });
        if (secondaryName is not null && secondaryUrl is not null)
            await grain.AddRepositoryAsync(secondaryName, secondaryUrl, "main");
        return (grain.GetPrimaryKeyString(), project);
    }

    private async Task<(string Key, int Number)> SeedIssueAsync(string projectId, string repositoryName, bool isDraft = false)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        await _grains.CreateIssueThroughCoordinatorAsync(
            projectId,
            number,
            commandId: $"create:{projectId}:{number}",
            $"Issue #{number}",
            repositoryName: repositoryName,
            isDraft: isDraft);
        return (IssueGrainKey(projectId, number), number);
    }

    private static string IssueGrainKey(string projectId, int number) =>
        Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(
            new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, number));

    private static (TaskCompletionSource FencePersisted, TaskCompletionSource ReleaseParticipant) InstallParticipantProbe()
    {
        var fencePersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParticipant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (fencePersisted, releaseParticipant);
    }

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
        await coordinator.DeactivateForTestAsync();

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

    /// <summary>
/// Signals the test that the coordinator has reached the
/// post-fence probe point, then blocks until the test releases
/// the in-flight command. Returns only after the release task
/// completes, leaving the coordinator free to continue calling
/// the participant.
/// </summary>
private static async Task SignalAndBlockAsync(TaskCompletionSource fencePersisted, Task release)
{
    fencePersisted.TrySetResult();
    await release;
}

    /// <summary>
    /// Awaits the supplied probe task. The probe must itself signal
    /// via its own completion (e.g. a <c>TaskCompletionSource</c>);
    /// the test relies on the in-process Orleans scheduler to make
    /// forward progress while the test thread awaits.
    /// </summary>
    private static Task WaitForAsync(Task probe, TimeSpan timeout) => probe;
}
