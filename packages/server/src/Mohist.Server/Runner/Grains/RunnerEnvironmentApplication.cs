namespace Mohist.Server.Runner.Grains;

public static class RunnerUpdateInterruptKinds
{
    public const string Release = "release";
    public const string EnvironmentApplication = "environment-application";

    public static bool IsEnvironmentApplication(RunnerUpdateInterruptFence? fence) =>
        string.Equals(fence?.Kind, EnvironmentApplication, StringComparison.Ordinal);
}

[GenerateSerializer]
public enum RunnerEnvironmentApplicationPhase
{
    Waiting,
    Applying,
    Active,
    Failed,
    Cancelled,
    Unconfirmed,
}

[GenerateSerializer]
public sealed class RunnerEnvironmentApplication
{
    [Id(0)] public string UpdateId { get; set; } = string.Empty;
    [Id(1)] public string TargetVersion { get; set; } = string.Empty;
    [Id(2)] public string? PreviousVersion { get; set; }
    [Id(3)] public string? BaseProcessGeneration { get; set; }
    [Id(4)] public string? BaseConnectionGeneration { get; set; }
    [Id(5)] public RunnerEnvironmentApplicationPhase Phase { get; set; }
    [Id(6)] public string? FailureCode { get; set; }
    [Id(7)] public DateTimeOffset RequestedAt { get; set; }
    [Id(8)] public DateTimeOffset? CompletedAt { get; set; }
}

[GenerateSerializer]
public sealed record RunnerEnvironmentSettlement(
    [property: Id(0)] bool OwnerLedgersEmpty,
    [property: Id(1)] bool CurrentGenerationReportedSettled,
    [property: Id(2)] bool Settled,
    [property: Id(3)] int ActiveWorkCount,
    [property: Id(4)] int InFlightCount,
    [property: Id(5)] int AwaitingAckCount,
    [property: Id(6)] string? ProcessGeneration,
    [property: Id(7)] string? ConnectionGeneration);

[GenerateSerializer]
public sealed record RunnerEnvironmentApplicationSnapshot(
    [property: Id(0)] string UpdateId,
    [property: Id(1)] string TargetVersion,
    [property: Id(2)] string? PreviousVersion,
    [property: Id(3)] RunnerEnvironmentApplicationPhase Phase,
    [property: Id(4)] string? FailureCode,
    [property: Id(5)] string? BaseProcessGeneration,
    [property: Id(6)] string? BaseConnectionGeneration,
    [property: Id(7)] DateTimeOffset RequestedAt,
    [property: Id(8)] DateTimeOffset? CompletedAt,
    [property: Id(9)] RunnerEnvironmentSettlement Settlement);

[GenerateSerializer]
public enum RunnerEnvironmentApplicationBeginStatus
{
    Waiting,
    AlreadyPending,
    AlreadyCompleted,
    Conflict,
    NotReady,
}

[GenerateSerializer]
public sealed record RunnerEnvironmentApplicationBeginResult(
    [property: Id(0)] string UpdateId,
    [property: Id(1)] RunnerEnvironmentApplicationBeginStatus Status,
    [property: Id(2)] RunnerEnvironmentApplicationSnapshot? Application);

[GenerateSerializer]
public enum RunnerEnvironmentApplicationCommandStatus
{
    Accepted,
    AlreadyApplied,
    AlreadyRolledBack,
    Cancelled,
    AlreadyCancelled,
    Failed,
    Unconfirmed,
    NotFound,
    Conflict,
    NotSettled,
    Stale,
    VersionMismatch,
}

[GenerateSerializer]
public sealed record RunnerEnvironmentApplicationCommandResult(
    [property: Id(0)] string UpdateId,
    [property: Id(1)] RunnerEnvironmentApplicationCommandStatus Status,
    [property: Id(2)] RunnerEnvironmentApplicationSnapshot? Application);
