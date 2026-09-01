using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Data.Migrations;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

public sealed class GitHubMirrorMigrationTests
{
    [Fact]
    public async Task MirrorIntentMigration_IsDiscoverableAndUpgradesModelAndIndexesOnce()
    {
        var migrationType = typeof(AddGitHubMirrorIntent);
        var migration = Assert.Single(migrationType.GetCustomAttributes<MigrationAttribute>());
        Assert.Equal("20260915000000_AddGitHubMirrorIntent", migration.Id);
        Assert.Single(migrationType.GetCustomAttributes<DbContextAttribute>());
        var recoveryMigration = Assert.Single(
            typeof(AddGitHubOperationRecovery).GetCustomAttributes<MigrationAttribute>());
        Assert.Equal("20260918000000_AddGitHubOperationRecovery", recoveryMigration.Id);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using (var db = new MohistDbContext(options))
        {
            Assert.Contains("20260915000000_AddGitHubMirrorIntent", db.Database.GetMigrations());
            await db.Database.MigrateAsync(SquashedMigrationHistory.BaselineId);
            await ExecuteAsync(connection, """
                CREATE INDEX "IX_GitHubIssueLinks_ProjectId_IssueNumber"
                    ON "GitHubIssueLinks" ("ProjectId", "IssueNumber");
                CREATE INDEX "IX_GitHubConnections_ProjectId_RepositoryName"
                    ON "GitHubConnections" ("ProjectId", "RepositoryName");
                """);
            await db.Database.MigrateAsync();

            var links = db.Model.FindEntityType(typeof(GitHubIssueLinkRow));
            Assert.NotNull(links);
            var issueIndex = Assert.Single(links!.GetIndexes(), index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(GitHubIssueLinkRow.ProjectId), nameof(GitHubIssueLinkRow.IssueNumber)]));
            Assert.True(issueIndex.IsUnique);

            var mirrorIndex = Assert.Single(links.GetIndexes(), index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(GitHubIssueLinkRow.ProjectId),
                        nameof(GitHubIssueLinkRow.RepositoryName),
                        nameof(GitHubIssueLinkRow.GithubIssueNumber)]));
            Assert.True(mirrorIndex.IsUnique);
            Assert.Equal("\"GithubIssueNumber\" > 0", mirrorIndex.GetFilter());

            var connections = db.Model.FindEntityType(typeof(GitHubConnectionRow));
            Assert.NotNull(connections);
            var repositoryIndex = Assert.Single(connections!.GetIndexes(), index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(GitHubConnectionRow.ProjectId), nameof(GitHubConnectionRow.RepositoryName)]));
            Assert.False(repositoryIndex.IsUnique);

            var commentOperations = db.Model.FindEntityType(typeof(GitHubIssueCommentOperationRow));
            Assert.NotNull(commentOperations);
            Assert.True(Assert.Single(commentOperations!.GetIndexes(), index =>
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(GitHubIssueCommentOperationRow.LinkId), nameof(GitHubIssueCommentOperationRow.CommentKey)])).IsUnique);
        }

        var connectionColumns = await ReadColumnsAsync(connection, "GitHubConnections");
        Assert.DoesNotContain("FeedMode", connectionColumns);
        Assert.DoesNotContain("IntakeLabel", connectionColumns);

        var linkColumns = await ReadColumnsAsync(connection, "GitHubIssueLinks");
        Assert.Contains("MirrorMarker", linkColumns);
        Assert.Contains("MirrorCreateAttempted", linkColumns);
        Assert.Contains("SyncStatus", linkColumns);
        Assert.Contains("LastErrorOperation", linkColumns);
        Assert.Contains("LastErrorCode", linkColumns);
        Assert.Contains("LastErrorDetail", linkColumns);
        Assert.Contains("LastErrorAt", linkColumns);

        var linkIndexes = await ReadIndexesAsync(connection, "GitHubIssueLinks");
        Assert.Equal(1, linkIndexes.Count(index =>
            index.Name == "IX_GitHubIssueLinks_ProjectId_IssueNumber"));
        Assert.Equal(1, linkIndexes.Count(index =>
            index.Name == "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber"));
        Assert.True(linkIndexes.Single(index =>
            index.Name == "IX_GitHubIssueLinks_ProjectId_IssueNumber").Unique);
        Assert.True(linkIndexes.Single(index =>
            index.Name == "IX_GitHubIssueLinks_ProjectId_RepositoryName_GithubIssueNumber").Unique);

        var commentOperationIndexes = await ReadIndexesAsync(connection, "GitHubIssueCommentOperations");
        Assert.True(commentOperationIndexes.Single(index =>
            index.Name == "IX_GitHubIssueCommentOperations_LinkId_CommentKey").Unique);

        Assert.Contains("NeedsReprojection", await ReadColumnsAsync(connection, "GitHubConnections"));
        var operationColumns = await ReadColumnsAsync(connection, "GitHubIssueCommentOperations");
        Assert.Contains("Kind", operationColumns);
        Assert.Contains("Body", operationColumns);
        Assert.Contains("StateReason", operationColumns);
        Assert.Contains("Marker", operationColumns);
        Assert.Contains("AttemptCount", operationColumns);
        Assert.Contains("NextAttemptAt", operationColumns);
        Assert.Contains("LeaseUntil", operationColumns);
        Assert.Contains("LastError", operationColumns);
        Assert.Contains("FailedAt", operationColumns);
        Assert.Contains("GithubIssueNumber", operationColumns);

        var connectionIndexes = await ReadIndexesAsync(connection, "GitHubConnections");
        Assert.Equal(1, connectionIndexes.Count(index =>
            index.Name == "IX_GitHubConnections_ProjectId_RepositoryName"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlySet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<IReadOnlyList<SqliteIndex>> ReadIndexesAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\");";
        var indexes = new List<SqliteIndex>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(new SqliteIndex(
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(4) != 0));
        }
        return indexes;
    }

    private sealed record SqliteIndex(string Name, bool Unique, bool Partial);
}
