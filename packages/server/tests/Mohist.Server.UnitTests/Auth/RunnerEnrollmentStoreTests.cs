using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

/// <summary>
/// Enrollment token issuance/consumption and runner machine credentials
/// (docs/auth.md "Runner：安装即注册"): hash-only storage, single-use and
/// 15-minute expiry for enrollment tokens; runner credentials bound to
/// their RunnerId with at most one live credential per runner.
/// </summary>
public sealed class RunnerEnrollmentStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateEnrollmentToken_ReturnsTheFullValueOnce_AndStoresOnlyItsHash()
    {
        using var setup = CreateStore();
        var expiresAt = setup.Time.GetUtcNow().AddMinutes(15);

        var result = await setup.Store.CreateEnrollmentTokenAsync(expiresAt);

        Assert.StartsWith("moh_enroll_", result.Token, StringComparison.Ordinal);
        Assert.Equal(expiresAt, result.EnrollmentToken.ExpiresAt);
        Assert.Null(result.EnrollmentToken.ConsumedAt);

        var row = await ReadEnrollmentTokenRowAsync(setup.Connection, CredentialToken.Hash(result.Token));
        Assert.NotNull(row);
        Assert.Equal(CredentialToken.Hash(result.Token), row!.TokenHash);
        Assert.Equal(expiresAt, row.ExpiresAt);
        Assert.Null(row.ConsumedAt);
    }

    [Fact]
    public async Task ConsumeEnrollmentToken_ConsumesExactlyOnce()
    {
        using var setup = CreateStore();
        var result = await setup.Store.CreateEnrollmentTokenAsync(setup.Time.GetUtcNow().AddMinutes(15));

        var first = await setup.Store.ConsumeEnrollmentTokenAsync(
            CredentialToken.Hash(result.Token), setup.Time.GetUtcNow());
        var second = await setup.Store.ConsumeEnrollmentTokenAsync(
            CredentialToken.Hash(result.Token), setup.Time.GetUtcNow());

        Assert.Equal(EnrollmentTokenConsumeStatus.Consumed, first);
        Assert.Equal(EnrollmentTokenConsumeStatus.AlreadyConsumed, second);
    }

    [Fact]
    public async Task ConsumeEnrollmentToken_ExpiredToken_ReturnsExpired()
    {
        using var setup = CreateStore();
        var result = await setup.Store.CreateEnrollmentTokenAsync(setup.Time.GetUtcNow().AddMinutes(15));

        setup.Time.Advance(TimeSpan.FromMinutes(16));

        var status = await setup.Store.ConsumeEnrollmentTokenAsync(
            CredentialToken.Hash(result.Token), setup.Time.GetUtcNow());

        Assert.Equal(EnrollmentTokenConsumeStatus.Expired, status);
    }

    [Fact]
    public async Task ConsumeEnrollmentToken_UnknownToken_ReturnsNotFound()
    {
        using var setup = CreateStore();

        var status = await setup.Store.ConsumeEnrollmentTokenAsync(
            CredentialToken.Hash("moh_enroll_unknown"), setup.Time.GetUtcNow());

        Assert.Equal(EnrollmentTokenConsumeStatus.NotFound, status);
    }

    [Fact]
    public async Task CreateRunnerCredential_BindsTheRunnerId_AndResolvesAsActiveCredential()
    {
        using var setup = CreateStore();

        var result = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-a");

        Assert.NotNull(result);
        Assert.StartsWith("moh_runner_", result!.Token, StringComparison.Ordinal);
        Assert.Equal("runner-a", result.Credential.Name);
        Assert.Equal(CredentialKind.Runner, result.Credential.Kind);
        Assert.Equal(Scope.Runner, Assert.Single(result.Credential.Scopes));
        Assert.Equal("admin", result.Credential.PrincipalId);
        Assert.Null(result.Credential.ExpiresAt);
        Assert.Null(result.Credential.RevokedAt);

        var row = await ReadCredentialRowAsync(setup.Connection, result.Credential.Name!);
        Assert.Equal(CredentialToken.Hash(result.Token), row!.TokenHash);
        Assert.Equal(CredentialToken.DisplayPrefix(result.Token), row.Prefix);
        Assert.Equal("""["runner"]""", row.ScopesJson);

        var resolved = await setup.Store.FindActiveAsync(CredentialToken.Hash(result.Token));
        Assert.NotNull(resolved);
        Assert.Equal(CredentialKind.Runner, resolved!.Kind);
        Assert.Equal("runner-a", resolved.Name);
    }

    [Fact]
    public async Task CreateRunnerCredential_ReplacesAnExistingActiveCredential()
    {
        using var setup = CreateStore();
        var first = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-a");
        var second = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-a");

        Assert.NotNull(second);
        Assert.Null(await setup.Store.FindActiveAsync(CredentialToken.Hash(first!.Token)));
        Assert.NotNull(await setup.Store.FindActiveAsync(CredentialToken.Hash(second!.Token)));
    }

    [Fact]
    public async Task CreateRunnerCredential_DoesNotAffectOtherRunners()
    {
        using var setup = CreateStore();
        var runnerA = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-a");
        var runnerB = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-b");

        await setup.Store.RevokeRunnerCredentialAsync("runner-a", setup.Time.GetUtcNow());

        Assert.Null(await setup.Store.FindActiveAsync(CredentialToken.Hash(runnerA!.Token)));
        Assert.NotNull(await setup.Store.FindActiveAsync(CredentialToken.Hash(runnerB!.Token)));
    }

    [Fact]
    public async Task RevokeRunnerCredential_RevokesAndIsIdempotent()
    {
        using var setup = CreateStore();
        var result = await setup.Store.CreateRunnerCredentialAsync("admin", "runner-a");

        var revoked = await setup.Store.RevokeRunnerCredentialAsync("runner-a", setup.Time.GetUtcNow());
        var revokedAgain = await setup.Store.RevokeRunnerCredentialAsync("runner-a", setup.Time.GetUtcNow());

        Assert.True(revoked);
        Assert.False(revokedAgain);
        Assert.Null(await setup.Store.FindActiveAsync(CredentialToken.Hash(result!.Token)));
    }

    [Fact]
    public async Task RevokeRunnerCredential_UnknownRunner_ReturnsFalse()
    {
        using var setup = CreateStore();

        var revoked = await setup.Store.RevokeRunnerCredentialAsync("missing", setup.Time.GetUtcNow());

        Assert.False(revoked);
    }

    private static async Task<EnrollmentTokenRow?> ReadEnrollmentTokenRowAsync(SqliteConnection connection, string tokenHash)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"TokenHash\", \"ExpiresAt\", \"ConsumedAt\", \"CreatedAt\" FROM \"EnrollmentTokens\" WHERE \"TokenHash\" = $hash;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new EnrollmentTokenRow
        {
            Id = reader.GetString(0),
            TokenHash = reader.GetString(1),
            ExpiresAt = DateTimeOffset.Parse(reader.GetString(2)),
            ConsumedAt = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
        };
    }

    private static async Task<CredentialRow?> ReadCredentialRowAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"Credentials\" WHERE \"Name\" = $name AND \"Kind\" = 'Runner';";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
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
        };
    }

    private static StoreSetup CreateStore()
    {
        var time = new FakeTimeProvider(Now);
        var connection = new SqliteConnection("Data Source=runner-enrollment-store-tests;Mode=Memory;Cache=Shared");
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
                    "ProjectId" TEXT NULL,
                    "DirectApiProjectGrantKind" TEXT NULL
                );
                CREATE UNIQUE INDEX "IX_Credentials_TokenHash" ON "Credentials" ("TokenHash");
                CREATE UNIQUE INDEX "IX_Credentials_PrincipalId_Name" ON "Credentials" ("PrincipalId", "Name") WHERE "RevokedAt" IS NULL;
                CREATE TABLE "CredentialProjectGrants" (
                    "CredentialId" TEXT NOT NULL,
                    "ProjectId" TEXT NOT NULL,
                    CONSTRAINT "PK_CredentialProjectGrants" PRIMARY KEY ("CredentialId", "ProjectId")
                );
                CREATE UNIQUE INDEX "UX_CredentialProjectGrants_CredentialId_ProjectId" ON "CredentialProjectGrants" ("CredentialId", "ProjectId");
                CREATE TABLE "EnrollmentTokens" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_EnrollmentTokens" PRIMARY KEY,
                    "TokenHash" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "ConsumedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_EnrollmentTokens_TokenHash" ON "EnrollmentTokens" ("TokenHash");
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
