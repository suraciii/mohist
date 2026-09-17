namespace Mohist.Server.Auth.Domain;

public enum RunnerCredentialStatus
{
    Active,
    Revoked,
    Missing,
    Unknown,
}

public interface IRunnerCredentialStatusReader
{
    Task<RunnerCredentialStatus> GetStatusAsync(
        string runnerId,
        CancellationToken ct = default);
}
