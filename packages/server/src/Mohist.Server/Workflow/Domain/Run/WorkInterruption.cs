namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Durable evidence that a runner disappeared while it owned workflow work.
/// The owning task or stage-checks work remains in its existing Running state
/// while this record is present; the workflow grain derives the bounded
/// terminal fallback from <see cref="RecoveryDeadlineAt"/>.
/// </summary>
public sealed record WorkInterruption(
    string ReasonCode,
    string WorkId,
    string OwnerId,
    DateTimeOffset RecordedAt,
    DateTimeOffset RecoveryDeadlineAt);
