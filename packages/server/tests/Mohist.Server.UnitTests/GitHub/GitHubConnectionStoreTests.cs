using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure;
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

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _secrets[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.TryGetValue(address, out var secret) ? secret : null);

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static TestDatabase NewDatabase(string repositoriesJson)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var db = new MohistDbContext(options))
        {
            db.Database.EnsureCreated();
            db.Projects.Add(new ProjectRow { Id = "proj_1", Name = "demo", RepositoriesJson = repositoriesJson });
            db.SaveChanges();
        }
        return new TestDatabase(connection, options);
    }

    private static GitHubConnectionStore NewStore(TestDatabase database, FakeSecretStore secrets) =>
        new(
            new TestDbContextFactory(database.Options),
            secrets,
            new FakeTimeProvider(Now));

    private static string RepositoriesJson(params string[] gitUrls) =>
        JSON.Serialize(gitUrls.Select(url => new RepositoryInfo { Name = url.Split('/').Last().Replace(".git", ""), GitUrl = url }).ToList());

    private static GitHubConnection Connection(string owner = "octocat", string repo = "hello-world") => new()
    {
        Id = $"ghconn_{Guid.NewGuid():N}",
        ProjectId = "proj_1",
        Owner = owner,
        Repo = repo,
    };

    [Fact]
    public async Task CreateAsync_MatchesRegisteredRepositoryAndStoresWebhookSecret()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var secrets = new FakeSecretStore();
        var store = NewStore(database, secrets);
        var connection = Connection();

        var secret = await store.CreateAsync(connection);

        Assert.Equal("hello-world", connection.RepositoryName);
        Assert.Equal(GitHubConnectionStatus.Active, connection.Status);
        Assert.Equal(64, secret.Length);
        var stored = await store.LoadWebhookSecretAsync("proj_1", connection.Id);
        Assert.NotNull(stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public async Task CreateAsync_RejectsRepositoryNotRegisteredInProject()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());

        var ex = await Assert.ThrowsAsync<GitHubConnectionValidationException>(
            () => store.CreateAsync(Connection(repo: "other")));

        Assert.Equal("repository_not_registered", ex.Code);
        Assert.Contains("register the repository first", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_MatchesRegisteredRepositoryWithDifferentGitUrlCase()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/OCTOCAT/Hello-World.git"));
        var store = NewStore(database, new FakeSecretStore());

        var connection = Connection();
        await store.CreateAsync(connection);

        Assert.Equal("Hello-World", connection.RepositoryName);
    }

    [Fact]
    public async Task CreateAsync_DuplicateOwnerRepo_ConflictsAcrossConnections()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());

        await store.CreateAsync(Connection());
        var ex = await Assert.ThrowsAsync<GitHubConnectionConflictException>(
            () => store.CreateAsync(Connection()));

        Assert.Equal("github_repository_already_connected", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_NormalizesOwnerAndRepoToLowercase()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());

        var connection = Connection(owner: "OCTOCAT", repo: "Hello-World");
        await store.CreateAsync(connection);

        Assert.Equal("octocat", connection.Owner);
        Assert.Equal("hello-world", connection.Repo);
    }

    [Fact]
    public async Task SetStatusAsync_DisablesAndEnables()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        await store.CreateAsync(connection);

        var disabled = await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Disabled);
        Assert.Equal(GitHubConnectionStatus.Disabled, disabled!.Status);
        Assert.NotNull(await store.GetAsync("proj_1", connection.Id));
        Assert.Equal(GitHubConnectionStatus.Disabled, (await store.GetByIdAsync(connection.Id))!.Status);

        var enabled = await store.SetStatusAsync("proj_1", connection.Id, GitHubConnectionStatus.Active);
        Assert.Equal(GitHubConnectionStatus.Active, enabled!.Status);
    }

    [Fact]
    public async Task UpdateApproversAsync_ReplacesList_TrimsAndDeduplicates()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        connection.Approvers = ["old-approver"];
        await store.CreateAsync(connection);

        var updated = await store.UpdateApproversAsync("proj_1", connection.Id, [" alice ", "Alice", "", "bob"]);

        Assert.NotNull(updated);
        Assert.Equal(["alice", "bob"], updated!.Approvers);
        Assert.Equal(["alice", "bob"], (await store.GetAsync("proj_1", connection.Id))!.Approvers);
    }

    [Fact]
    public async Task UpdateApproversAsync_EmptyList_ClearsApprovers()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());
        var connection = Connection();
        connection.Approvers = ["alice"];
        await store.CreateAsync(connection);

        var updated = await store.UpdateApproversAsync("proj_1", connection.Id, []);

        Assert.NotNull(updated);
        Assert.Empty(updated!.Approvers);
    }

    [Fact]
    public async Task UpdateApproversAsync_UnknownConnection_ReturnsNull()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var store = NewStore(database, new FakeSecretStore());

        var updated = await store.UpdateApproversAsync("proj_1", "ghconn_missing", ["alice"]);

        Assert.Null(updated);
    }

    [Fact]
    public async Task UpdateApproversAsync_StampsUpdatedAt()
    {
        var database = NewDatabase(RepositoriesJson("https://github.com/octocat/hello-world.git"));
        var timeProvider = new FakeTimeProvider(Now);
        var store = new GitHubConnectionStore(
            new TestDbContextFactory(database.Options),
            new FakeSecretStore(),
            timeProvider);
        var connection = Connection();
        await store.CreateAsync(connection);
        var before = (await store.GetAsync("proj_1", connection.Id))!.UpdatedAt;
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        var updated = await store.UpdateApproversAsync("proj_1", connection.Id, ["alice"]);

        Assert.Equal(Now.AddMinutes(5), updated!.UpdatedAt);
        Assert.True(updated.UpdatedAt > before);
    }
}
