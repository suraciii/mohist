using System.Collections.Concurrent;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Project.Grain;

/// <summary>
/// issue-417 T-005: locks the persisted-fence coordinator contract.
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
[Collection("ComponentGrain")]
[Trait("level", "L0")]
public partial class IssueRepositoryCoordinatorSpecs
{
    private readonly IGrainFactory _grains;

    public IssueRepositoryCoordinatorSpecs(ComponentWorkflowGrainFixture fixture)
    {
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
            }, "git diff --check");
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
