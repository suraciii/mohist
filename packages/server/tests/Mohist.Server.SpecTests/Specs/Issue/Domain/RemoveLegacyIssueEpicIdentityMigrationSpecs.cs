using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public sealed class RemoveLegacyIssueEpicIdentityMigrationSpecs
{
    private const string PreviousMigration = "20260716180000_RemoveWorkflowBindingState";
    private const string TargetMigration = "20260716190000_RemoveLegacyIssueEpicIdentity";

    [Fact]
    public async Task Migration_DropsLegacyIdentityColumns_CleansCurrentState_AndPreservesProjectScope()
    {
        await using var database = CreateDatabase(PreviousMigration);
        await using (var seed = database.CreateDbContext())
        {
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Epics" ("ProjectId", "Number", "Title", "Description", "Priority", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('proj_alpha', 7, 'Alpha epic', '', 'p2', 'idle', '2026-07-17 00:00:00+00:00', '2026-07-17 00:00:00+00:00'),
                       ('proj_beta', 7, 'Beta epic', '', 'p2', 'idle', '2026-07-17 00:00:00+00:00', '2026-07-17 00:00:00+00:00');

                INSERT INTO "Issues" ("IssueId", "State", "EpicId", "EpicNumber")
                VALUES ('issue_alpha_42', '{{"id":"issue_alpha_42","projectId":"proj_alpha","number":42,"title":"Alpha issue","status":"backlog","priority":"p2","epicId":"epic_alpha_7"}}', 'epic_alpha_7', 7),
                       ('issue_beta_42', '{{"Id":"issue_beta_42","ProjectId":"proj_beta","Number":42,"Title":"Beta issue","Status":"backlog","Priority":"p2","EpicId":"epic_beta_7"}}', 'epic_beta_7', 7);

                INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicId", "EpicNumber", "ETag")
                VALUES ('run_alpha', '{{"id":"run_alpha","metadata":{{"annotations":{{"projectId":"proj_alpha","issueNumber":"42","issueId":"issue_alpha_42","epicNumber":"7","epicId":"epic_alpha_7"}}}}}}', 'epic_alpha_7', 7, 1),
                       ('run_beta', '{{"Id":"run_beta","Metadata":{{"Annotations":{{"ProjectId":"proj_beta","IssueNumber":"42","IssueId":"issue_beta_42","EpicNumber":"7","EpicId":"epic_beta_7"}}}}}}', 'epic_beta_7', 7, 1);
                """);

            await seed.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using var verify = database.CreateDbContext();
        Assert.Equal(
            ["proj_alpha:42:7", "proj_beta:42:7"],
            await ReadStringsAsync(verify, "SELECT \"ProjectId\" || ':' || \"Number\" || ':' || \"EpicNumber\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\""));
        Assert.Equal(
            ["proj_alpha:7:42", "proj_beta:7:42"],
            await ReadStringsAsync(verify, "SELECT \"MetadataProjectId\" || ':' || \"EpicNumber\" || ':' || \"IssueNumber\" AS \"Value\" FROM \"WorkflowRuns\" ORDER BY \"MetadataProjectId\""));

        Assert.DoesNotContain("IssueId", await ReadColumnsAsync(verify, "Issues"));
        Assert.DoesNotContain("EpicId", await ReadColumnsAsync(verify, "Issues"));
        Assert.DoesNotContain("EpicId", await ReadColumnsAsync(verify, "WorkflowRuns"));

        var issueStates = await ReadStringsAsync(verify, "SELECT \"State\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\"");
        Assert.All(issueStates, state =>
        {
            Assert.DoesNotContain("\"id\"", state, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"epicId\"", state, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"epicNumber\":7", state, StringComparison.Ordinal);
            Assert.DoesNotContain("\"EpicNumber\"", state, StringComparison.Ordinal);
        });

        var issues = await verify.Issues.AsNoTracking().OrderBy(row => row.ProjectId).ToListAsync();
        Assert.All(issues, row => Assert.Equal(7, IssueStore.Deserialize(row.State)!.EpicNumber));

        var runStates = await ReadStringsAsync(verify, "SELECT \"State\" AS \"Value\" FROM \"WorkflowRuns\" ORDER BY \"WorkflowRunId\"");
        Assert.All(runStates, state =>
        {
            Assert.DoesNotContain("issueId", state, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("epicId", state, StringComparison.OrdinalIgnoreCase);
        });

        await verify.GetService<IMigrator>().MigrateAsync(TargetMigration);
        Assert.Equal(2, await verify.Issues.CountAsync());
        Assert.Equal(2, await verify.WorkflowRuns.CountAsync());
    }

    private static async Task<string[]> ReadColumnsAsync(MohistDbContext context, string tableName)
    {
        var sql = tableName switch
        {
            "Issues" => "SELECT \"name\" AS \"Value\" FROM pragma_table_info('Issues') ORDER BY \"name\"",
            "WorkflowRuns" => "SELECT \"name\" AS \"Value\" FROM pragma_table_info('WorkflowRuns') ORDER BY \"name\"",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        return await context.Database.SqlQueryRaw<string>(sql).ToArrayAsync();
    }

    private static async Task<string[]> ReadStringsAsync(MohistDbContext context, string sql) =>
        await context.Database.SqlQueryRaw<string>(sql).ToArrayAsync();

    private static TestDatabase CreateDatabase(string migration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, migration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options) : IAsyncDisposable
    {
        public MohistDbContext CreateDbContext() => new(options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
