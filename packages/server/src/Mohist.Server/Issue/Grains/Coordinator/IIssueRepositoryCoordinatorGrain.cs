namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: Project-scoped, non-reentrant application process
/// manager. Serializes Issue create, target reassignment, cancelled-Issue
/// reopen, and repository removal so they cannot race into an orphan
/// binding. Persists at most one
/// <see cref="PendingRepositoryCommand"/> fence; clears it after a
/// definitive applied or rejected participant result; replays any
/// surviving fence on activation / call.
/// </summary>
public interface IIssueRepositoryCoordinatorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Create an Issue whose target binding is established under the
    /// coordinator fence. Pre-allocate the Issue number and identity
    /// before calling — a failed create may consume the number, which
    /// is permitted and necessary for stable replay.
    /// </summary>
    Task<IssueRepositoryBindingResult> CreateIssueAsync(RepositoryCommandPayload.Create payload, string commandId, long? expectedRevision);

    /// <summary>
    /// Reassign a non-terminal, unstarted Issue to another declared
    /// repository. Carries the complete aggregate PATCH so an ambiguous
    /// result cannot partially commit sibling fields.
    /// </summary>
    Task<IssueRepositoryBindingResult> ChangeRepositoryAsync(RepositoryCommandPayload.Change payload, string commandId, long? expectedRevision);

    /// <summary>
    /// Reopen a cancelled Issue. Verifies the retained target still
    /// resolves inside the same fence that performs the reopen, so
    /// repository removal cannot race into an orphan reopen.
    /// </summary>
    Task<IssueRepositoryBindingResult> ReopenAsync(RepositoryCommandPayload.Reopen payload, string commandId, long? expectedRevision);

    /// <summary>
    /// Remove a Project repository under the coordinator fence.
    /// Performs the committed-state blocker check before fencing; if a
    /// non-terminal Issue is bound, returns
    /// <see cref="IssueRepositoryBindingResultCode.RepositoryInUse"/>
    /// without mutating any state and without persisting a fence.
    /// </summary>
    Task<IssueRepositoryBindingResult> RemoveRepositoryAsync(RepositoryCommandPayload.Remove payload, string commandId, long? expectedRevision);

    /// <summary>
    /// issue-417 T-005: test-only hook that lets a test force the
    /// coordinator activation to deactivate. Used to prove that
    /// the persisted fence survives activation loss and replays on
    /// the next activation.
    /// </summary>
    Task DeactivateForTestAsync();
}

public enum IssueRepositoryBindingResultCode
{
    Applied = 0,
    AlreadyApplied = 1,
    RepositoryUnknown = 2,
    RepositoryLocked = 3,
    RepositoryStaleRevision = 4,
    RepositoryInUse = 5,
    ProjectNotFound = 6,
    RepositoryNotFound = 7,
    RepositoryDefault = 8,
    RepositoryMissingOnReopen = 9,
}

[GenerateSerializer]
public sealed record IssueRepositoryBindingResult(
    [property: Id(0)] IssueRepositoryBindingResultCode Code,
    [property: Id(1)] string RepositoryName,
    [property: Id(2)] long AppliedRevision,
    [property: Id(3)] string? Message = null)
{
    public bool IsApplied => Code is IssueRepositoryBindingResultCode.Applied
        or IssueRepositoryBindingResultCode.AlreadyApplied;
}