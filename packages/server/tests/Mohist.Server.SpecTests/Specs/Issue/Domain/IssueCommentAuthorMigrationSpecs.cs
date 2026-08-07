using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class IssueCommentAuthorMigrationSpecs
{
    private const string PreviousMigrationId = "20260720120000_RemoveIssueWorkflowProfilePrompts";
    private const string MigrationId = "20260721161000_AddIssueCommentAuthor";

    [Fact]
    public async Task Migration_AddsNullableAuthorWithoutBackfillAndDownRemovesIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        MigratedSqliteTemplate.CopyTo(connection, PreviousMigrationId);
        await InsertHistoricalCommentAsync(connection);

        await using var context = CreateDbContext(connection);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationId);

        Assert.True(await ColumnExistsAsync(connection));
        Assert.Null(await ReadAuthorAsync(connection));

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        new TestMigration().BuildDown(builder);
        Assert.Contains(builder.Operations, operation => operation is DropColumnOperation
        {
            Name: "Author",
            Table: "IssueComments",
        });
    }

    private static MohistDbContext CreateDbContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    private static async Task InsertHistoricalCommentAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IssueComments (Id, ProjectId, IssueNumber, Body, CreatedAt)
            VALUES ('cmt_historical', 'proj_history', 42, 'Before attribution', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('IssueComments') WHERE name = 'Author' LIMIT 1;";
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<object?> ReadAuthorAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Author FROM IssueComments WHERE Id = 'cmt_historical';";
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private sealed class TestMigration : AddIssueCommentAuthor
    {
        public void BuildDown(MigrationBuilder builder) => Down(builder);
    }
}
