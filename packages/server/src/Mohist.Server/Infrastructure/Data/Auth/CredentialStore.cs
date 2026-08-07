using Microsoft.EntityFrameworkCore;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Auth;

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

        return ToCredential(row, kind);
    }

    public async Task<PatCreateResult> CreatePatAsync(
        string principalId,
        string name,
        IReadOnlyList<Scope> scopes,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var token = CredentialToken.Generate(CredentialKind.Pat);
        var row = new CredentialRow
        {
            Id = $"pat_{Guid.NewGuid():N}",
            PrincipalId = principalId,
            Kind = CredentialKind.Pat.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = JSON.Serialize(scopes.Select(scope => scope.Name).ToArray()),
            Name = name,
            Prefix = CredentialToken.DisplayPrefix(token),
            ExpiresAt = expiresAt,
            CreatedAt = _time.GetUtcNow(),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await NameIsInUseAsync(db, principalId, name, ct).ConfigureAwait(false))
            return new PatCreateResult(PatCreateStatus.DuplicateName, null, null);

        db.Credentials.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent issuance of the same name won the race; the
            // unique (PrincipalId, Name) index on active rows is the
            // backstop that turns both into one winner.
            return new PatCreateResult(PatCreateStatus.DuplicateName, null, null);
        }

        return new PatCreateResult(
            PatCreateStatus.Created,
            ToCredential(row, CredentialKind.Pat),
            token);
    }

    public async Task<IReadOnlyList<Credential>> ListPatAsync(string principalId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Credentials
            .AsNoTracking()
            .Where(row => row.PrincipalId == principalId && row.Kind.ToLower() == "pat")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => ToCredential(row, CredentialKind.Pat))
            .ToList();
    }

    public async Task<bool> RevokePatAsync(
        string principalId,
        string name,
        DateTimeOffset revokedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .FirstOrDefaultAsync(
                candidate => candidate.PrincipalId == principalId
                    && candidate.Kind.ToLower() == "pat"
                    && candidate.Name == name,
                ct)
            .ConfigureAwait(false);
        if (row is null)
            return false;

        row.RevokedAt ??= revokedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<EnrollmentTokenCreateResult> CreateEnrollmentTokenAsync(
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var token = CredentialToken.GenerateEnrollmentToken();
        var row = new EnrollmentTokenRow
        {
            Id = $"enroll_{Guid.NewGuid():N}",
            TokenHash = CredentialToken.Hash(token),
            ExpiresAt = expiresAt,
            CreatedAt = _time.GetUtcNow(),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.EnrollmentTokens.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new EnrollmentTokenCreateResult(token, ToEnrollmentToken(row));
    }

    public async Task<EnrollmentTokenConsumeStatus> ConsumeEnrollmentTokenAsync(
        string tokenHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // The SQLite provider cannot translate DateTimeOffset comparisons
        // inside ExecuteUpdate, so the atomic conditional consume is one
        // parameterized UPDATE: only an unconsumed, unexpired row matches.
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "EnrollmentTokens"
            SET "ConsumedAt" = {now}
            WHERE "TokenHash" = {tokenHash} AND "ConsumedAt" IS NULL AND "ExpiresAt" > {now}
            """, ct)
            .ConfigureAwait(false);
        if (consumed == 1)
            return EnrollmentTokenConsumeStatus.Consumed;

        var row = await db.EnrollmentTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);
        if (row is null)
            return EnrollmentTokenConsumeStatus.NotFound;
        return row.ConsumedAt is not null
            ? EnrollmentTokenConsumeStatus.AlreadyConsumed
            : EnrollmentTokenConsumeStatus.Expired;
    }

    public async Task<RunnerCredentialCreateResult?> CreateRunnerCredentialAsync(
        string principalId,
        string runnerId,
        CancellationToken ct = default)
    {
        var token = CredentialToken.Generate(CredentialKind.Runner);
        var row = new CredentialRow
        {
            Id = $"runner_{Guid.NewGuid():N}",
            PrincipalId = principalId,
            Kind = CredentialKind.Runner.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = JSON.Serialize<string[]>([Scope.Runner.Name]),
            Name = runnerId,
            Prefix = CredentialToken.DisplayPrefix(token),
            CreatedAt = _time.GetUtcNow(),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // A runner has at most one live credential: re-registration (the
        // recovery path) revokes the previous one inside the same
        // transaction, so the active-unique (PrincipalId, Name) index never
        // sees two live rows for one runner.
        await db.Credentials
            .Where(candidate => candidate.Kind.ToLower() == "runner"
                && candidate.Name == runnerId
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RevokedAt, row.CreatedAt), ct)
            .ConfigureAwait(false);
        db.Credentials.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent registration of the same runner won the race;
            // the caller reports it and the install flow is re-run.
            return null;
        }

        return new RunnerCredentialCreateResult(token, ToCredential(row, CredentialKind.Runner));
    }

    public async Task<bool> RevokeRunnerCredentialAsync(
        string runnerId,
        DateTimeOffset revokedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var revoked = await db.Credentials
            .Where(candidate => candidate.Kind.ToLower() == "runner"
                && candidate.Name == runnerId
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.RevokedAt, revokedAt), ct)
            .ConfigureAwait(false);
        return revoked > 0;
    }

    private static async Task<bool> NameIsInUseAsync(
        MohistDbContext db,
        string principalId,
        string name,
        CancellationToken ct)
    {
        return await db.Credentials
            .AnyAsync(
                candidate => candidate.PrincipalId == principalId
                    && candidate.Name == name
                    && candidate.RevokedAt == null,
                ct)
            .ConfigureAwait(false);
    }

    private static EnrollmentToken ToEnrollmentToken(EnrollmentTokenRow row) =>
        new(row.TokenHash, row.ExpiresAt, row.ConsumedAt);

    private static Credential ToCredential(CredentialRow row, CredentialKind kind) =>
        new(
            row.Id,
            row.PrincipalId,
            kind,
            row.TokenHash,
            DeserializeScopes(row.ScopesJson),
            row.Name,
            row.Prefix,
            row.ExpiresAt,
            row.RevokedAt,
            row.CreatedAt);

    public async Task CreateAsync(Credential credential, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Credentials.Add(new CredentialRow
        {
            Id = credential.Id,
            PrincipalId = credential.PrincipalId,
            Kind = credential.Kind.ToString(),
            TokenHash = credential.TokenHash,
            ScopesJson = SerializeScopes(credential.Scopes),
            Name = credential.Name,
            Prefix = credential.Prefix,
            ExpiresAt = credential.ExpiresAt,
            RevokedAt = credential.RevokedAt,
            CreatedAt = credential.CreatedAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(string tokenHash, DateTimeOffset revokedAt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, ct)
            .ConfigureAwait(false);
        if (row is null || row.RevokedAt is not null)
            return false;

        row.RevokedAt = revokedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static string SerializeScopes(IReadOnlyList<Scope> scopes) =>
        JSON.Serialize(scopes.Select(scope => scope.Name).ToArray());

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
