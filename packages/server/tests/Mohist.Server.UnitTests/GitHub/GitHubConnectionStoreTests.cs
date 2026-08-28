using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubConnectionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);

    private sealed class TestDatabase(SqliteConnection keeper, DbContextOptions<MohistDbContext> options)
    {
        public SqliteConnection Keeper { get; } = keeper;
        public DbContextOptions<MohistDbContext> Options { get; } = options;
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _secrets = [];
        public Func<SecretStoreAddress, Exception?>? StoreFailure { get; set; }
        public Func<SecretStoreAddress, Exception?>? LoadFailure { get; set; }

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            var failure = StoreFailure?.Invoke(address);
            if (failure is null) _secrets[address] = plaintext;
            if (failure is not null) throw failure;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default)
        {
            var failure = LoadFailure?.Invoke(address);
            if (failure is not null) throw failure;
            return Task.FromResult(_secrets.TryGetValue(address, out var value) ? value : null);
        }
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(_secrets.Remove(address));
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static TestDatabase NewDatabase(string repositoriesJson)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(connection).Options;
        SqliteSchemaTemplate.CopyModelSchemaTo(connection);
        using var db = new MohistDbContext(options);
        db.Projects.Add(new ProjectRow { Id = "proj_1", Name = "demo", RepositoriesJson = repositoriesJson });
        db.SaveChanges();
        return new TestDatabase(connection, options);
    }

    private static GitHubConnectionStore NewStore(TestDatabase database, FakeSecretStore secrets) =>
        new(new TestDbContextFactory(database.Options), secrets, new GitHubConnectionGate(), new FakeTimeProvider(Now));

    private static string RepositoriesJson(params string[] gitUrls) =>
        JSON.Serialize(gitUrls.Select(url => new RepositoryInfo { Name = url.Split('/').Last().Replace(".git", ""), GitUrl = url }).ToList());

    private static GitHubConnection Connection(string owner = "octocat", string repo = "hello-world") => new()
    {
        Id = $"ghconn_{Guid.NewGuid():N}", ProjectId = "proj_1", Owner = owner, Repo = repo,
    };

    private static GitHubRepositoryInstallation Installation(GitHubConnection connection) =>
        new("installation-1", connection.Owner, connection.Repo, "repository-node-1");

    [Fact]
    public async Task CreateAsync_MatchesRegisteredRepositoryAndStoresWebhookSecret()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        var secret = await store.CreateAsync(connection, Installation(connection));
        Assert.Equal("hello-world", connection.RepositoryName);
        Assert.Equal(GitHubConnectionStatus.Active, connection.Status);
        Assert.Equal("installation-1", connection.InstallationId);
        Assert.Equal("repository-node-1", connection.RepositoryNodeId);
        Assert.Equal(64, secret.Length);
        Assert.NotNull(await store.LoadWebhookSecretAsync("proj_1", connection.Id));
    }

    [Fact]
    public async Task CreateAsync_RejectsRepositoryNotRegisteredInProject()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection(repo: "other");
        var ex = await Assert.ThrowsAsync<GitHubConnectionValidationException>(() => store.CreateAsync(connection, Installation(connection)));
        Assert.Equal("repository_not_registered", ex.Code);
        Assert.Empty(await store.ListAsync("proj_1"));
    }

    [Fact]
    public async Task CreateAsync_ReconnectsExistingBindingAndPreservesWebhookSecret()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var secrets = new FakeSecretStore();
        var store = NewStore(database, secrets);
        var connection = Connection();
        var secret = await store.CreateAsync(connection, Installation(connection));
        await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Disabled);
        await using (var db = new MohistDbContext(database.Options))
        {
            var row = await db.GitHubConnections.SingleAsync();
            row.ReconnectRequired = true;
            row.InstallationId = null;
            row.RepositoryNodeId = null;
            await db.SaveChangesAsync();
        }
        var reconnect = Connection();
        var recoveredSecret = await store.CreateAsync(reconnect, Installation(reconnect));
        Assert.Equal(connection.Id, reconnect.Id);
        Assert.Equal(secret, recoveredSecret);
        Assert.True(reconnect.NeedsReprojection);
        var current = await store.GetAsync("proj_1", connection.Id);
        Assert.False(current!.ReconnectRequired);
        Assert.Equal(GitHubConnectionStatus.Active, current.Status);
    }

    [Fact]
    public async Task CreateAsync_ReconnectMissingWebhookSecretLeavesConnectionDisabled()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var secrets = new FakeSecretStore();
        var store = NewStore(database, secrets);
        var connection = Connection();
        await store.CreateAsync(connection, Installation(connection));
        await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Disabled);
        await secrets.DeleteAsync(GitHubConnectionStore.WebhookSecretAddress("proj_1", connection.Id));

        var exception = await Assert.ThrowsAsync<GitHubConnectionValidationException>(() =>
            store.CreateAsync(Connection(), Installation(connection)));

        Assert.Equal("github_webhook_secret_missing", exception.Code);
        var current = await store.GetAsync("proj_1", connection.Id);
        Assert.Equal(GitHubConnectionStatus.Disabled, current!.Status);
        Assert.Equal("installation-1", current.InstallationId);
    }

    [Fact]
    public async Task CreateAsync_ReconnectSecretStoreFailureLeavesConnectionDisabled()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var secrets = new FakeSecretStore();
        var store = NewStore(database, secrets);
        var connection = Connection();
        await store.CreateAsync(connection, Installation(connection));
        await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Disabled);
        await using (var db = new MohistDbContext(database.Options))
        {
            var row = await db.GitHubConnections.SingleAsync();
            row.ReconnectRequired = true;
            await db.SaveChangesAsync();
        }
        secrets.LoadFailure = _ => new InvalidOperationException("secret store unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(Connection(), Installation(connection)));

        var current = await store.GetAsync("proj_1", connection.Id);
        Assert.Equal(GitHubConnectionStatus.Disabled, current!.Status);
        Assert.True(current.ReconnectRequired);
    }

    [Fact]
    public async Task CreateAsync_SameRepositoryAndNodeIsIdempotent()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var first = Connection();
        var secret = await store.CreateAsync(first, Installation(first));
        var second = Connection();
        var sameSecret = await store.CreateAsync(second, Installation(second));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(secret, sameSecret);
    }

    [Fact]
    public async Task CreateAsync_NormalizesOwnerRepoAndApprovers()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection("OCTOCAT", "Hello-World");
        connection.Approvers = [" alice ", "Alice", "", "bob"];
        await store.CreateAsync(connection, Installation(connection) with { Owner = "OCTOCAT", Repo = "Hello-World" });
        Assert.Equal("octocat", connection.Owner);
        Assert.Equal("hello-world", connection.Repo);
        Assert.Equal(["alice", "bob"], connection.Approvers);
    }

    [Fact]
    public async Task SetStatusAsync_RequiresVerifiedInstallationToEnable()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        await store.CreateAsync(connection, Installation(connection));
        await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Disabled);
        var enabled = await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Active);
        Assert.Equal(GitHubConnectionStatus.Active, enabled!.Status);
    }

    [Fact]
    public async Task SetStatusAsync_RejectsReconnectRequiredConnection()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        await store.CreateAsync(connection, Installation(connection));
        await using (var db = new MohistDbContext(database.Options))
        {
            var row = await db.GitHubConnections.SingleAsync();
            row.Status = GitHubConnectionStatus.Disabled;
            row.ReconnectRequired = true;
            await db.SaveChangesAsync();
        }
        var ex = await Assert.ThrowsAsync<GitHubConnectionValidationException>(() =>
            store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Active));
        Assert.Equal("github_app_reconnect_required", ex.Code);
    }

    [Fact]
    public async Task UpdateApproversAsync_ReplacesList()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        await store.CreateAsync(connection, Installation(connection));
        var updated = await store.UpdateApproversAsync("proj_1", connection.Id, [" alice ", "Alice", "bob"]);
        Assert.Equal(["alice", "bob"], updated!.Approvers);
    }
}
