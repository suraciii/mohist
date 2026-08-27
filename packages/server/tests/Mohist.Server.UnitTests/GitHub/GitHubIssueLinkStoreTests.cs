using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.UnitTests.Support;
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
        SqliteSchemaTemplate.CopyModelSchemaTo(connection);
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
        Assert.False(created.HasPostedComment(GitHubCommentKinds.CommandReply("comment-1")));

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
    public async Task CreatePending_RoundTripsMarkerAndAttemptState()
    {
        var database = NewDatabase();
        var store = NewStore(database);

        var pending = await store.CreatePendingAsync("proj_1", "hello-world", 7);

        Assert.True(pending.IsPending);
        Assert.False(pending.MirrorCreateAttempted);
        Assert.Equal(GitHubMirrorMarker.For(pending.Id), pending.MirrorMarker);
        Assert.Equal(pending.Id, (await store.GetByIssueAsync("proj_1", 7))!.Id);

        var marked = await store.MarkMirrorCreateAttemptedAsync(pending.Id);
        Assert.True(marked!.MirrorCreateAttempted);
        Assert.True(marked.IsPending);
    }

    [Fact]
    public async Task SyncHealth_RoundTripsErrorAndClearsIt()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var link = await store.CreateAsync("proj_1", "hello-world", 42, 7);
        var error = new GitHubSyncError("content", "503", "GitHub unavailable", Now);

        var failed = await store.MarkErrorAsync(link.Id, error);
        Assert.Equal(GitHubSyncStatus.Error, failed!.SyncStatus);
        Assert.Equal(error, failed.LastError);

        var loaded = await store.GetAsync("proj_1", "hello-world", 42);
        Assert.Equal(error, loaded!.LastError);

        var recovered = await store.ClearErrorAsync(link.Id);
        Assert.Equal(GitHubSyncStatus.Healthy, recovered!.SyncStatus);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task SetMirror_DuplicateGithubIssueLeavesSecondPending()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var first = await store.CreatePendingAsync("proj_1", "hello-world", 7);
        var second = await store.CreatePendingAsync("proj_1", "hello-world", 8);

        await store.SetMirrorAsync(first.Id, 42);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.SetMirrorAsync(second.Id, 42));

        var stillPending = await store.GetByIssueAsync("proj_1", 8);
        Assert.NotNull(stillPending);
        Assert.True(stillPending!.IsPending);
    }

    [Fact]
    public async Task MarkCommentPosted_RecordsKeyOnce()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        var link = await store.CreateAsync("proj_1", "hello-world", 42, 7);

        await store.MarkCommentPostedAsync(link.Id, GitHubCommentKinds.CommandReply("comment-1"));
        var loaded = await store.GetAsync("proj_1", "hello-world", 42);
        Assert.True(loaded!.HasPostedComment(GitHubCommentKinds.CommandReply("comment-1")));

        await store.MarkCommentPostedAsync(link.Id, GitHubCommentKinds.CommandReply("comment-1"));
        await using var db = new MohistDbContext(database.Options);
        var row = await db.GitHubIssueLinks.SingleAsync();
        Assert.Contains("\"command-reply:comment-1\"", row.PostedCommentsJson);
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
