namespace Mohist.Server.Auth.Domain;

public enum RunnerCredentialStatus
{
    Active,
    Revoked,
    Missing,
    Unknown,
}

public sealed record RunnerCredentialAuthority(string CredentialId, DateTimeOffset IssuedAt);

public interface IRunnerCredentialStatusReader
{
    Task<RunnerCredentialStatus> GetStatusAsync(
        string runnerId,
        CancellationToken ct = default);

    Task<RunnerCredentialAuthority?> GetActiveAuthorityAsync(
        string runnerId,
        CancellationToken ct = default) => Task.FromResult<RunnerCredentialAuthority?>(null);
}
