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

        var grant = await LoadGrantAsync(db, row, ct).ConfigureAwait(false);
        return ToCredential(row, kind, grant);
    }

    public async Task<PatCreateResult> CreatePatAsync(
        string principalId,
        string name,
        IReadOnlyList<Scope> scopes,
        DateTimeOffset expiresAt,
        CancellationToken ct = default,
        DirectApiProjectGrant? directApiProjectGrant = null)
    {
        if (directApiProjectGrant is not null && !directApiProjectGrant.IsValid)
            return new PatCreateResult(PatCreateStatus.InvalidGrant, null, null);

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
            DirectApiProjectGrantKind = directApiProjectGrant?.StorageValue,
            ExpiresAt = expiresAt,
            CreatedAt = _time.GetUtcNow(),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await NameIsInUseAsync(db, principalId, name, ct).ConfigureAwait(false))
            return new PatCreateResult(PatCreateStatus.DuplicateName, null, null);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Credentials.Add(row);
        if (directApiProjectGrant is not null)
        {
            db.CredentialProjectGrants.AddRange(directApiProjectGrant.AllowedProjectIds.Select(projectId =>
                new CredentialProjectGrantRow
                {
                    CredentialId = row.Id,
                    ProjectId = projectId,
                }));
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            if (await NameIsInUseAsync(db, principalId, name, ct).ConfigureAwait(false))
            {
                // A concurrent issuance of the same name won the race; the
                // unique (PrincipalId, Name) index on active rows is the
                // backstop that turns both into one winner.
                return new PatCreateResult(PatCreateStatus.DuplicateName, null, null);
            }

            return new PatCreateResult(PatCreateStatus.InvalidGrant, null, null);
        }

        return new PatCreateResult(
            PatCreateStatus.Created,
            ToCredential(row, CredentialKind.Pat, directApiProjectGrant),
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
        var grants = await LoadGrantsAsync(db, rows.Select(row => row.Id), ct).ConfigureAwait(false);
        return rows
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => ToCredential(row, CredentialKind.Pat, grants.GetValueOrDefault(row.Id)))
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

    public async Task<IntegrationCreateResult> CreateIntegrationAsync(
        string principalId,
        string name,
        string projectId,
        CancellationToken ct = default)
    {
        var token = CredentialToken.Generate(CredentialKind.Integration);
        var row = new CredentialRow
        {
            Id = $"itok_{Guid.NewGuid():N}",
            PrincipalId = principalId,
            Kind = CredentialKind.Integration.ToString(),
            TokenHash = CredentialToken.Hash(token),
            ScopesJson = JSON.Serialize(new[] { Scope.Webhook.Name }),
            Name = name,
            Prefix = CredentialToken.DisplayPrefix(token),
            ProjectId = projectId,
            CreatedAt = _time.GetUtcNow(),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await NameIsInUseAsync(db, principalId, name, ct).ConfigureAwait(false))
            return new IntegrationCreateResult(IntegrationCreateStatus.DuplicateName, null, null);

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
            return new IntegrationCreateResult(IntegrationCreateStatus.DuplicateName, null, null);
        }

        return new IntegrationCreateResult(
            IntegrationCreateStatus.Created,
            ToCredential(row, CredentialKind.Integration),
            token);
    }

    public async Task<bool> RevokeIntegrationAsync(
        string principalId,
        string id,
        DateTimeOffset revokedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .FirstOrDefaultAsync(
                candidate => candidate.PrincipalId == principalId
                    && candidate.Kind.ToLower() == "integration"
                    && candidate.Id == id,
                ct)
            .ConfigureAwait(false);
        if (row is null)
            return false;

        row.RevokedAt ??= revokedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
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

    private static async Task<DirectApiProjectGrant?> LoadGrantAsync(
        MohistDbContext db,
        CredentialRow row,
        CancellationToken ct)
    {
        var grants = await LoadGrantsAsync(db, [row.Id], ct).ConfigureAwait(false);
        return grants.GetValueOrDefault(row.Id);
    }

    private static async Task<Dictionary<string, DirectApiProjectGrant>> LoadGrantsAsync(
        MohistDbContext db,
        IEnumerable<string> credentialIds,
        CancellationToken ct)
    {
        var ids = credentialIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return [];

        var rows = await db.Credentials
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .Select(row => new { row.Id, row.DirectApiProjectGrantKind })
            .ToDictionaryAsync(row => row.Id, ct)
            .ConfigureAwait(false);
        var projectRows = await db.CredentialProjectGrants
            .AsNoTracking()
            .Where(grant => ids.Contains(grant.CredentialId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<string, DirectApiProjectGrant>(StringComparer.Ordinal);
        foreach (var row in rows.Values)
        {
            if (!DirectApiProjectGrant.TryParse(row.DirectApiProjectGrantKind, out var kind))
                continue;

            var projectIds = projectRows
                .Where(grant => grant.CredentialId == row.Id)
                .Select(grant => grant.ProjectId)
                .ToArray();
            var grant = new DirectApiProjectGrant(kind, projectIds);
            if (grant.IsValid)
                result[row.Id] = grant;
        }

        return result;
    }

    private static EnrollmentToken ToEnrollmentToken(EnrollmentTokenRow row) =>
        new(row.TokenHash, row.ExpiresAt, row.ConsumedAt);

    private static Credential ToCredential(
        CredentialRow row,
        CredentialKind kind,
        DirectApiProjectGrant? directApiProjectGrant = null) =>
        new(
            row.Id,
            row.PrincipalId,
            kind,
            row.TokenHash,
            DeserializeScopes(row.ScopesJson),
            row.Name,
            row.Prefix,
            row.ProjectId,
            row.FamilyId,
            row.ExpiresAt,
            row.RevokedAt,
            row.CreatedAt)
        {
            DirectApiProjectGrant = directApiProjectGrant,
        };

    public async Task CreateAsync(Credential credential, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = new CredentialRow
        {
            Id = credential.Id,
            PrincipalId = credential.PrincipalId,
            Kind = credential.Kind.ToString(),
            TokenHash = credential.TokenHash,
            ScopesJson = SerializeScopes(credential.Scopes),
            Name = credential.Name,
            Prefix = credential.Prefix,
            ProjectId = credential.ProjectId,
            FamilyId = credential.FamilyId,
            DirectApiProjectGrantKind = credential.DirectApiProjectGrant?.StorageValue,
            ExpiresAt = credential.ExpiresAt,
            RevokedAt = credential.RevokedAt,
            CreatedAt = credential.CreatedAt,
        };
        db.Credentials.Add(row);
        if (credential.DirectApiProjectGrant is { } grant)
        {
            if (!grant.IsValid)
                throw new ArgumentException("Credential carries an invalid direct API Project grant.", nameof(credential));
            db.CredentialProjectGrants.AddRange(grant.AllowedProjectIds.Select(projectId =>
                new CredentialProjectGrantRow
                {
                    CredentialId = row.Id,
                    ProjectId = projectId,
                }));
        }
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
