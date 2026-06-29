namespace Mohist.Cli;

internal enum InfoSourceKind
{
    Unknown,
    Resolved,
    NotGitRepo,
}

internal sealed record InfoCli(
    string? Version,
    string? BinaryPath,
    string? BuildDate)
{
    public InfoCli(string? version, string? binaryPath)
        : this(version, binaryPath, null) { }
}

internal sealed record InfoServiceStatus(
    string? State,
    int? Pid,
    string? Uptime,
    long? UptimeSeconds,
    string? Connectivity)
{
    public InfoServiceStatus(string? state, int? pid, string? uptime)
        : this(state, pid, uptime, null, null) { }
}

internal sealed record InfoSource(
    string? Path,
    string? CommitShort,
    string? CommitSubject,
    InfoSourceKind Kind)
{
    public InfoSource(string? path, string? commitShort, string? commitSubject)
        : this(path, commitShort, commitSubject, commitShort is null ? InfoSourceKind.NotGitRepo : InfoSourceKind.Resolved) { }
}

internal sealed record InfoService(
    InfoServiceStatus? Status,
    InfoSource? Source);

internal sealed record InfoProject(
    string? Id,
    string? Name,
    int? IssueCount,
    int? ActiveIssueCount);

internal sealed record InfoDataDir(
    string Path,
    string? Size);

internal sealed record InfoVerboseSkill(
    string Name,
    string? InstallPath);

internal sealed record InfoVerboseSkills(
    IReadOnlyList<InfoVerboseSkill> Skills,
    bool Resolved);

internal sealed record InfoVerboseGitRemote(
    string? OriginUrl,
    bool IsGitRepo);

internal sealed record InfoVerboseOpencodeRuntime(
    string? Command,
    string? Version,
    int? ModelCount,
    bool Resolved);

internal sealed record InfoVerboseEnvVar(
    string Name,
    string? Value);

internal sealed record InfoVerboseOsRuntime(
    string? Os,
    string? Architecture,
    string? DotnetVersion,
    string? NodeVersion);

internal sealed record InfoVerboseCapacity(
    int? ActiveWorkflows);

internal sealed record InfoVerboseDiskCategory(
    string Name,
    string? Size,
    int? FileCount);

internal sealed record InfoVerboseDiskUsage(
    IReadOnlyList<InfoVerboseDiskCategory> Categories,
    bool Resolved);

internal sealed record InfoVerbose(
    InfoVerboseSkills Skills,
    InfoVerboseGitRemote GitRemote,
    InfoVerboseOpencodeRuntime OpencodeRuntime,
    IReadOnlyList<InfoVerboseEnvVar> EnvVars,
    InfoVerboseOsRuntime OsRuntime,
    InfoVerboseCapacity Capacity,
    InfoVerboseDiskUsage DiskUsage);

internal sealed record InfoResult(
    InfoCli Cli,
    InfoService Server,
    InfoService Runner,
    InfoProject? Project,
    InfoDataDir DataDir,
    string? PlatformNotice,
    InfoVerbose? Verbose = null);
