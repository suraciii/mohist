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
}
