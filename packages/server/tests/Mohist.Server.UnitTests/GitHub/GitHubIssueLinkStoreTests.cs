using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubIssueLinkStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

    private sealed class TestDatabase(SqliteConnection keeper, DbContextOptions<MohistDbContext> options)
    {
        public SqliteConnection Keeper { get; } = keeper;
        public DbContextOptions<MohistDbContext> Options { get; } = options;
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static TestDatabase NewDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var db = new MohistDbContext(options))
        {
            db.Database.EnsureCreated();
        }
        return new TestDatabase(connection, options);
    }

    private static GitHubIssueLinkStore NewStore(TestDatabase database) =>
        new(new TestDbContextFactory(database.Options), new FakeTimeProvider(Now));

    [Fact]
    public async Task CreateThenGet_RoundTripsLink()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        var created = await store.CreateAsync("proj_1", "hello-world", 42, 7);

        Assert.Equal("proj_1", created.ProjectId);
        Assert.Equal("hello-world", created.RepositoryName);
        Assert.Equal(42, created.GithubIssueNumber);
        Assert.Equal(7, created.IssueNumber);
        Assert.False(created.HasPostedComment(GitHubCommentKinds.FeedRejected));

        var loaded = await store.GetAsync("proj_1", "hello-world", 42);
        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(7, loaded.IssueNumber);
    }

    [Fact]
    public async Task Create_DuplicateTriple_ReturnsExistingLinkWithoutSecondRow()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        var first = await store.CreateAsync("proj_1", "hello-world", 42, 7);
        var second = await store.CreateAsync("proj_1", "hello-world", 42, 99);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(7, second.IssueNumber);
        await using var db = new MohistDbContext(database.Options);
        Assert.Equal(1, await db.GitHubIssueLinks.CountAsync());
    }

    [Fact]
    public async Task Create_SameGithubIssueInDifferentProjects_Coexists()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        await store.CreateAsync("proj_1", "hello-world", 42, 7);
        await store.CreateAsync("proj_2", "hello-world", 42, 8);

        await using var db = new MohistDbContext(database.Options);
        Assert.Equal(2, await db.GitHubIssueLinks.CountAsync());
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        Assert.Null(await store.GetAsync("proj_1", "hello-world", 42));
    }

    [Fact]
    public async Task MarkCommentPosted_RecordsKeyOnce()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var link = await store.CreateAsync("proj_1", "hello-world", 42, 7);

        await store.MarkCommentPostedAsync(link.Id, GitHubCommentKinds.FeedRejected);
        var loaded = await store.GetAsync("proj_1", "hello-world", 42);
        Assert.True(loaded!.HasPostedComment(GitHubCommentKinds.FeedRejected));

        await store.MarkCommentPostedAsync(link.Id, GitHubCommentKinds.FeedRejected);
        await using var db = new MohistDbContext(database.Options);
        var row = await db.GitHubIssueLinks.SingleAsync();
        Assert.Contains("\"feed-rejected\"", row.PostedCommentsJson);
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.PostedCommentsJson)!);
    }

    [Fact]
    public async Task Delete_RemovesLink()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var link = await store.CreateAsync("proj_1", "hello-world", 42, 7);

        await store.DeleteAsync(link.Id);

        Assert.Null(await store.GetAsync("proj_1", "hello-world", 42));
    }
}
