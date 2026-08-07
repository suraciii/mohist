using Microsoft.EntityFrameworkCore;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Auth;

public interface ICredentialStore
{
    /// <summary>
    /// Returns the credential whose token hash matches, or null when the
    /// row is missing, revoked, expired, or malformed — the caller never
    /// learns which.
    /// </summary>
    Task<Credential?> FindActiveAsync(string tokenHash, CancellationToken ct = default);
}

public sealed class CredentialStore : ICredentialStore, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public CredentialStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<Credential?> FindActiveAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);
        if (row is null || row.RevokedAt is not null)
            return null;

        var now = _time.GetUtcNow();
        if (row.ExpiresAt is not null && row.ExpiresAt.Value <= now)
            return null;
        if (!Enum.TryParse<CredentialKind>(row.Kind, ignoreCase: true, out var kind))
            return null;

        return new Credential(
            row.Id,
            row.PrincipalId,
            kind,
            row.TokenHash,
            DeserializeScopes(row.ScopesJson),
            row.Name,
            row.ExpiresAt,
            row.RevokedAt,
            row.CreatedAt);
    }

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
