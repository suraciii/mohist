namespace Mohist.Server.Workflow.Domain.Artifacts;

/// <summary>
/// Emitted when a pending artifact upload has been successfully bound
/// to its producing task run and the resulting <see cref="WorkflowArtifact"/>
/// is visible in workflow history.
/// </summary>
/// <remarks>
/// <para>
/// The payload carries only the durable domain identity of the recorded
/// artifact. Infrastructure metadata (storage path, content hash, content
/// type, size, issue linkage, display name) is read from the bound
/// artifact row, not carried in the event payload.
/// </para>
/// <para>
/// This event is emitted <em>only</em> after binding succeeds. A missing
/// declared artifact is not a domain event — it fails the task through
/// the normal task failure path and never produces
/// <c>WorkflowArtifactMissing</c>.
/// </para>
/// </remarks>
public sealed record WorkflowArtifactRecorded(
    string WorkflowRunId,
    string ActionAttemptId,
    string Path,
    DateTimeOffset RecordedAt);
