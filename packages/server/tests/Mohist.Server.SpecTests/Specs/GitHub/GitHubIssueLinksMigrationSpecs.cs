using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

public class GitHubIssueLinksMigrationSpecs
{
    private const string MigrationId = "20260817000000_AddGitHubWriteBack";

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, MigrationId);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, new TestDbContextFactory(options));
    }

    [Fact]
    public async Task Up_CreatesGitHubIssueLinksTableAndRecordsMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        Assert.False(await context.GitHubIssueLinks.AnyAsync());
        await using var command = database.Keeper.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync())
            applied.Add(reader.GetString(0));
        Assert.Contains(MigrationId, applied);
    }

    [Fact]
    public async Task Up_UniqueTripleRejectsSecondFeedForSameGithubIssue()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        context.GitHubIssueLinks.Add(new GitHubIssueLinkRow
        {
            Id = "ghlink_first",
            ProjectId = "project-1",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 1,
            PostedCommentsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.SaveChangesAsync();

        context.GitHubIssueLinks.Add(new GitHubIssueLinkRow
        {
            Id = "ghlink_second",
            ProjectId = "project-1",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 2,
            PostedCommentsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        await context.DisposeAsync();

        await using var second = database.CreateDbContext();
        second.GitHubIssueLinks.Add(new GitHubIssueLinkRow
        {
            Id = "ghlink_other_project",
            ProjectId = "project-2",
            RepositoryName = "hello-world",
            GithubIssueNumber = 42,
            IssueNumber = 3,
            PostedCommentsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await second.SaveChangesAsync();
        Assert.Equal(2, await second.GitHubIssueLinks.CountAsync());
    }

    private sealed class TestDatabase(SqliteConnection keeper, TestDbContextFactory factory)
        : IAsyncDisposable
    {
        public SqliteConnection Keeper { get; } = keeper;
        public TestDbContextFactory Factory { get; } = factory;

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Keeper.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>, IAsyncDisposable
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
