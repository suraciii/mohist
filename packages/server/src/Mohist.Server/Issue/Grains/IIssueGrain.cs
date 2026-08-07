using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Grains;

public interface IIssueGrain : IGrainWithStringKey
{
    Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null, int? parentIssueNumber = null);
    Task<string> StartWorkAsync(WorkflowProjectContext? project = null);
    Task CompleteWorkAsync(string workflowRunId);
    Task MarkDoneAsync();
    Task CancelAsync();
    Task UpdateAsync(string title, string? body);
    Task UpdateFullAsync(UpdateIssueData data);
    Task CloseCompositeAsync();
    Task ReopenCompositeAsync();
    Task ArchiveAsync();
    Task ArchiveForParentCascadeAsync();
    Task UnarchiveAsync();
    Task<IssueWorkflowStatus?> GetWorkflowStatusAsync();

    /// <summary>
    /// returns the Issue's currently-bound
    /// workflow run id, or <c>null</c> when the issue has no
    /// <c>workflowRunId</c>. Used by the durable
    /// <c>IssueWorkStartedHandler</c> to detect and discard stale
    /// events whose run id no longer matches the Issue's active
    /// run.
    /// </summary>
    Task<string?> GetActiveWorkflowRunIdAsync();
    Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber);
    Task RemovePrerequisiteAsync(int prerequisiteNumber);
    Task<IssueStartReadiness> GetStartReadinessAsync();

    /// <summary>
    /// composite advancement entry point. Marks
    /// the parent <c>Backlog → InProgress</c> without minting a workflow
    /// run, then fans out <c>StartWorkAsync</c> to every currently
    /// startable child in parallel. Invoked from <c>StartWorkAsync</c>
    /// when the issue has one or more children; exposed on the interface
    /// for direct test/spec invocation.
    /// </summary>
    Task StartCompositeAsync();

    /// <summary>
    /// idempotent recompute of the parent's
    /// aggregated status from a fresh children snapshot, with optional
    /// fan-out of newly-unlocked startable children. Dispatched by the
    /// durable handlers on child <c>started</c>, <c>completed</c>,
    /// <c>cancelled</c>, <c>reopened</c>, and <c>parent-changed</c>
    /// events. When the issue has no children (last detach has
    /// occurred) the call no-ops; the next direct <c>StartWorkAsync</c>
    /// on the now-empty parent uses the normal single-issue path.
    /// </summary>
    Task RecomputeCompositeStatusAsync();
    Task<IssueCommentResult> AddCommentAsync(string actor, string? displayName, string body, string[]? attachmentIds = null);

    Task<bool> AssignEpicAsync(int epicNumber);
    Task<bool> RemoveEpicAsync(int expectedEpicNumber);
    Task<bool> TryStartFromEpicAsync(int expectedEpicNumber);

    /// <summary>
    /// receipt-bearing create invoked from the
    /// <c>IIssueBindingParticipant</c> proxy under the coordinator
    /// fence. Receipt match, stale-revision rejection, and unknown-target
    /// rejection all behave as documented on
    /// <see cref="Mohist.Server.Issue.Grains.Coordinator.IIssueBindingParticipant"/>.
    /// </summary>
    Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(
        string projectId,
        int number,
        string title,
        string? body,
        IReadOnlyDictionary<string, string>? labels,
        string? priority,
        string repositoryRef,
        string? risk,
        bool isDraft,
        string[]? attachmentIds,
        string? workflowProfileId,
        int[]? prerequisiteNumbers,
        int? parentIssueNumber,
        string commandId,
        long? expectedRevision);

    /// <summary>
    /// receipt-bearing reassignment that atomically
    /// applies the repository change plus every present PATCH field in
    /// a single Issue transaction, so an ambiguous result cannot commit
    /// only the repository reassignment while dropping sibling Issue
    /// fields.
    /// </summary>
    Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(
        IssueChangeRepositoryCommand command,
        string commandId,
        long? expectedRevision);

    /// <summary>
    /// receipt-bearing reopen that re-validates the
    /// retained target declaration and writes a receipt. The
    /// coordinator is the only caller (route-level callers must enter
    /// through the coordinator).
    /// </summary>
    Task<Mohist.Server.Issue.Grains.Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(
        string commandId,
        long? expectedRevision);

    /// <summary>
    /// returns the current coordination revision so
    /// the coordinator can capture an <c>expectedRevision</c> snapshot
    /// before fencing. Returns <c>0</c> for an unloaded issue slot.
    /// </summary>
    Task<long> GetRepositoryBindingRevisionAsync();
}

/// <summary>
/// complete aggregate PATCH bundled with a repository
/// reassignment so an ambiguous coordinator outcome cannot commit only
/// the repository change and drop sibling Issue fields. Every field
/// honors its own three-state (absent / present-and-null /
/// present-and-value) semantics, but the participant applies them
/// atomically with the reassignment.
/// </summary>
[GenerateSerializer]
public sealed record IssueChangeRepositoryCommand(
    [property: Id(0)] string RepositoryName,
    [property: Id(1)] string? Title,
    [property: Id(2)] string? Body,
    [property: Id(3)] IReadOnlyDictionary<string, string>? Labels,
    [property: Id(4)] string? Priority,
    [property: Id(5)] bool? IsDraft,
    [property: Id(6)] string[]? AttachmentIds,
    [property: Id(7)] string? WorkflowProfileId,
    [property: Id(8)] IReadOnlySet<string>? PresentFields,
    [property: Id(9)] int? ParentIssueNumber,
    [property: Id(10)] string? Risk = null);

[GenerateSerializer]
public sealed record IssueWorkflowStatus(
    int IssueNumber,
    string Title,
    string Stage,
    string RuntimeStatus,
    string? WorkflowRunId,
    string? ChangeDir,
    string? WorkspacePath,
    WorkflowStatusView? Workflow);

[GenerateSerializer]
public sealed record WorkflowProjectContext(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string? RepositoryName = null,
    [property: Id(3)] string? RepositoryGitUrl = null,
    [property: Id(4)] string? RepositoryBaseBranch = null);

[GenerateSerializer]
public sealed record IssuePrerequisiteResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string Code,
    [property: Id(2)] string Message)
{
    public static IssuePrerequisiteResult Added() => new(true, "ok", "Prerequisite added");
    public static IssuePrerequisiteResult IssueNotFound() => new(false, "issue_not_found", "Issue not found");
    public static IssuePrerequisiteResult PrerequisiteNotFound(int number) => new(false, "prerequisite_not_found", $"Issue #{number} not found");
    public static IssuePrerequisiteResult Circular(string? message = null) => new(false, "circular_prerequisite", message ?? "Issue cannot depend on itself");
}

[GenerateSerializer]
public sealed record IssueCommentResult(
    [property: Id(0)] string Id,
    [property: Id(1)] string Body,
    [property: Id(2)] string? Author,
    [property: Id(3)] string? DisplayName);
