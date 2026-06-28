namespace Mohist.Server.Infrastructure.Data.Runner;

public enum RunnerWorkStatus
{
    Outstanding,
    Completed,
    Failed,
}

public sealed record RunnerWork(
    string RunnerId,
    string OwnerKind,
    string OwnerId,
    string WorkId,
    DateTimeOffset TakenAt,
    RunnerWorkStatus Status,
    string? Reason = null,
    DateTimeOffset? FinishedAt = null);
