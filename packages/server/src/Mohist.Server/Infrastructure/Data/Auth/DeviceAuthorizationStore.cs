using Microsoft.EntityFrameworkCore;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Auth;

/// <summary>
/// EF-backed <see cref="IDeviceAuthorizationStore"/>. The Pending→Approved
/// and Approved→Issued transitions are conditional UPDATEs so a race has
/// exactly one winner; refresh rotation revokes the presented token in
/// the same transaction that mints its replacement, and a replay of a
/// revoked refresh revokes the whole family (RFC 9700 §4.14.2).
/// </summary>
public sealed class DeviceAuthorizationStore : IDeviceAuthorizationStore, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public DeviceAuthorizationStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task CreateAsync(DeviceAuthorization authorization, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.DeviceAuthorizations.Add(new DeviceAuthorizationRow
        {
            Id = authorization.Id,
            DeviceCodeHash = authorization.DeviceCodeHash,
            UserCodeHash = authorization.UserCodeHash,
            ClientName = authorization.ClientName,
            Status = authorization.Status.ToString(),
            PrincipalId = authorization.PrincipalId,
            DecidedAt = authorization.DecidedAt,
            ExpiresAt = authorization.ExpiresAt,
            CreatedAt = authorization.CreatedAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<DeviceAuthorization?> FindByDeviceCodeHashAsync(
        string deviceCodeHash,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.DeviceAuthorizations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.DeviceCodeHash == deviceCodeHash, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToAuthorization(row);
    }

    public async Task<DeviceAuthorization?> FindByUserCodeHashAsync(
        string userCodeHash,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.DeviceAuthorizations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.UserCodeHash == userCodeHash, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToAuthorization(row);
    }

    public async Task<DeviceDecisionResult> DecideAsync(
        string deviceAuthorizationId,
        DeviceFlowStatus decision,
        string principalId,
        DateTimeOffset decidedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Only a still-pending flow can be decided; the conditional
        // UPDATE keeps a double-click from flipping an already-recorded
        // decision.
        var decided = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "DeviceAuthorizations"
            SET "Status" = {decision.ToString()}, "PrincipalId" = {principalId}, "DecidedAt" = {decidedAt}
            WHERE "Id" = {deviceAuthorizationId} AND "Status" = 'Pending'
            """, ct)
            .ConfigureAwait(false);
        if (decided == 1)
            return new DeviceDecisionResult(DeviceDecisionStatus.Decided, decision);

        var row = await db.DeviceAuthorizations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == deviceAuthorizationId, ct)
            .ConfigureAwait(false);
        if (row is null)
            return new DeviceDecisionResult(DeviceDecisionStatus.NotFound, null);

        return Enum.TryParse<DeviceFlowStatus>(row.Status, ignoreCase: true, out var status)
            ? new DeviceDecisionResult(DeviceDecisionStatus.AlreadyDecided, status)
            : new DeviceDecisionResult(DeviceDecisionStatus.AlreadyDecided, null);
    }

    public async Task<DeviceTokenIssueResult> IssueDeviceTokensAsync(
        string deviceAuthorizationId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // One winner per flow: the conditional consume and the credential
        // issuance commit atomically, so a concurrent poll can never mint
        // a second pair.
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "DeviceAuthorizations"
            SET "Status" = 'Issued'
            WHERE "Id" = {deviceAuthorizationId} AND "Status" = 'Approved'
            """, ct)
            .ConfigureAwait(false);
        if (consumed == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new DeviceTokenIssueResult(CurrentIssueStatus(db, deviceAuthorizationId, now), null, null, null, null);
        }

        var access = CredentialToken.Generate(CredentialKind.Session);
        var refresh = CredentialToken.Generate(CredentialKind.Refresh);
        db.Credentials.AddRange(
            DeviceCredentialRow(deviceAuthorizationId, access, CredentialKind.Session, now + DeviceFlowPolicy.AccessTtl, now),
            DeviceCredentialRow(deviceAuthorizationId, refresh, CredentialKind.Refresh, now + DeviceFlowPolicy.RefreshTtl, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new DeviceTokenIssueResult(
            DeviceTokenIssueStatus.Issued,
            access,
            refresh,
            new Credential(
                $"device_access_{Guid.NewGuid():N}",
                MohistPrincipal.AdminPrincipalId,
                CredentialKind.Session,
                CredentialToken.Hash(access),
                [Scope.Operator],
                Name: null,
                Prefix: null,
                FamilyId: deviceAuthorizationId,
                now + DeviceFlowPolicy.AccessTtl,
                RevokedAt: null,
                now),
            new Credential(
                $"device_refresh_{Guid.NewGuid():N}",
                MohistPrincipal.AdminPrincipalId,
                CredentialKind.Refresh,
                CredentialToken.Hash(refresh),
                [Scope.Operator],
                Name: null,
                Prefix: null,
                FamilyId: deviceAuthorizationId,
                now + DeviceFlowPolicy.RefreshTtl,
                RevokedAt: null,
                now));
    }

    public async Task<RefreshRotationResult> RotateRefreshAsync(
        string refreshTokenHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var presented = await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == refreshTokenHash, ct)
            .ConfigureAwait(false);
        if (presented is null || presented.Kind.ToLower() != "refresh")
            return new RefreshRotationResult(RefreshRotationStatus.NotFound, null, null, null, null);
        if (presented.RevokedAt is not null)
        {
            // Presenting a revoked refresh is a leak (RFC 9700 §4.14.2):
            // revoke the whole session family.
            await RevokeFamilyCoreAsync(db, presented.FamilyId, now, ct).ConfigureAwait(false);
            return new RefreshRotationResult(RefreshRotationStatus.ReplayDetected, null, null, null, null);
        }
        if (presented.ExpiresAt is not null && presented.ExpiresAt.Value <= now)
            return new RefreshRotationResult(RefreshRotationStatus.Expired, null, null, null, null);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // The conditional revoke is the rotation: exactly one concurrent
        // presenter wins; a loser finds its token already revoked and
        // reads as a replay above on its next attempt.
        var revoked = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "Credentials"
            SET "RevokedAt" = {now}
            WHERE "TokenHash" = {refreshTokenHash} AND "RevokedAt" IS NULL
            """, ct)
            .ConfigureAwait(false);
        if (revoked == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            await RevokeFamilyCoreAsync(db, presented.FamilyId, now, ct).ConfigureAwait(false);
            return new RefreshRotationResult(RefreshRotationStatus.ReplayDetected, null, null, null, null);
        }

        var access = CredentialToken.Generate(CredentialKind.Session);
        var refresh = CredentialToken.Generate(CredentialKind.Refresh);
        db.Credentials.AddRange(
            DeviceCredentialRow(presented.FamilyId!, access, CredentialKind.Session, now + DeviceFlowPolicy.AccessTtl, now),
            DeviceCredentialRow(presented.FamilyId!, refresh, CredentialKind.Refresh, now + DeviceFlowPolicy.RefreshTtl, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new RefreshRotationResult(
            RefreshRotationStatus.Rotated,
            access,
            refresh,
            new Credential(
                $"device_access_{Guid.NewGuid():N}",
                presented.PrincipalId,
                CredentialKind.Session,
                CredentialToken.Hash(access),
                DeserializeScopes(presented.ScopesJson),
                Name: null,
                Prefix: null,
                FamilyId: presented.FamilyId,
                now + DeviceFlowPolicy.AccessTtl,
                RevokedAt: null,
                now),
            new Credential(
                $"device_refresh_{Guid.NewGuid():N}",
                presented.PrincipalId,
                CredentialKind.Refresh,
                CredentialToken.Hash(refresh),
                DeserializeScopes(presented.ScopesJson),
                Name: null,
                Prefix: null,
                FamilyId: presented.FamilyId,
                now + DeviceFlowPolicy.RefreshTtl,
                RevokedAt: null,
                now));
    }

    public async Task<bool> RevokeFamilyAsync(
        string familyId,
        DateTimeOffset revokedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await RevokeFamilyCoreAsync(db, familyId, revokedAt, ct).ConfigureAwait(false);
    }

    public async Task<string?> FindFamilyIdByRefreshTokenAsync(
        string refreshTokenHash,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == refreshTokenHash, ct)
            .ConfigureAwait(false);
        return row?.Kind.ToLower() == "refresh" && !string.IsNullOrEmpty(row.FamilyId) ? row.FamilyId : null;
    }

    private static async Task<bool> RevokeFamilyCoreAsync(
        MohistDbContext db,
        string? familyId,
        DateTimeOffset revokedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(familyId))
            return false;
        var revoked = await db.Credentials
            .Where(candidate => candidate.FamilyId == familyId && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RevokedAt, revokedAt), ct)
            .ConfigureAwait(false);
        return revoked > 0;
    }

    private static DeviceTokenIssueStatus CurrentIssueStatus(
        MohistDbContext db,
        string deviceAuthorizationId,
        DateTimeOffset now)
    {
        var row = db.DeviceAuthorizations
            .AsNoTracking()
            .FirstOrDefault(candidate => candidate.Id == deviceAuthorizationId);
        if (row is null)
            return DeviceTokenIssueStatus.NotFound;
        return Enum.TryParse<DeviceFlowStatus>(row.Status, ignoreCase: true, out var status)
            ? status switch
            {
                DeviceFlowStatus.Pending => DeviceTokenIssueStatus.Pending,
                DeviceFlowStatus.Denied => DeviceTokenIssueStatus.Denied,
                _ => DeviceTokenIssueStatus.AlreadyIssued,
            }
            : DeviceTokenIssueStatus.AlreadyIssued;
    }

    private static CredentialRow DeviceCredentialRow(
        string familyId,
        string token,
        CredentialKind kind,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = $"device_{Guid.NewGuid():N}",
            PrincipalId = MohistPrincipal.AdminPrincipalId,
            Kind = kind.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = JSON.Serialize<string[]>([Scope.Operator.Name]),
            Name = null,
            Prefix = CredentialToken.DisplayPrefix(token),
            FamilyId = familyId,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            CreatedAt = createdAt,
        };

    private static DeviceAuthorization ToAuthorization(DeviceAuthorizationRow row) =>
        new(
            row.Id,
            row.DeviceCodeHash,
            row.UserCodeHash,
            row.ClientName,
            Enum.TryParse<DeviceFlowStatus>(row.Status, ignoreCase: true, out var status)
                ? status
                : DeviceFlowStatus.Pending,
            row.PrincipalId,
            row.DecidedAt,
            row.ExpiresAt,
            row.CreatedAt);

    private static IReadOnlyList<Scope> DeserializeScopes(string json)
    {
        var names = JSON.Deserialize<string[]>(json);
        if (names is null)
            return [];

        var scopes = new List<Scope>(names.Length);
        foreach (var name in names)
        {
            if (Scope.TryParse(name, out var scope))
                scopes.Add(scope);
        }

        return scopes;
    }
}
