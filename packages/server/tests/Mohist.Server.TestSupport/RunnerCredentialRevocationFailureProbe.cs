using Mohist.Server.Auth.Domain;

namespace Mohist.Server.TestSupport;

public sealed class RunnerCredentialRevocationFailureProbe
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _remaining = new(StringComparer.Ordinal);

    public void FailNext(string runnerId)
    {
        lock (_gate)
            _remaining[runnerId] = 1;
    }

    public void Reset()
    {
        lock (_gate)
            _remaining.Clear();
    }

    internal bool Consume(string runnerId)
    {
        lock (_gate)
        {
            if (!_remaining.TryGetValue(runnerId, out var remaining) || remaining <= 0)
                return false;
            if (remaining == 1)
                _remaining.Remove(runnerId);
            else
                _remaining[runnerId] = remaining - 1;
            return true;
        }
    }
}

public sealed class FaultInjectingCredentialStore(
    ICredentialStore inner,
    RunnerCredentialRevocationFailureProbe failures) : ICredentialStore
{
    public Task<Credential?> FindActiveAsync(string tokenHash, CancellationToken ct = default) =>
        inner.FindActiveAsync(tokenHash, ct);

    public Task CreateAsync(Credential credential, CancellationToken ct = default) =>
        inner.CreateAsync(credential, ct);

    public Task<PatCreateResult> CreatePatAsync(
        string principalId,
        string name,
        IReadOnlyList<Scope> scopes,
        DateTimeOffset expiresAt,
        CancellationToken ct = default,
        DirectApiProjectGrant? directApiProjectGrant = null) =>
        inner.CreatePatAsync(principalId, name, scopes, expiresAt, ct, directApiProjectGrant);

    public Task<IReadOnlyList<Credential>> ListPatAsync(
        string principalId,
        CancellationToken ct = default) =>
        inner.ListPatAsync(principalId, ct);

    public Task<bool> RevokePatAsync(
        string principalId,
        string name,
        DateTimeOffset revokedAt,
        CancellationToken ct = default) =>
        inner.RevokePatAsync(principalId, name, revokedAt, ct);

    public Task<bool> RevokeAsync(
        string tokenHash,
        DateTimeOffset revokedAt,
        CancellationToken ct = default) =>
        inner.RevokeAsync(tokenHash, revokedAt, ct);

    public Task<EnrollmentTokenCreateResult> CreateEnrollmentTokenAsync(
        DateTimeOffset expiresAt,
        CancellationToken ct = default) =>
        inner.CreateEnrollmentTokenAsync(expiresAt, ct);

    public Task<EnrollmentTokenConsumeStatus> ConsumeEnrollmentTokenAsync(
        string tokenHash,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        inner.ConsumeEnrollmentTokenAsync(tokenHash, now, ct);

    public Task<RunnerCredentialCreateResult?> CreateRunnerCredentialAsync(
        string principalId,
        string runnerId,
        CancellationToken ct = default) =>
        inner.CreateRunnerCredentialAsync(principalId, runnerId, ct);

    public Task<bool> RevokeRunnerCredentialAsync(
        string runnerId,
        DateTimeOffset revokedAt,
        CancellationToken ct = default) =>
        failures.Consume(runnerId)
            ? Task.FromException<bool>(new InvalidOperationException(
                $"Injected credential revocation failure for Runner {runnerId}."))
            : inner.RevokeRunnerCredentialAsync(runnerId, revokedAt, ct);

    public Task<IntegrationCreateResult> CreateIntegrationAsync(
        string principalId,
        string name,
        string projectId,
        CancellationToken ct = default) =>
        inner.CreateIntegrationAsync(principalId, name, projectId, ct);

    public Task<bool> RevokeIntegrationAsync(
        string principalId,
        string id,
        DateTimeOffset revokedAt,
        CancellationToken ct = default) =>
        inner.RevokeIntegrationAsync(principalId, id, revokedAt, ct);
}
