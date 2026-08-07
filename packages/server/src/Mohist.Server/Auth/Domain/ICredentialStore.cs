namespace Mohist.Server.Auth.Domain;

public enum PatCreateStatus
{
    Created,
    DuplicateName,
}

public sealed record PatCreateResult(PatCreateStatus Status, Credential? Credential, string? Token);

/// <summary>
/// Persistence contract for issued credentials. Lives in the domain so the
/// API and identity layers depend on the abstraction, not the EF store: the
/// SHA-256-only storage model and one-time token disclosure are invariants
/// the domain owns, while the row mapping stays in Infrastructure.Data.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Returns the credential whose token hash matches, or null when the
    /// row is missing, revoked, expired, or malformed — the caller never
    /// learns which.
    /// </summary>
    Task<Credential?> FindActiveAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// Persists an issued credential (used for browser session issuance).
    /// </summary>
    Task CreateAsync(Credential credential, CancellationToken ct = default);

    /// <summary>
    /// Issues a PAT for the given principal: generates the token, stores
    /// only its hash (plus a short display prefix) and returns the full
    /// value exactly once. The name must be unused by any active
    /// (non-revoked) credential of the same principal; a concurrent
    /// duplicate is rejected by the same rule.
    /// </summary>
    Task<PatCreateResult> CreatePatAsync(
        string principalId,
        string name,
        IReadOnlyList<Scope> scopes,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// All PATs of the principal, revoked or not, newest first. Full token
    /// values never leave the store; only display prefixes do.
    /// </summary>
    Task<IReadOnlyList<Credential>> ListPatAsync(string principalId, CancellationToken ct = default);

    /// <summary>
    /// Revokes the principal's PAT with the given name. Idempotent: true
    /// when a row with that name exists (already revoked or not), false
    /// when there is no such credential.
    /// </summary>
    Task<bool> RevokePatAsync(
        string principalId,
        string name,
        DateTimeOffset revokedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the credential with the given token hash. False when no
    /// active row matched — the caller never learns whether the token
    /// was unknown or already revoked.
    /// </summary>
    Task<bool> RevokeAsync(string tokenHash, DateTimeOffset revokedAt, CancellationToken ct = default);

    /// <summary>
    /// Issues a one-time runner enrollment token: generates the value,
    /// stores only its SHA-256 hash and returns the full value exactly
    /// once. Unbound — whoever consumes it registers their own RunnerId.
    /// </summary>
    Task<EnrollmentTokenCreateResult> CreateEnrollmentTokenAsync(
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes the enrollment token at <paramref name="now"/>:
    /// succeeds only when the row exists, is not yet consumed and has not
    /// expired. The caller never learns the token's value, only the
    /// outcome class.
    /// </summary>
    Task<EnrollmentTokenConsumeStatus> ConsumeEnrollmentTokenAsync(
        string tokenHash,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a runner machine credential bound to
    /// <paramref name="runnerId"/> under the given principal. Any
    /// still-active credential of the same runner is revoked first, so a
    /// runner has at most one live credential and re-install replaces the
    /// previous one. Returns the full value exactly once; the store keeps
    /// only its hash. Null when a concurrent registration of the same
    /// runner won the race.
    /// </summary>
    Task<RunnerCredentialCreateResult?> CreateRunnerCredentialAsync(
        string principalId,
        string runnerId,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes every active credential of the runner. Idempotent: false
    /// when the runner had no active credential.
    /// </summary>
    Task<bool> RevokeRunnerCredentialAsync(
        string runnerId,
        DateTimeOffset revokedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Issues an integration token for the given principal and project:
    /// generates the token, stores only its hash (plus a short display
    /// prefix) and returns the full value exactly once. The credential
    /// carries the <c>webhook</c> scope and the canonical project id it
    /// is narrowed to. The name must be unused by any active
    /// (non-revoked) credential of the same principal; a concurrent
    /// duplicate is rejected by the same rule.
    /// </summary>
    Task<IntegrationCreateResult> CreateIntegrationAsync(
        string principalId,
        string name,
        string projectId,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the principal's integration token with the given id.
    /// Idempotent: true when a row with that id exists (already revoked
    /// or not), false when there is no such credential.
    /// </summary>
    Task<bool> RevokeIntegrationAsync(
        string principalId,
        string id,
        DateTimeOffset revokedAt,
        CancellationToken ct = default);
}

public enum EnrollmentTokenConsumeStatus
{
    Consumed,
    NotFound,
    Expired,
    AlreadyConsumed,
}

public sealed record EnrollmentTokenCreateResult(string Token, EnrollmentToken EnrollmentToken);

public sealed record RunnerCredentialCreateResult(string Token, Credential Credential);

public enum IntegrationCreateStatus
{
    Created,
    DuplicateName,
}

public sealed record IntegrationCreateResult(
    IntegrationCreateStatus Status,
    Credential? Credential,
    string? Token);
