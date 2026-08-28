using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Workflow.Definition;
using Mohist.Server.Contracts;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Runner.Grains;
using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// The lifecycle status of a <see cref="WorkflowRun"/> aggregate.
/// This state machine is independent from <see cref="WorkflowActionAttemptStatus"/> —
/// the two describe different aggregates and do not derive each other.
/// <c>Paused</c>, <c>Stopped</c>, and <c>AwaitingApproval</c> only result
/// from workflow-level commands, never from a task status transition.
/// </summary>
public enum WorkflowRunStatus { Created, Pending, Ready, Running, AwaitingApproval, Paused, Stopped, Completed, Failed }

public static class WorkflowRunStatusExtensions
{
    // Terminal = the run is permanently done and will not be dispatched again.
    // `Failed` is deliberately NOT terminal: it is a recoverable mid-state —
    // Retry/Rerun/RerunFromStage revive it back to a dispatchable status. A run
    // that can be retried must not have its workspace reclaimed, so cleanup
    // eligibility is keyed on the true terminals (Completed / Stopped) only.
    public static bool IsTerminal(this WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Stopped or WorkflowRunStatus.Completed;
}

[GenerateSerializer]
public sealed record WorkflowRunMetadata(
    [property: Id(0)] string? Name,
    [property: Id(1)] DateTimeOffset CreatedAt,
    [property: Id(2)] Dictionary<string, string>? Labels = null,
    [property: Id(3)] Dictionary<string, string>? Annotations = null,
    [property: Id(4)] string? ProjectId = null,
    [property: Id(5)] int? IssueNumber = null,
    [property: Id(6)] int? EpicNumber = null);

[GenerateSerializer]
public sealed record WorkspaceIdentity(
    [property: Id(0)] string Path,
    [property: Id(1)] string? Branch = null,
    [property: Id(2)] string? ChangeDir = null);

/// <summary>
/// authoritative, immutable repository
/// context captured at workflow-run creation. Issue-backed runs
/// MUST populate this; generic runs may leave it null. Once
/// assigned, ordinary run commands cannot mutate it — the context
/// is owned by the <see cref="WorkflowRun"/> aggregate and the
/// run store preserves it across replay. The fingerprint is
/// produced by <c>GitRemoteUrlNormalizer</c> and the version stamp
/// travels alongside the digest so future Server/Runner releases
/// can refuse stale comparisons rather than silently agreeing.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowRepositoryContext(
    [property: Id(0)] string Name,
    [property: Id(1)] string GitUrl,
    [property: Id(2)] string BaseBranch);

[GenerateSerializer]
public sealed record WorkflowPullRequestIdentity(
    [property: Id(0)] WorkflowRepositoryContext Repository,
    [property: Id(1)] int Number);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowRunMetadata Metadata { get; set; }
    public WorkflowRunStatus Status { get; set; }
    public string? ExplicitWorkflowProfileId { get; set; }
    public string? WorkflowProfileId { get; set; }
    /// <summary>
    /// The active worker assignment for this run. At most one worker may own a
    /// run at a time; running tasks derive their worker id from this assignment
    /// so reports can be rejected when they arrive from a stale worker.
    /// </summary>
    public WorkflowAssignment? Assignment { get; set; }

    /// <summary>
    /// Write-once immutable snapshot of the complete effective
    /// <c>WorkflowDefinition</c> captured at <c>BindWorkflowRun</c> time.
    /// Subsequent stage initialization and stage-lock resolution read this
    /// snapshot rather than the live profile provider, so a profile edit
    /// after binding cannot retroactively change a run's lane mode, task
    /// definitions, command text, or per-lane timeouts. Pre-existing runs
    /// without this field remain readable as explicit legacy mode and use
    /// the retained pre-change aggregate definition for the affected
    /// built-in profiles; the lane gate does not apply to them.
    /// </summary>
    public string? BoundWorkflowDefinitionJson { get; set; }
    public string? CurrentStageId { get; set; }
    public required List<StageRun> Stages { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>
    /// When the run (re-)entered <see cref="WorkflowRunStatus.Ready"/>. Drives
    /// fairness: the scheduler serves Ready runs in <c>ReadySince ASC</c> order,
    /// so just-served runs re-queue at the tail with zero scheduler state.
    /// Maintained as a side
    /// effect of entering Ready;
    /// leaving Ready does not clear it, re-entering overwrites it.
    /// </summary>
    public DateTimeOffset? ReadySince { get; set; }
    public FailureDetails? Failure { get; set; }
    public WorkspaceIdentity? Workspace { get; set; }
    /// <summary>
    /// immutable repository snapshot assigned
    /// at workflow start. Normal run commands MUST NOT mutate it;
    /// <see cref="WorkflowRunExtensions.EnsureStarted"/> is the only
    /// entry that touches this property, and it refuses to overwrite
    /// a non-null value with a conflicting context.
    /// </summary>
    [JsonInclude]
    public WorkflowRepositoryContext? Repository { get; private set; }

    [JsonInclude]
    public WorkflowPullRequestIdentity? PullRequestIdentity { get; private set; }

    internal void AssignRepositoryContext(WorkflowRepositoryContext? repository) =>
        Repository = repository;

    internal void AssignPullRequestIdentity(int number) =>
        AssignPullRequestIdentity(Repository
            ?? throw new InvalidOperationException(
                $"WorkflowRun '{Id}' cannot record a Pull Request without a bound repository"), number);

    internal void AssignPullRequestIdentity(WorkflowRepositoryContext repository, int number)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (number <= 0)
            throw new InvalidOperationException("Pull Request number must be positive");
        if (Repository is null || !RepositoryContextEquals(Repository, repository))
            throw new InvalidOperationException(
                $"WorkflowRun '{Id}' cannot record a Pull Request for a conflicting repository");

        var candidate = new WorkflowPullRequestIdentity(repository, number);
        if (PullRequestIdentity is null)
        {
            PullRequestIdentity = candidate;
            return;
        }

        if (PullRequestIdentity.Number != number
            || !RepositoryContextEquals(PullRequestIdentity.Repository, repository))
        {
            throw new InvalidOperationException(
                $"WorkflowRun '{Id}' already has Pull Request #{PullRequestIdentity.Number} for a conflicting identity");
        }
    }

    internal void ValidatePullRequestNumber(int number)
    {
        if (number <= 0)
            throw new InvalidOperationException("Pull Request number must be positive");
        if (PullRequestIdentity is not null && PullRequestIdentity.Number != number)
            throw new InvalidOperationException(
                $"WorkflowRun '{Id}' is bound to Pull Request #{PullRequestIdentity.Number}, not #{number}");
    }

    private static bool RepositoryContextEquals(
        WorkflowRepositoryContext left,
        WorkflowRepositoryContext right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.GitUrl, right.GitUrl, StringComparison.Ordinal)
        && string.Equals(left.BaseBranch, right.BaseBranch, StringComparison.Ordinal);

    public List<ApprovalFeedback> Feedback { get; set; } = new();

    public bool IsAssigned => Assignment is not null;
    public string? AssignedTo => Assignment?.WorkerId;
}
