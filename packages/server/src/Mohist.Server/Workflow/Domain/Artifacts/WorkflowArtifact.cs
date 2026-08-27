namespace Mohist.Server.Workflow.Domain.Artifacts;

/// <summary>
/// Immutable workflow artifact fact: a single recorded output produced by
/// one workflow task run. The producer identity is
/// <see cref="WorkflowRunId"/> + <see cref="ActionAttemptId"/>; the stable
/// business identity under that producer is <see cref="Path"/>. A later
/// task run that records the same path produces a separate, immutable
/// <see cref="WorkflowArtifact"/>; the earlier record is not mutated.
/// </summary>
/// <remarks>
/// <para>
/// This is the only domain fact Mohist needs to reason about workflow
/// artifacts. Infrastructure metadata (storage path, content hash,
/// content type, size, issue linkage, derived display name) lives in
/// the persistence row / read model — it is not part of the core
/// language so that storage and presentation can evolve without
/// changing the domain contract.
/// </para>
/// <para>
/// The name intentionally avoids the term <c>Snapshot</c>. Version
/// history is the behavior of <c>WorkflowArtifact</c>, not part of
/// its name.
/// </para>
/// </remarks>
public sealed record WorkflowArtifact(
    string WorkflowRunId,
    string ActionAttemptId,
    string Path,
    DateTimeOffset RecordedAt)
{
    /// <summary>
    /// Producer identity — same workflow run + same task run. Two
    /// artifacts with the same key are considered the same recording;
    /// the task run ids are unique inside a workflow run.
    /// </summary>
    public string ProducerKey => $"{WorkflowRunId}:{ActionAttemptId}";
}
