namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Sanitized facts reported by the local environment manager. It deliberately
/// contains no snapshot values, command arguments, or command output.
/// </summary>
[GenerateSerializer]
public sealed class RunnerEnvironmentObservation
{
    [Id(0)] public string? ProcessGeneration { get; set; }
    [Id(1)] public string? EnvironmentVersion { get; set; }
    [Id(2)] public DateTimeOffset? EnvironmentLoadedAt { get; set; }
    [Id(3)] public string? CandidateSource { get; set; }
    [Id(4)] public string? CandidateUser { get; set; }
    [Id(5)] public string? CandidateVersion { get; set; }
    [Id(6)] public string[] CandidateVariables { get; set; } = [];
    [Id(7)] public DateTimeOffset? CandidateCapturedAt { get; set; }
    [Id(8)] public string[] CandidateAddedVariables { get; set; } = [];
    [Id(9)] public string[] CandidateRemovedVariables { get; set; } = [];
    [Id(10)] public string[] CandidateChangedVariables { get; set; } = [];
    [Id(11)] public RunnerEnvironmentToolCheck[] ToolChecks { get; set; } = [];
    [Id(12)] public DateTimeOffset ReportedAt { get; set; }
}

[GenerateSerializer]
public sealed class RunnerEnvironmentToolCheck
{
    [Id(0)] public string Executable { get; set; } = string.Empty;
    [Id(1)] public string? ResolvedPath { get; set; }
    [Id(2)] public string SnapshotKind { get; set; } = string.Empty;
    [Id(3)] public string? SnapshotVersion { get; set; }
    [Id(4)] public string Outcome { get; set; } = string.Empty;
    [Id(5)] public int? ExitCode { get; set; }
    [Id(6)] public long DurationMilliseconds { get; set; }
    [Id(7)] public DateTimeOffset CheckedAt { get; set; }
}

[GenerateSerializer]
public enum RunnerEnvironmentObservationStatus
{
    Accepted,
    Stale,
}

[GenerateSerializer]
public sealed record RunnerEnvironmentObservationResult(
    [property: Id(0)] RunnerEnvironmentObservationStatus Status,
    [property: Id(1)] RunnerEnvironmentObservation? Observation);
