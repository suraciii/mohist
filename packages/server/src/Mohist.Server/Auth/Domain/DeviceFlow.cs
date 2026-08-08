using System.Security.Cryptography;

namespace Mohist.Server.Auth.Domain;

public enum DeviceFlowStatus
{
    Pending,
    Approved,
    Denied,
    Issued,
}

/// <summary>
/// A pending RFC 8628 device authorization. Only hashes of the device
/// code and user code are persisted; the values themselves travel only
/// to their intended holders. The row doubles as the session family
/// anchor: every access/refresh credential minted from it carries its Id
/// as <see cref="Credential.FamilyId"/>.
/// </summary>
public sealed record DeviceAuthorization(
    string Id,
    string DeviceCodeHash,
    string UserCodeHash,
    string? ClientName,
    DeviceFlowStatus Status,
    string? PrincipalId,
    DateTimeOffset? DecidedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// RFC 8628 device-flow policy: 8-character user codes from a
/// confusion-free alphabet (no I/O/0/1, same as the Slack claim-code
/// precedent), a ten-minute pending window with five-second polling,
/// and the issued session shape — access (session, 1h) + refresh (30d)
/// — that rolling refresh keeps alive.
/// </summary>
public static class DeviceFlowPolicy
{
    public const int UserCodeLength = 8;
    public const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int MaxClientNameLength = 256;

    public static readonly TimeSpan FlowTtl = TimeSpan.FromSeconds(600);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AccessTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(30);

    public static string GenerateUserCode()
    {
        var code = new char[UserCodeLength];
        for (var index = 0; index < UserCodeLength; index++)
            code[index] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return new string(code);
    }

    /// <summary>
    /// Canonicalizes typed user codes: case and hyphens are ignored
    /// (the CLI shows the code as XXXX-XXXX; the confirmation page
    /// accepts the grouped form).
    /// </summary>
    public static string NormalizeUserCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = new char[input.Length];
        var count = 0;
        foreach (var character in input)
        {
            var upper = char.ToUpperInvariant(character);
            if (UserCodeAlphabet.IndexOf(upper) >= 0)
                normalized[count++] = upper;
        }

        return new string(normalized, 0, count);
    }

    /// <summary>Groups a canonical 8-character code for display as XXXX-XXXX.</summary>
    public static string DisplayUserCode(string userCode) =>
        userCode.Length == UserCodeLength
            ? $"{userCode[..4]}-{userCode[4..]}"
            : userCode;
}

/// <summary>
/// Persistence contract for device authorizations and the session
/// chains they spawn. Lives in the domain so the API layer depends on
/// the abstraction: hash-only storage, the atomic Pending→Approved→Issued
/// transitions and refresh rotation (with RFC 9700 §4.14.2 family
/// revocation on replay) are invariants the domain owns.
/// </summary>
public interface IDeviceAuthorizationStore
{
    Task CreateAsync(DeviceAuthorization authorization, CancellationToken ct = default);

    Task<DeviceAuthorization?> FindByDeviceCodeHashAsync(string deviceCodeHash, CancellationToken ct = default);

    Task<DeviceAuthorization?> FindByUserCodeHashAsync(string userCodeHash, CancellationToken ct = default);

    /// <summary>
    /// Records the confirmation-page decision on a still-pending flow
    /// (identified by its Id, which verify resolved from the user code).
    /// Idempotent for a repeated identical decision; a conflicting
    /// decision on an already-decided flow reports the current status.
    /// </summary>
    Task<DeviceDecisionResult> DecideAsync(
        string deviceAuthorizationId,
        DeviceFlowStatus decision,
        string principalId,
        DateTimeOffset decidedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes an approved flow and mints its access +
    /// refresh pair (both carrying the flow Id as family). One winner
    /// per flow: a concurrent poll loses and learns the current status.
    /// </summary>
    Task<DeviceTokenIssueResult> IssueDeviceTokensAsync(
        string deviceAuthorizationId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Rolls a refresh token forward: the presented token is revoked
    /// immediately and a fresh access + refresh pair (same family) is
    /// minted. Presenting a revoked refresh is treated as a leak — the
    /// whole family is revoked (RFC 9700 §4.14.2).
    /// </summary>
    Task<RefreshRotationResult> RotateRefreshAsync(
        string refreshTokenHash,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// The session family anchored by the credential whose refresh token
    /// hash matches — the chain logout revokes. Null when the hash
    /// matches nothing (revoked or not) or the row is not a device
    /// session.
    /// </summary>
    Task<string?> FindFamilyIdByRefreshTokenAsync(string refreshTokenHash, CancellationToken ct = default);

    /// <summary>
    /// Revokes every still-active credential of the session family.
    /// Idempotent; false when the family has no active credential.
    /// </summary>
    Task<bool> RevokeFamilyAsync(string familyId, DateTimeOffset revokedAt, CancellationToken ct = default);
}

public sealed record DeviceDecisionResult(DeviceDecisionStatus Status, DeviceFlowStatus? CurrentStatus);

public enum DeviceDecisionStatus
{
    Decided,
    NotFound,
    AlreadyDecided,
}

public sealed record DeviceTokenIssueResult(
    DeviceTokenIssueStatus Status,
    string? AccessToken,
    string? RefreshToken,
    Credential? Access,
    Credential? Refresh);

public enum DeviceTokenIssueStatus
{
    Issued,
    NotFound,
    Pending,
    Denied,
    AlreadyIssued,
}

public sealed record RefreshRotationResult(
    RefreshRotationStatus Status,
    string? AccessToken,
    string? RefreshToken,
    Credential? Access,
    Credential? Refresh);

public enum RefreshRotationStatus
{
    Rotated,
    NotFound,
    Expired,
    ReplayDetected,
}
