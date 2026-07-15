using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// Pure helper that builds the lineage extensions dictionary for a workflow
/// CloudEvent from the producing run's own state (<see cref="WorkflowRun"/>
/// / <see cref="WorkflowRunMetadata.Annotations"/>) and the unwrapped
/// <see cref="WorkflowEvent"/> variant. No cross-aggregate query is issued —
/// stamping uses only identity the run already holds.
/// </summary>
/// <remarks>
/// Lineage attribute names live on <see cref="EventCatalog.Lineage"/> and
/// stay in sync with <c>design/event-protocol.md</c>. Stage carriage is
/// derived structurally from the variant (D2): events whose record type
/// exposes a <c>Stage</c> member carry the stamp; <see cref="WorkflowArtifactRecorded"/>
/// has no <c>Stage</c> member and therefore MUST NOT receive a <c>stage</c>
/// stamp even though it is a <c>workflow.*</c> event.
/// </remarks>
public static class WorkflowRunLineage
{
    /// <summary>
    /// Build the <c>extensions</c> dictionary for the given workflow event.
    /// <c>workflowrunid</c> is always stamped (the run is the producer);
    /// <c>projectid</c>, <c>issueid</c>, and <c>issue</c> are stamped only
    /// when their value is present on the run's metadata annotations (absent
    /// affiliations are omitted, never empty). <c>stage</c> is stamped when
    /// the variant exposes a <c>Stage</c> member — see <see cref="StageOf"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildExtensions(WorkflowRun run, WorkflowEvent evt)
    {
        ArgumentNullException.ThrowIfNull(run);

        var annotations = run.Metadata?.Annotations;
        var projectId = RequiredAnnotation(annotations, "projectId", run.Id);
        var issueId = annotations?.GetValueOrDefault("issueId");
        var issueNumber = annotations?.GetValueOrDefault("issueNumber");
        var epicId = annotations?.GetValueOrDefault("epicId");
        var stage = StageOf(evt);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.WorkflowRunId] = run.Id,
            [EventCatalog.Lineage.ProjectId] = projectId,
        };
        if (!string.IsNullOrWhiteSpace(issueId))
            extensions[EventCatalog.Lineage.IssueId] = issueId;
        if (!string.IsNullOrWhiteSpace(issueNumber))
            extensions[EventCatalog.Lineage.Issue] = issueNumber;
        if (!string.IsNullOrWhiteSpace(epicId))
            extensions[EventCatalog.Lineage.EpicId] = epicId;
        if (!string.IsNullOrWhiteSpace(stage))
            extensions[EventCatalog.Lineage.Stage] = stage!;

        return extensions;
    }

    private static string RequiredAnnotation(
        IReadOnlyDictionary<string, string>? annotations,
        string key,
        string workflowRunId)
    {
        if (annotations?.TryGetValue(key, out var value) == true && !string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Workflow run '{workflowRunId}' cannot emit events without the required '{key}' annotation.");
    }

    /// <summary>
    /// Extract the <c>Stage</c> name from a workflow event by structural
    /// inspection of the unwrapped union variant (D2). Returns <c>null</c>
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
            CheckPassed v => v.Stage,
            CheckFailed v => v.Stage,
            CheckPending v => v.Stage,
            _ => null,
        };
    }
}
