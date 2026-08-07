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
                    "ExpiresAt" TEXT NULL,
                    "RevokedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_Credentials_TokenHash" ON "Credentials" ("TokenHash");
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
        DateTimeOffset? revokedAt = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Credentials" ("Id", "PrincipalId", "Kind", "TokenHash", "ScopesJson", "Name", "ExpiresAt", "RevokedAt", "CreatedAt")
            VALUES ($id, $principalId, $kind, $tokenHash, $scopesJson, $name, $expiresAt, $revokedAt, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", "cred_1");
        command.Parameters.AddWithValue("$principalId", "admin");
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$tokenHash", TokenHash);
        command.Parameters.AddWithValue("$scopesJson", scopesJson);
        command.Parameters.AddWithValue("$name", "ci");
        command.Parameters.AddWithValue("$expiresAt", (object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$revokedAt", (object?)revokedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
