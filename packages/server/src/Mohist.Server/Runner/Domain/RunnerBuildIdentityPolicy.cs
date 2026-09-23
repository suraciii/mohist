namespace Mohist.Server.Runner.Domain;

public static class RunnerBuildIdentityPolicy
{
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? ResolveForRegister(
        string? incoming,
        string? pendingRuntimeIdentity,
        string? pendingBuildGitHash) =>
        incoming ?? pendingRuntimeIdentity ?? pendingBuildGitHash;

    public static string? ResolveForHeartbeat(
        string? incoming,
        string? pendingBuildGitHash,
        string? current) =>
        incoming ?? pendingBuildGitHash ?? current;

    /// <summary>
    /// Keeps the legacy <c>SourceRevision ?? BuildGitHash</c> fallback only for
    /// a payload without <c>schemaVersion</c>. A canonical payload reports
    /// <c>sourceRevision</c> directly.
    /// </summary>
    public static string? ResolveSourceRevision(
        int? schemaVersion,
        string? sourceRevision,
        string? buildGitHash) =>
        schemaVersion is null ? sourceRevision ?? buildGitHash : sourceRevision;
}
