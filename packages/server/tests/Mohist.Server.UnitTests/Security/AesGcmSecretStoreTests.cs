using System.Security.Cryptography;
using System.Text;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.Security;

public sealed class AesGcmSecretStoreTests
{
    [Fact]
    public async Task StoreThenLoad_RoundTripsPlaintextForBothKinds()
    {
        var database = NewDatabase();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(database, new InMemoryKeyFile(), time);

        var appAddress = Address("proj_a", "conn_1", SecretKind.AppToken);
        var botAddress = Address("proj_a", "conn_1", SecretKind.BotToken);
        await store.StoreAsync(appAddress, "xapp-1"u8.ToArray());
        await store.StoreAsync(botAddress, "xoxb-1"u8.ToArray());

        var appLoaded = await store.LoadAsync(appAddress);
        var botLoaded = await store.LoadAsync(botAddress);
        Assert.NotNull(appLoaded);
        Assert.NotNull(botLoaded);
        Assert.Equal("xapp-1", Encoding.UTF8.GetString(appLoaded));
        Assert.Equal("xoxb-1", Encoding.UTF8.GetString(botLoaded));
    }

    [Fact]
    public async Task StoreAsync_IsUpsert_RewritesPlaintextOnSecondCall()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var address = Address("proj_a", "conn_1", SecretKind.AppToken);

        await store.StoreAsync(address, "first"u8.ToArray());
        await store.StoreAsync(address, "second"u8.ToArray());

        var loaded = await store.LoadAsync(address);
        Assert.NotNull(loaded);
        Assert.Equal("second", Encoding.UTF8.GetString(loaded));

        await using var db = database.CreateContext();
        Assert.Single(db.ConnectionSecrets);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullForMissingEntry()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        var loaded = await store.LoadAsync(Address("proj_a", "conn_1", SecretKind.AppToken));

        Assert.Null(loaded);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRowAndReturnsTrue()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var address = Address("proj_a", "conn_1", SecretKind.AppToken);
        await store.StoreAsync(address, "value"u8.ToArray());

        var first = await store.DeleteAsync(address);
        var second = await store.DeleteAsync(address);

        Assert.True(first);
        Assert.False(second);

        await using var db = database.CreateContext();
        Assert.Empty(db.ConnectionSecrets);
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherKindsIntact()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        await store.StoreAsync(Address("proj_a", "conn_1", SecretKind.AppToken), "app"u8.ToArray());
        await store.StoreAsync(Address("proj_a", "conn_1", SecretKind.BotToken), "bot"u8.ToArray());

        await store.DeleteAsync(Address("proj_a", "conn_1", SecretKind.AppToken));

        var bot = await store.LoadAsync(Address("proj_a", "conn_1", SecretKind.BotToken));
        Assert.NotNull(bot);
        Assert.Equal("bot", Encoding.UTF8.GetString(bot));

        await using var db = database.CreateContext();
        Assert.Single(db.ConnectionSecrets);
    }

    [Fact]
    public async Task LoadAsync_RaisesSecretStoreKeyException_WhenMasterKeyMismatches()
    {
        var database = NewDatabase();
        var first = NewKey();
        var second = NewKey();
        var store = NewStore(database, new InMemoryKeyFile { CurrentKey = first });
        await store.StoreAsync(Address("proj_a", "conn_1", SecretKind.AppToken), "shared"u8.ToArray());

        var rotated = NewStore(database, new InMemoryKeyFile { CurrentKey = second });

        var error = await Assert.ThrowsAsync<SecretStoreKeyException>(
            () => rotated.LoadAsync(Address("proj_a", "conn_1", SecretKind.AppToken)));
        Assert.Contains("master key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RaisesSecretStoreKeyException_WhenMasterKeyFileMissing()
    {
        var database = NewDatabase();
        var first = NewStore(database, new InMemoryKeyFile { CurrentKey = NewKey() });
        await first.StoreAsync(Address("proj_a", "conn_1", SecretKind.AppToken), "shared"u8.ToArray());

        var unloaded = NewStore(database, new InMemoryKeyFile { Missing = true });

        await Assert.ThrowsAsync<SecretStoreKeyException>(
            () => unloaded.LoadAsync(Address("proj_a", "conn_1", SecretKind.AppToken)));
    }

    [Fact]
    public async Task StoreAsync_ThrowsArgumentException_OnEmptyProjectId()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync(default, "value"u8.ToArray()));
    }

    [Fact]
    public async Task StoreAsync_ThrowsArgumentException_OnEmptyConnectionId()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        var address = new SecretStoreAddress("proj_a", "", SecretKind.AppToken);
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync(address, "value"u8.ToArray()));
    }

    [Fact]
    public void Redact_ReplacesAnySecretNamedKey_AndLeavesOthersAlone()
    {
        var store = NewStore(NewDatabase(), new InMemoryKeyFile());

        var redacted = store.Redact(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appToken"] = "xapp-abc",
            ["BotToken"] = "xoxb-xyz",
            ["clientSecret"] = "shhh",
            ["Name"] = "demo",
            ["ProjectId"] = "proj_a",
        });

        Assert.Equal("***", redacted["appToken"]);
        Assert.Equal("***", redacted["BotToken"]);
        Assert.Equal("***", redacted["clientSecret"]);
        Assert.Equal("demo", redacted["Name"]);
        Assert.Equal("proj_a", redacted["ProjectId"]);
    }

    [Fact]
    public async Task StoreAsync_PersistsExactProjectConnectionIdAndKind()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        await store.StoreAsync(Address("proj_a", "conn_1", SecretKind.AppToken), "app"u8.ToArray());
        await store.StoreAsync(Address("proj_a", "conn_1", SecretKind.BotToken), "bot"u8.ToArray());
        await store.StoreAsync(Address("proj_a", "conn_2", SecretKind.AppToken), "other"u8.ToArray());

        await using var db = database.CreateContext();
        var rows = db.ConnectionSecrets
            .OrderBy(r => r.ProjectId)
            .ThenBy(r => r.ConnectionId)
            .ThenBy(r => r.Kind)
            .ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(
            new[] { ("proj_a", "conn_1", "appToken"), ("proj_a", "conn_1", "botToken"), ("proj_a", "conn_2", "appToken") },
            rows.Select(r => (r.ProjectId, r.ConnectionId, r.Kind)).ToArray());
        Assert.All(rows, r => Assert.NotEmpty(r.Blob));
        Assert.All(rows, r => Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            r.UpdatedAt));
    }

    private static SecretStoreAddress Address(string projectId, string connectionId, SecretKind kind) =>
        new(projectId, connectionId, kind);

    private static AesGcmSecretStore NewStore(
        TestDatabase database,
        InMemoryKeyFile? keyFile = null,
        FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        keyFile ??= new InMemoryKeyFile();
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

    private sealed class TestDatabase
    {
        public TestDatabase(SqliteConnection keeper, DbContextOptions<MohistDbContext> options)
        {
            Keeper = keeper;
            Options = options;
        }

        public SqliteConnection Keeper { get; }
        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateContext() => new(Options);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MohistDbContext(options));
    }

    private static TestDatabase NewDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        SqliteSchemaTemplate.CopyModelSchemaTo(connection);
        return new TestDatabase(connection, options);
    }

    private static IEnvironmentVariableProvider EmptyEnvironment() =>
        new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);

    private sealed class InMemoryKeyFile : ISecretKeyFile
    {
        public byte[]? CurrentKey { get; set; }
        public bool Missing { get; set; }

        public bool Exists(string path) => CurrentKey is not null && !Missing;

        public Task<byte[]> EnsureKeyAsync(string path, CancellationToken ct = default)
        {
            if (Missing)
                throw new SecretStoreKeyException("master key missing");
            return Task.FromResult(CurrentKey ?? Random());
        }

        public Task<byte[]?> TryLoadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(Missing ? null : CurrentKey);

        public Task WriteAsync(string path, byte[] key, CancellationToken ct = default)
        {
            CurrentKey = key;
            Missing = false;
            return Task.CompletedTask;
        }

        private static byte[] Random()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }
    }
}
