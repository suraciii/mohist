using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubWriteBackFailureStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

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

    private static GitHubWriteBackFailureStore NewStore(TestDatabase database) =>
        new(new TestDbContextFactory(database.Options), new FakeTimeProvider(Now));

    [Fact]
    public async Task CreateThenListRecent_RoundTripsFailure()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        await store.CreateAsync(new GitHubWriteBackFailure
        {
            ProjectId = "project-1",
            ConnectionId = "conn-1",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 7,
            EventType = "com.mohist.workflow.run.failed",
            Operation = GitHubWriteBackOperation.Label,
            ErrorCode = "403",
            ErrorDetail = "Resource not accessible by integration",
        });

        var failures = await store.ListRecentAsync("project-1", limit: 10);

        var failure = Assert.Single(failures);
        Assert.Equal("project-1", failure.ProjectId);
        Assert.Equal("conn-1", failure.ConnectionId);
        Assert.Equal("hello-world", failure.RepositoryName);
        Assert.Equal(42, failure.GithubIssueNumber);
        Assert.Equal(7, failure.IssueNumber);
        Assert.Equal("com.mohist.workflow.run.failed", failure.EventType);
        Assert.Equal(GitHubWriteBackOperation.Label, failure.Operation);
        Assert.Equal("403", failure.ErrorCode);
        Assert.Equal("Resource not accessible by integration", failure.ErrorDetail);
        Assert.Equal(Now, failure.CreatedAt);
    }

    [Fact]
    public async Task ListRecent_ScopesByProjectAndLimits()
    {
        var database = NewDatabase();
        var store = NewStore(database);
        await store.CreateAsync(new GitHubWriteBackFailure
        {
            ProjectId = "project-1",
            ConnectionId = "conn-1",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 7,
            EventType = "com.mohist.issue.completed",
            Operation = GitHubWriteBackOperation.Close,
            ErrorCode = "500",
            ErrorDetail = "boom",
        });
        await store.CreateAsync(new GitHubWriteBackFailure
        {
            ProjectId = "project-1",
            ConnectionId = "conn-1",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 7,
            EventType = "com.mohist.issue.completed",
            Operation = GitHubWriteBackOperation.Comment,
            ErrorCode = "500",
            ErrorDetail = "boom",
        });
        await store.CreateAsync(new GitHubWriteBackFailure
        {
            ProjectId = "project-2",
            ConnectionId = "conn-2",
            RepositoryName = "other",
            GithubIssueNumber = 1,
            IssueNumber = 1,
            EventType = "com.mohist.issue.completed",
            Operation = GitHubWriteBackOperation.Close,
            ErrorCode = "500",
            ErrorDetail = "boom",
        });

        var failures = await store.ListRecentAsync("project-1", limit: 1);

        Assert.Single(failures);
        Assert.All(failures, f => Assert.Equal("project-1", f.ProjectId));
    }
}
