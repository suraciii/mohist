using System.Security.Cryptography;
using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Security;

public class AesGcmSecretStoreSpecs
{
    [Fact]
    public async Task StoreThenLoad_RoundTripsBothKinds()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = NewStore(database, masterKey: NewKey());
        var fixture = ConnectionSecretsFixture.Default;

        await store.StoreAsync(fixture.AppAddress, "xapp-stored"u8.ToArray());
        await store.StoreAsync(fixture.BotAddress, "xoxb-stored"u8.ToArray());

        var app = await store.LoadAsync(fixture.AppAddress);
        var bot = await store.LoadAsync(fixture.BotAddress);
        Assert.NotNull(app);
        Assert.NotNull(bot);
        Assert.Equal("xapp-stored", Encoding.UTF8.GetString(app));
        Assert.Equal("xoxb-stored", Encoding.UTF8.GetString(bot));
    }

    [Fact]
    public async Task StoreAsync_WritesDistinctCtsForBothKinds_AndMarksUpdatedAtFromTimeProvider()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        var store = NewStore(database, masterKey: NewKey(), time: time);
        var fixture = ConnectionSecretsFixture.Default;

        await store.StoreAsync(fixture.AppAddress, "app"u8.ToArray());
        await store.StoreAsync(fixture.BotAddress, "bot"u8.ToArray());

        await using var db = database.CreateContext();
        var rows = db.StoredSecrets.AsNoTracking().ToList();
        Assert.Equal(2, rows.Count);
        var byKind = rows.ToDictionary(r => r.Kind);
        Assert.True(byKind["appToken"].Blob.Length > 0);
        Assert.True(byKind["botToken"].Blob.Length > 0);
        Assert.NotEqual(byKind["appToken"].Blob, byKind["botToken"].Blob);
        Assert.All(rows, r => Assert.Equal(
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            r.UpdatedAt));
    }

    [Fact]
    public async Task LoadAsync_RaisesSecretStoreKeyException_WhenMasterKeyReplacedByAnotherValue()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var first = NewStore(database, masterKey: NewKey());
        var fixture = ConnectionSecretsFixture.Default;
        await first.StoreAsync(fixture.AppAddress, "shared"u8.ToArray());
        await first.StoreAsync(fixture.BotAddress, "shared-bot"u8.ToArray());

        var secondKey = NewKey();
        var rotated = NewStore(database, masterKey: secondKey);

        var error = await Assert.ThrowsAsync<SecretStoreKeyException>(
            () => rotated.LoadAsync(fixture.AppAddress));
        Assert.Contains("master key", error.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<SecretStoreKeyException>(
            () => rotated.LoadAsync(fixture.BotAddress));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullForMissingEntry_AndDoesNotSeeOtherConnectionsSecrets()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = NewStore(database);
        var fixtureA = ConnectionSecretsFixture.With(
            projectId: "proj_a",
            connectionId: "conn_alpha");
        var fixtureB = ConnectionSecretsFixture.With(
            projectId: "proj_a",
            connectionId: "conn_beta");

        await store.StoreAsync(fixtureA.AppAddress, "alpha"u8.ToArray());

        Assert.Null(await store.LoadAsync(fixtureB.AppAddress));
        Assert.Null(await store.LoadAsync(fixtureA.BotAddress));
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntryAndReturnsFalseForMissingDelete()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = NewStore(database);
        var fixture = ConnectionSecretsFixture.Default;
        await store.StoreAsync(fixture.AppAddress, "app"u8.ToArray());

        var firstDelete = await store.DeleteAsync(fixture.AppAddress);
        var secondDelete = await store.DeleteAsync(fixture.AppAddress);

        Assert.True(firstDelete);
        Assert.False(secondDelete);
        Assert.Null(await store.LoadAsync(fixture.AppAddress));

        await using var db = database.CreateContext();
        Assert.Empty(db.StoredSecrets);
    }

    [Fact]
    public async Task DeleteAsync_AffectsOnlyRequestedKind()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = NewStore(database);
        var fixture = ConnectionSecretsFixture.Default;
        await store.StoreAsync(fixture.AppAddress, "app"u8.ToArray());
        await store.StoreAsync(fixture.BotAddress, "bot"u8.ToArray());

        await store.DeleteAsync(fixture.AppAddress);

        Assert.Null(await store.LoadAsync(fixture.AppAddress));
        var bot = await store.LoadAsync(fixture.BotAddress);
        Assert.NotNull(bot);
        Assert.Equal("bot", Encoding.UTF8.GetString(bot));
    }

    [Fact]
    public async Task DatabaseSchema_AppliesConnectionSecretsMigration()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using var db = database.CreateContext();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260729000000_AddConnectionSecrets");
    }

    [Fact]
    public async Task TypedSlackOwnersKeepCredentialSetsIsolated()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var store = NewStore(database);
        var enrollmentKinds = new[]
        {
            SecretKind.ConfigurationAccessToken,
            SecretKind.ConfigurationRefreshToken,
            SecretKind.AppToken,
            SecretKind.BotToken,
            SecretKind.ClientSecret,
            SecretKind.SigningSecret,
        };
        foreach (var kind in enrollmentKinds)
        {
            var address = SecretStoreAddress.ForSlackWorkspaceEnrollment("shared-id", kind);
            var value = $"enrollment-{SecretKinds.ToWire(kind)}";
            await store.StoreAsync(address, Encoding.UTF8.GetBytes(value));

            var loaded = await store.LoadAsync(address);
            Assert.Equal(value, Encoding.UTF8.GetString(loaded!));
        }

        var agentApp = SecretStoreAddress.ForManagedSlackAgentApp("shared-id", SecretKind.BotToken);
        var connection = SecretStoreAddress.ForAgentConnection("proj_a", "shared-id", SecretKind.BotToken);
        await store.StoreAsync(agentApp, "xoxb-agent-app"u8.ToArray());
        await store.StoreAsync(connection, "xoxb-connection"u8.ToArray());

        Assert.Equal("xoxb-agent-app", Encoding.UTF8.GetString((await store.LoadAsync(agentApp))!));
        Assert.Equal("xoxb-connection", Encoding.UTF8.GetString((await store.LoadAsync(connection))!));

        await using var db = database.CreateContext();
        Assert.Equal(
            [
                SecretOwnerKinds.AgentConnection,
                SecretOwnerKinds.ManagedSlackAgentApp,
                SecretOwnerKinds.SlackWorkspaceEnrollment,
            ],
            db.StoredSecrets
                .Select(row => row.OwnerKind)
                .Distinct()
                .OrderBy(ownerKind => ownerKind)
                .ToArray());
        Assert.Throws<ArgumentException>(() =>
            SecretStoreAddress.ForSlackWorkspaceEnrollment("shared-id", SecretKind.WebhookSecret));
        Assert.Throws<ArgumentException>(() =>
            SecretStoreAddress.ForManagedSlackAgentApp("shared-id", SecretKind.ConfigurationAccessToken));
        Assert.Throws<ArgumentException>(() =>
            SecretStoreAddress.ForAgentConnection("proj_a", "shared-id", SecretKind.ClientSecret));
    }

    private static AesGcmSecretStore NewStore(
        TestSqliteDatabase database,
        byte[]? masterKey = null,
        FakeTimeProvider? time = null)
    {
        var keyFile = new InMemorySecretKeyFile(masterKey ?? NewKey());
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new SecretStoreOptions());
        return new AesGcmSecretStore(
            new TestDbContextFactory(database.Options),
            keyFile,
            options,
            EmptyEnvironment(),
            time,
            NullLogger<AesGcmSecretStore>.Instance);
    }

    private static byte[] NewKey()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static IEnvironmentVariableProvider EmptyEnvironment() =>
        new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);

    private sealed record ConnectionSecretsFixture(
        SecretStoreAddress AppAddress,
        SecretStoreAddress BotAddress)
    {
        public static ConnectionSecretsFixture Default => With("proj_a", "conn_1");

        public static ConnectionSecretsFixture With(string projectId, string connectionId) =>
            new(
                new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken),
                new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken));
    }

    private sealed class InMemorySecretKeyFile(byte[] seed) : ISecretKeyFile
    {
        private byte[] _key = seed;
        private int _ensures;

        public bool Exists(string path) => true;

        public Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default)
        {
            _ensures++;
            return Task.FromResult(_key);
        }

        public Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(_key);

        public Task WriteAsync(string path, byte[] key, CancellationToken ct = default)
        {
            _key = key;
            return Task.CompletedTask;
        }
    }
}
