using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class CredentialStoreTests
{
    private const string TokenHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task FindActive_ReturnsCredential_WhenNotRevokedAndNotExpired()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""");

        var credential = await setup.Store.FindActiveAsync(TokenHash);

        Assert.NotNull(credential);
        Assert.Equal("admin", credential.PrincipalId);
        Assert.Equal(CredentialKind.Pat, credential.Kind);
        Assert.Equal(Scope.Operator, Assert.Single(credential.Scopes));
        Assert.Equal("ci", credential.Name);
    }

    [Fact]
    public async Task FindActive_ReturnsNull_WhenRowIsMissing()
    {
        using var setup = CreateStore();

        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task FindActive_ReturnsNull_WhenRevoked()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Pat",
            scopesJson: "[]",
            revokedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task FindActive_ReturnsNull_WhenExpired()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Pat",
            scopesJson: "[]",
            expiresAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        setup.Time.Advance(TimeSpan.FromDays(2));

        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task FindActive_ReturnsNull_WhenKindIsUnknown()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Bogus", scopesJson: "[]");

        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task FindActive_DropsUnknownScopesButKeepsKnownOnes()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator","future_scope"]""");

        var credential = await setup.Store.FindActiveAsync(TokenHash);

        Assert.NotNull(credential);
        Assert.Equal(Scope.Operator, Assert.Single(credential.Scopes));
    }

    [Fact]
    public async Task CreatePat_ReturnsTheFullTokenOnlyOnce_AndStoresHashAndPrefix()
    {
        using var setup = CreateStore();

        var result = await setup.Store.CreatePatAsync(
            "admin", "ci", [Scope.Readonly], setup.Time.GetUtcNow().AddDays(30));

        Assert.Equal(PatCreateStatus.Created, result.Status);
        Assert.NotNull(result.Token);
        Assert.StartsWith("moh_pat_", result.Token, StringComparison.Ordinal);
        Assert.Equal(CredentialToken.DisplayPrefix(result.Token), result.Credential!.Prefix);
        Assert.Equal(CredentialKind.Pat, result.Credential.Kind);
        Assert.Equal(Scope.Readonly, Assert.Single(result.Credential.Scopes));
        Assert.Equal("ci", result.Credential.Name);
        Assert.Equal(setup.Time.GetUtcNow().AddDays(30), result.Credential.ExpiresAt);
        Assert.Null(result.Credential.RevokedAt);

        // The full token is not persisted — only its hash and a short
        // display prefix are, so the value shown once cannot be recovered
        // from the store.
        var stored = await ReadRowAsync(setup.Connection);
        Assert.Equal(CredentialToken.Hash(result.Token), stored.TokenHash);
        Assert.Equal(CredentialToken.DisplayPrefix(result.Token), stored.Prefix);

        var resolved = await setup.Store.FindActiveAsync(CredentialToken.Hash(result.Token));
        Assert.NotNull(resolved);
    }

    [Fact]
    public async Task CreatePat_WithAnActiveName_IsRejectedAsDuplicate()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""");

        var result = await setup.Store.CreatePatAsync(
            "admin", "ci", [Scope.Operator], setup.Time.GetUtcNow().AddDays(30));

        Assert.Equal(PatCreateStatus.DuplicateName, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task CreatePat_WithARevokedName_Succeeds()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Pat",
            scopesJson: """["operator"]""",
            revokedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var result = await setup.Store.CreatePatAsync(
            "admin", "ci", [Scope.Operator], setup.Time.GetUtcNow().AddDays(30));

        Assert.Equal(PatCreateStatus.Created, result.Status);
    }

    [Fact]
    public async Task ListPat_ReturnsOnlyPatRowsOfThePrincipal_WithPrefixes()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""");
        await InsertAsync(
            setup.Connection,
            kind: "Session",
            scopesJson: """["operator"]""",
            tokenHash: "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
            name: "session-1");
        await InsertAsync(
            setup.Connection,
            kind: "Pat",
            scopesJson: """["operator"]""",
            principalId: "other",
            tokenHash: "1111111111111111111111111111111111111111111111111111111111111111",
            name: "other-ci");

        var credentials = await setup.Store.ListPatAsync("admin");

        var credential = Assert.Single(credentials);
        Assert.Equal("ci", credential.Name);
        Assert.Equal("moh_pat_abcdef", credential.Prefix);
        Assert.Null(credential.RevokedAt);
    }

    [Fact]
    public async Task RevokePat_SetsRevokedAt_AndIsIdempotent()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""");
        var revokedAt = setup.Time.GetUtcNow();

        var revoked = await setup.Store.RevokePatAsync("admin", "ci", revokedAt);
        var revokedAgain = await setup.Store.RevokePatAsync("admin", "ci", revokedAt);

        Assert.True(revoked);
        Assert.True(revokedAgain);
        var row = await ReadRowAsync(setup.Connection);
        Assert.Equal(revokedAt, row.RevokedAt);
        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task RevokePat_WithUnknownName_ReturnsFalse()
    {
        using var setup = CreateStore();

        var revoked = await setup.Store.RevokePatAsync(
            "admin", "missing", setup.Time.GetUtcNow());

        Assert.False(revoked);
    }

    [Fact]
    public async Task Create_ThenFindActive_ReturnsTheIssuedCredential()
    {
        using var setup = CreateStore();
        var now = setup.Time.GetUtcNow();
        var credential = new Credential(
            "cred_session", "admin", CredentialKind.Session, TokenHash,
            [Scope.Operator], Name: null, Prefix: null, ProjectId: null, FamilyId: null, now + TimeSpan.FromDays(7), RevokedAt: null, now);

        await setup.Store.CreateAsync(credential);

        var found = await setup.Store.FindActiveAsync(TokenHash);
        Assert.NotNull(found);
        Assert.Equal("admin", found.PrincipalId);
        Assert.Equal(CredentialKind.Session, found.Kind);
        Assert.Equal(Scope.Operator, Assert.Single(found.Scopes));
        Assert.Equal(now + TimeSpan.FromDays(7), found.ExpiresAt);
        Assert.Null(found.RevokedAt);
    }

    [Fact]
    public async Task Revoke_ThenFindActive_ReturnsNull()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""");
        var revokedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var revoked = await setup.Store.RevokeAsync(TokenHash, revokedAt);

        Assert.True(revoked);
        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task CreateIntegration_ReturnsTheFullTokenOnlyOnce_AndStoresProjectConstraint()
    {
        using var setup = CreateStore();

        var result = await setup.Store.CreateIntegrationAsync("admin", "github-webhook", "proj_a");

        Assert.Equal(IntegrationCreateStatus.Created, result.Status);
        Assert.NotNull(result.Token);
        Assert.StartsWith("moh_integration_", result.Token, StringComparison.Ordinal);
        Assert.Equal(CredentialToken.DisplayPrefix(result.Token), result.Credential!.Prefix);
        Assert.Equal(CredentialKind.Integration, result.Credential.Kind);
        Assert.Equal(Scope.Webhook, Assert.Single(result.Credential.Scopes));
        Assert.Equal("proj_a", result.Credential.ProjectId);
        Assert.Null(result.Credential.ExpiresAt);
        Assert.Null(result.Credential.RevokedAt);

        // The full token is not persisted — only its hash, a short
        // display prefix and the project constraint are.
        var stored = await ReadRowAsync(setup.Connection, name: "github-webhook");
        Assert.Equal(CredentialToken.Hash(result.Token), stored.TokenHash);
        Assert.Equal(CredentialToken.DisplayPrefix(result.Token), stored.Prefix);
        Assert.Equal("proj_a", stored.ProjectId);

        var resolved = await setup.Store.FindActiveAsync(CredentialToken.Hash(result.Token));
        Assert.NotNull(resolved);
        Assert.Equal("proj_a", resolved.ProjectId);
        Assert.Equal(Scope.Webhook, Assert.Single(resolved.Scopes));
    }

    [Fact]
    public async Task CreateIntegration_WithAnActiveName_IsRejectedAsDuplicate()
    {
        using var setup = CreateStore();
        await InsertAsync(setup.Connection, kind: "Integration", scopesJson: """["webhook"]""");

        var result = await setup.Store.CreateIntegrationAsync("admin", "ci", "proj_a");

        Assert.Equal(IntegrationCreateStatus.DuplicateName, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task FindActive_ReturnsTheProjectConstraint_ForIntegrationRows()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Integration",
            scopesJson: """["webhook"]""",
            name: "github-webhook",
            projectId: "proj_a");

        var credential = await setup.Store.FindActiveAsync(TokenHash);

        Assert.NotNull(credential);
        Assert.Equal(CredentialKind.Integration, credential.Kind);
        Assert.Equal("proj_a", credential.ProjectId);
        Assert.Equal(Scope.Webhook, Assert.Single(credential.Scopes));
    }

    [Fact]
    public async Task RevokeIntegration_ById_SetsRevokedAt_AndIsIdempotent()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Integration",
            scopesJson: """["webhook"]""",
            name: "github-webhook",
            projectId: "proj_a",
            id: "itok_victim");
        var revokedAt = setup.Time.GetUtcNow();

        var revoked = await setup.Store.RevokeIntegrationAsync("admin", "itok_victim", revokedAt);
        var revokedAgain = await setup.Store.RevokeIntegrationAsync("admin", "itok_victim", revokedAt);

        Assert.True(revoked);
        Assert.True(revokedAgain);
        var row = await ReadRowAsync(setup.Connection, name: "github-webhook");
        Assert.Equal(revokedAt, row.RevokedAt);
        Assert.Null(await setup.Store.FindActiveAsync(TokenHash));
    }

    [Fact]
    public async Task Revoke_UnknownOrAlreadyRevokedToken_ReturnsFalse()
    {
        using var setup = CreateStore();
        var revokedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.False(await setup.Store.RevokeAsync(TokenHash, revokedAt));

        await InsertAsync(setup.Connection, kind: "Pat", scopesJson: """["operator"]""", revokedAt: revokedAt);
        Assert.False(await setup.Store.RevokeAsync(TokenHash, revokedAt));
    }

    [Fact]
    public async Task RevokeIntegration_WithUnknownId_ReturnsFalse()
    {
        using var setup = CreateStore();

        var revoked = await setup.Store.RevokeIntegrationAsync(
            "admin", "missing", setup.Time.GetUtcNow());

        Assert.False(revoked);
    }

    [Fact]
    public async Task RevokeIntegration_OnlyAffectsTheMatchingIntegrationToken()
    {
        using var setup = CreateStore();
        await InsertAsync(
            setup.Connection,
            kind: "Integration",
            scopesJson: """["webhook"]""",
            tokenHash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            name: "victim",
            id: "itok_victim");
        await InsertAsync(
            setup.Connection,
            kind: "Integration",
            scopesJson: """["webhook"]""",
            tokenHash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            name: "survivor",
            id: "itok_survivor");

        var revoked = await setup.Store.RevokeIntegrationAsync(
            "admin", "itok_victim", setup.Time.GetUtcNow());

        Assert.True(revoked);
        Assert.Null(await setup.Store.FindActiveAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.NotNull(await setup.Store.FindActiveAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    private static async Task<CredentialRow> ReadRowAsync(SqliteConnection connection, string name = "ci")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"Credentials\" WHERE \"Name\" = $name;";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new CredentialRow
        {
            Id = reader.GetString(0),
            PrincipalId = reader.GetString(1),
            Kind = reader.GetString(2),
            TokenHash = reader.GetString(3),
            ScopesJson = reader.GetString(4),
            Name = reader.IsDBNull(5) ? null : reader.GetString(5),
            Prefix = reader.IsDBNull(6) ? null : reader.GetString(6),
            FamilyId = reader.IsDBNull(7) ? null : reader.GetString(7),
            ExpiresAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            RevokedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(10)),
            ProjectId = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    private static StoreSetup CreateStore()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var connection = new SqliteConnection("Data Source=credential-store-tests;Mode=Memory;Cache=Shared");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "Credentials" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Credentials" PRIMARY KEY,
                    "PrincipalId" TEXT NOT NULL,
                    "Kind" TEXT NOT NULL,
                    "TokenHash" TEXT NOT NULL,
                    "ScopesJson" TEXT NOT NULL,
                    "Name" TEXT NULL,
                    "Prefix" TEXT NULL,
                    "FamilyId" TEXT NULL,
                    "ExpiresAt" TEXT NULL,
                    "RevokedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ProjectId" TEXT NULL
                );
                CREATE UNIQUE INDEX "IX_Credentials_TokenHash" ON "Credentials" ("TokenHash");
                CREATE UNIQUE INDEX "IX_Credentials_PrincipalId_Name" ON "Credentials" ("PrincipalId", "Name") WHERE "RevokedAt" IS NULL;
                """;
            command.ExecuteNonQuery();
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var store = new CredentialStore(
            provider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            time);
        return new StoreSetup(store, time, connection, provider);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        string kind,
        string scopesJson,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null,
        string tokenHash = TokenHash,
        string principalId = "admin",
        string name = "ci",
        string? projectId = null,
        string? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Credentials" ("Id", "PrincipalId", "Kind", "TokenHash", "ScopesJson", "Name", "Prefix", "ExpiresAt", "RevokedAt", "CreatedAt", "ProjectId")
            VALUES ($id, $principalId, $kind, $tokenHash, $scopesJson, $name, $prefix, $expiresAt, $revokedAt, $createdAt, $projectId);
            """;
        command.Parameters.AddWithValue("$id", id ?? Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$principalId", principalId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$tokenHash", tokenHash);
        command.Parameters.AddWithValue("$scopesJson", scopesJson);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$prefix", "moh_pat_abcdef");
        command.Parameters.AddWithValue("$expiresAt", (object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$revokedAt", (object?)revokedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record StoreSetup(
        CredentialStore Store,
        FakeTimeProvider Time,
        SqliteConnection Connection,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Connection.Dispose();
        }
    }
}
