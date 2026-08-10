namespace Mohist.Server.SystemInfo;

public sealed record RunningInfo(
    string? Version,
    string? GitHash,
    DateTimeOffset StartedAt,
    string? TreeHash = null,
    string? ArtifactDigest = null,
    string? ReleaseId = null,
    long Generation = 0);

public sealed record SourceInfo(
    string? Path,
    string? Branch,
    string? Head,
    bool Dirty);

public sealed record InstallInfo(
    string Mode,
    string? ServiceManager,
    string? ServerUnit,
    string? RunnerUnit,
    string? Reason);

public sealed record UpdateInfo(
    string Status,
    bool Available,
    string? Reason);

public sealed record SystemUpdateRequest;

public sealed record SystemUpdateStartResponse(SystemUpdateStatusResponse Job);

public sealed record SystemUpdateStatusResponse(
    string JobId,
    string Status,
    string Stage,
    bool UpdateAvailable,
    string? RunningGitHash,
    string? SourceHead,
    string? SourcePath,
    string? ServerUnit,
    string? RunnerUnit,
    string? Reason,
    IReadOnlyList<SystemUpdateLogEntry> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome = null,
    string? UnavailableCapability = null);

public sealed record SystemUpdateStatusEnvelope(bool HasJob, SystemUpdateStatusResponse? Job);

public sealed record SystemUpdateLogEntry(DateTimeOffset At, string Stage, string Message);

public sealed record SystemUpdateOutcomeRequest(
    string? JobId = null,
    string? Status = null,
    string? Stage = null,
    string? Outcome = null,
    string? UnavailableCapability = null,
    IReadOnlyList<SystemUpdateLogEntry>? Logs = null,
    string? SourceHead = null,
    string? SourcePath = null,
    string? ServerUnit = null,
    string? RunnerUnit = null);

public sealed record SystemUpdateOutcomeResponse(SystemUpdateStatusResponse Job);

public sealed record RuntimeConsistencyComponent(
    string Name,
    string Status,
    string? Reason = null);

public sealed record RuntimeConsistencyResponse(
    string Status,
    string? Reason,
    IReadOnlyList<RuntimeConsistencyComponent> Components,
    SystemInfoResponse System);

public sealed record ServiceInfo(
    string? Server,
    string? Runner);

public sealed record SystemPaths(
    string? Db,
    string? Config,
    string? Logs,
    string? Opencode);

public sealed record SystemInfoResponse(
    RunningInfo Running,
    SourceInfo Source,
    InstallInfo Install,
    UpdateInfo Update,
    ServiceInfo Services,
    SystemPaths Paths);
