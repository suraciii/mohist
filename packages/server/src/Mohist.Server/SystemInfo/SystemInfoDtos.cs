namespace Mohist.Server.SystemInfo;

public sealed record RunningInfo(
    string? Version,
    string? GitHash,
    DateTimeOffset StartedAt);

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
    DateTimeOffset? CompletedAt);

public sealed record SystemUpdateStatusEnvelope(bool HasJob, SystemUpdateStatusResponse? Job);

public sealed record SystemUpdateLogEntry(DateTimeOffset At, string Stage, string Message);

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
