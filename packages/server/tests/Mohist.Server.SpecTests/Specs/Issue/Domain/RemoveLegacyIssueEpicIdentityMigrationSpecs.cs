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

                INSERT INTO "IssueEvents" ("Source", "Id", "EventId", "Type", "SpecVersion", "DataContentType", "Data", "ExtensionsJson", "Time", "DispatchedAt")
                VALUES ('/mohist/issues/issue_alpha_42', 1, 'issue_pending', 'com.mohist.issue.work-started', '1.0', 'application/json', '{{}}', '{{"issueid":"issue_alpha_42","custom":"preserve"}}', '2026-07-17 00:00:00+00:00', NULL),
                       ('/mohist/issues/issue_alpha_42', 2, 'issue_dispatched', 'com.mohist.issue.work-started', '1.0', 'application/json', '{{}}', '{{"issueid":"issue_alpha_42"}}', '2026-07-17 00:00:00+00:00', '2026-07-17 00:01:00+00:00');

                INSERT INTO "WorkflowRunEvents" ("Source", "Id", "EventId", "Type", "SpecVersion", "DataContentType", "Data", "ExtensionsJson", "Time", "DispatchedAt")
                VALUES ('/mohist/workflow-runs/run_alpha', 1, 'workflow_pending', 'com.mohist.workflow.run.completed', '1.0', 'application/json', '{{}}', '{{"custom":"preserve"}}', '2026-07-17 00:00:00+00:00', NULL);
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

        // Read State via raw SQL rather than through IssueRow: the
        // RepositoryName projection column is added by a later migration
        // (AddIssueRepositoryProjection), so the IssueRow mapping is not
        // usable at this migration point.
        var issueStatesByProject = await ReadStringsAsync(
            verify,
            "SELECT \"State\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\"");
        Assert.All(issueStatesByProject, state =>
            Assert.Equal(7, IssueStore.Deserialize(state)!.EpicNumber));

        Assert.Equal(
            ["/mohist/issues/issue_alpha_42:/mohist/projects/proj_alpha/issues/42"],
            await ReadStringsAsync(verify, "SELECT \"Source\" || ':' || \"TimelineSource\" AS \"Value\" FROM \"IssueEvents\" WHERE \"EventId\" = 'issue_pending'"));
        Assert.Equal(
            ["proj_alpha:42:preserve"],
            await ReadStringsAsync(verify, "SELECT json_extract(\"ExtensionsJson\", '$.projectid') || ':' || json_extract(\"ExtensionsJson\", '$.issue') || ':' || json_extract(\"ExtensionsJson\", '$.custom') AS \"Value\" FROM \"IssueEvents\" WHERE \"EventId\" = 'issue_pending'"));
        Assert.Equal(
            ["issue_alpha_42"],
            await ReadStringsAsync(verify, "SELECT json_extract(\"ExtensionsJson\", '$.issueid') AS \"Value\" FROM \"IssueEvents\" WHERE \"EventId\" = 'issue_dispatched'"));
        Assert.Equal(
            ["proj_alpha:42:run_alpha:preserve"],
            await ReadStringsAsync(verify, "SELECT json_extract(\"ExtensionsJson\", '$.projectid') || ':' || json_extract(\"ExtensionsJson\", '$.issue') || ':' || json_extract(\"ExtensionsJson\", '$.workflowrunid') || ':' || json_extract(\"ExtensionsJson\", '$.custom') AS \"Value\" FROM \"WorkflowRunEvents\" WHERE \"EventId\" = 'workflow_pending'"));

        var runStates = await ReadStringsAsync(verify, "SELECT \"State\" AS \"Value\" FROM \"WorkflowRuns\" ORDER BY \"WorkflowRunId\"");
        Assert.All(runStates, state =>
        {
            Assert.DoesNotContain("issueId", state, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("epicId", state, StringComparison.OrdinalIgnoreCase);
        });

        await verify.GetService<IMigrator>().MigrateAsync(TargetMigration);
        Assert.Equal(2, await ReadIntAsync(verify, "SELECT COUNT(*) AS \"Value\" FROM \"Issues\""));
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

    private static async Task<int> ReadIntAsync(MohistDbContext context, string sql) =>
        await context.Database.SqlQueryRaw<int>(sql).FirstAsync();

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
