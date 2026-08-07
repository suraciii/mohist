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
}
