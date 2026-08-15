using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for a workflow
/// CloudEvent from the producing run's own state (<see cref="WorkflowRun"/>
/// / <see cref="WorkflowRunMetadata"/>) and the unwrapped
/// <see cref="WorkflowEvent"/> variant. No cross-aggregate query is issued —
/// stamping uses only identity the run already holds.
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/>.
/// Stage carriage is
/// derived structurally from the variant: events whose record type
/// exposes a <c>Stage</c> member carry the stamp; <see cref="WorkflowArtifactRecorded"/>
/// has no <c>Stage</c> member and therefore MUST NOT receive a <c>stage</c>
/// stamp even though it is a <c>workflow.*</c> event.
/// </remarks>
public static class WorkflowRunLineage
{
    internal static WorkflowRunMetadata ForIssue(
        string projectId,
        int issueNumber,
        int? epicNumber,
        WorkflowRunMetadata? existing = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));
        if (issueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(issueNumber));

        return (existing ?? new WorkflowRunMetadata(null, DateTimeOffset.UnixEpoch)) with
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            EpicNumber = epicNumber is > 0 ? epicNumber : null,
        };
    }

    internal static WorkflowRunMetadata? NormalizeStartMetadata(WorkflowRunMetadata? metadata)
    {
        if (metadata?.IssueNumber is { } issueNumber)
            return ForIssue(metadata.ProjectId ?? string.Empty, issueNumber, metadata.EpicNumber, metadata);
        if (metadata?.EpicNumber is not null)
            throw new ArgumentException("Epic context requires an issue context.", nameof(metadata));

        return metadata;
    }

    internal static void ApplyContext(WorkflowRun run, string projectId, int issueNumber, int? epicNumber)
    {
        run.Metadata = ForIssue(projectId, issueNumber, epicNumber, run.Metadata);
    }

    internal static bool ContextEquals(WorkflowRun run, string projectId, int issueNumber, int? epicNumber) =>
        string.Equals(run.Metadata.ProjectId, projectId, StringComparison.Ordinal)
        && run.Metadata.IssueNumber == issueNumber
        && EpicAffiliationOf(run) == epicNumber;

    internal static void RestoreStoredEpicNumber(WorkflowRun run, int? epicNumber)
    {
        if (epicNumber is not null)
            run.Metadata = run.Metadata with { EpicNumber = epicNumber };
    }

    internal static int? EpicAffiliationOf(WorkflowRun run) =>
        run.Metadata.EpicNumber is > 0 ? run.Metadata.EpicNumber : null;

    /// <summary>
    /// Build the <c>extensions</c> dictionary for the given workflow event.
    /// <c>workflowrunid</c> is always stamped (the run is the producer);
    /// <c>projectid</c>, <c>issue</c>, and <c>epic</c> are stamped only
    /// when their value is present on the run's typed metadata (absent
    /// affiliations are omitted, never empty). <c>stage</c> is stamped when
    /// the variant exposes a <c>Stage</c> member — see <see cref="StageOf"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(WorkflowRun run, WorkflowEvent evt)
    {
        ArgumentNullException.ThrowIfNull(run);

        var projectId = run.Metadata.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(
                $"Workflow run '{run.Id}' cannot emit events without a project context.");

        var issueNumber = run.Metadata.IssueNumber?.ToString();
        var epicNumber = EpicAffiliationOf(run)?.ToString();
        var stage = StageOf(evt);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.WorkflowRunId] = run.Id,
            [EventCatalog.Lineage.ProjectId] = projectId,
        };
        if (!string.IsNullOrWhiteSpace(issueNumber))
            extensions[EventCatalog.Lineage.Issue] = issueNumber;
        if (!string.IsNullOrWhiteSpace(epicNumber))
            extensions[EventCatalog.Lineage.Epic] = epicNumber;
        if (!string.IsNullOrWhiteSpace(stage))
            extensions[EventCatalog.Lineage.Stage] = stage!;

        return extensions;
    }

    /// <summary>
    /// Extract the <c>Stage</c> name from a workflow event by structural
    /// inspection of the unwrapped union variant. Returns <c>null</c>
    /// for variants that do not expose a <c>Stage</c> member (e.g.
    /// <see cref="WorkflowArtifactRecorded"/>, the run-lifecycle variants).
    /// </summary>
    public static string? StageOf(WorkflowEvent evt)
    {
        return WorkflowEventSerializer.Unwrap(evt) switch
        {
            StageStarted v => v.Stage,
            StageCompleted v => v.Stage,
            StageFailed v => v.Stage,
            StageApprovalRequested v => v.Stage,
            StageApprovalResolved v => v.Stage,
            FeedbackRequested v => v.Stage,
            TaskStarted v => v.Stage,
            TaskCompleted v => v.Stage,
            TaskFailed v => v.Stage,
            TaskInterrupted v => v.Stage,
            TaskCancelled v => v.Stage,
            AgentTaskResultUnconfirmed v => v.Stage,
            TaskBlocked v => v.Stage,
            StageBlocked v => v.Stage,
            WorkflowRunBlocked v => v.Stage,
            CheckPassed v => v.Stage,
            CheckFailed v => v.Stage,
            CheckPending v => v.Stage,
            ChecksInterrupted v => v.Stage,
            _ => null,
        };
    }

    public static bool CarriesStage(WorkflowEvent evt) => WorkflowEventSerializer.Unwrap(evt) switch
    {
        StageStarted or
        StageCompleted or
        StageFailed or
        StageApprovalRequested or
        StageApprovalResolved or
        FeedbackRequested or
        TaskStarted or
        TaskCompleted or
        TaskFailed or
        TaskInterrupted or
        TaskCancelled or
        AgentTaskResultUnconfirmed or
        TaskBlocked or
        StageBlocked or
        WorkflowRunBlocked or
        CheckPassed or
        CheckFailed or
        CheckPending or
        ChecksInterrupted => true,
        _ => false,
    };
}
