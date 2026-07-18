using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using IssueStatus = Mohist.Server.Issue.Domain.IssueStatus;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class DropEpicMembershipTablesMigrationSpecs
{
    private const string PreviousMigration = "20260716160000_BackfillCanonicalEpicReferences";
    private const string TargetMigration = "20260716170000_DropEpicMembershipTables";

    [Fact]
    public async Task Migration_DropsLegacyMembershipTables_PreservingAffiliationDerivedFromMembership()
    {
        await using var database = CreateDatabase(PreviousMigration);
        await using (var seed = database.CreateDbContext())
        {
            var issue = new DomainIssue
            {
                ProjectId = "project_1",
                Number = 42,
                Title = "Issue 42",
                Status = IssueStatus.Backlog,
                Priority = "p2",
                EpicNumber = 7,
            };
            await InsertLegacyIssueAsync(seed, IssueStore.Serialize(issue), 7);
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "EpicIssues" ("EpicId", "IssueId", "ProjectId", "IssueNumber", "EpicNumber", "CreatedAt")
                VALUES ('epic_7', 'issue_42', 'project_1', 42, 7, '2026-07-17 00:00:00+00:00');
                """);
            Assert.True(await TableExistsAsync(seed, "EpicIssues"));
            Assert.True(await TableExistsAsync(seed, "EpicActiveIssues"));

            await seed.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "EpicIssues"));
        Assert.False(await TableExistsAsync(verify, "EpicActiveIssues"));
        Assert.Contains(TargetMigration, await verify.Database.GetAppliedMigrationsAsync());

        // Read via raw SQL: the RepositoryName projection column is added by
        // a later migration (AddIssueRepositoryProjection), so IssueRow's
        // mapping is not usable at this migration point.
        var rowState = Assert.Single(await ReadStringsAsync(
            verify,
            "SELECT \"State\" AS \"Value\" FROM \"Issues\""));
        var materialized = IssueStore.Deserialize(rowState);
        Assert.NotNull(materialized);
        Assert.Equal(7, materialized!.EpicNumber);
    }

    [Fact]
    public async Task Migration_ClearsStaleIssueAffiliationWhenNoLegacyMembershipExists()
    {
        await using var database = CreateDatabase(PreviousMigration);
        await using (var seed = database.CreateDbContext())
        {
            var issue = new DomainIssue
            {
                ProjectId = "project_1",
                Number = 42,
                Title = "Stale Issue",
                Status = IssueStatus.Backlog,
                Priority = "p2",
                EpicNumber = 7,
            };
            await InsertLegacyIssueAsync(seed, IssueStore.Serialize(issue), 7);

            await seed.GetService<IMigrator>().MigrateAsync("20260716190000_RemoveLegacyIssueEpicIdentity");
        }

        await using var verify = database.CreateDbContext();
        var rowState = Assert.Single(await ReadStringsAsync(
            verify,
            "SELECT \"State\" AS \"Value\" FROM \"Issues\""));
        Assert.Null(IssueStore.Deserialize(rowState)!.EpicNumber);
        Assert.DoesNotContain("epicNumber", rowState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migration_MovesActiveAndRetainedAffiliationIntoIssueStateBeforeDroppingTables()
    {
        await using var database = CreateDatabase(PreviousMigration);
        await using (var seed = database.CreateDbContext())
        {
            await seed.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Epics" ("ProjectId", "Number", "Title", "Description", "Priority", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('proj_alpha', 7, 'Alpha 7', '', 'p2', 'idle', '2026-07-17 00:00:00+00:00', '2026-07-17 00:00:00+00:00'),
                       ('proj_alpha', 9, 'Alpha 9', '', 'p2', 'idle', '2026-07-17 00:00:00+00:00', '2026-07-17 00:00:00+00:00'),
                       ('proj_beta', 7, 'Beta 7', '', 'p2', 'idle', '2026-07-17 00:00:00+00:00', '2026-07-17 00:00:00+00:00');

                INSERT INTO "Issues" ("IssueId", "State", "EpicId", "EpicNumber")
                VALUES ('issue_alpha_42', '{{"projectId":"proj_alpha","number":42,"title":"Alpha 42","status":"backlog","priority":"p2"}}', NULL, NULL),
                       ('issue_alpha_43', '{{"projectId":"proj_alpha","number":43,"title":"Alpha 43","status":"backlog","priority":"p2"}}', NULL, NULL),
                       ('issue_beta_42', '{{"projectId":"proj_beta","number":42,"title":"Beta 42","status":"backlog","priority":"p2"}}', NULL, NULL);

                INSERT INTO "EpicIssues" ("EpicId", "IssueId", "ProjectId", "IssueNumber", "EpicNumber", "CreatedAt")
                VALUES ('epic_alpha_7', 'issue_alpha_42', 'proj_alpha', 42, 7, '2026-07-16 00:00:00+00:00'),
                       ('epic_alpha_7', 'issue_alpha_43', 'proj_alpha', 43, 7, '2026-07-16 00:00:00+00:00'),
                       ('epic_beta_7', 'issue_beta_42', 'proj_beta', 42, 7, '2026-07-16 00:00:00+00:00');

                INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "EpicNumber", "CreatedAt")
                VALUES ('proj_alpha', 'issue_alpha_42', 'epic_alpha_9', 42, 9, '2026-07-17 00:00:00+00:00'),
                       ('proj_beta', 'issue_beta_42', 'epic_beta_7', 42, 7, '2026-07-17 00:00:00+00:00');
                """);

            await seed.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "EpicIssues"));
        Assert.False(await TableExistsAsync(verify, "EpicActiveIssues"));

        var identities = await ReadStringsAsync(
            verify,
            "SELECT \"ProjectId\" || ':' || \"Number\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\", \"Number\"");
        var states = await ReadStringsAsync(
            verify,
            "SELECT \"State\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\", \"Number\"");
        Assert.Equal(
            ["proj_alpha:42:9", "proj_alpha:43:7", "proj_beta:42:7"],
            identities.Zip(states).Select(pair => $"{pair.First}:{IssueStore.Deserialize(pair.Second)!.EpicNumber}").ToArray());
    }

    private static async Task<string[]> ReadStringsAsync(MohistDbContext context, string sql) =>
        await context.Database.SqlQueryRaw<string>(sql).ToArrayAsync();

    private static async Task<bool> TableExistsAsync(MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task InsertLegacyIssueAsync(MohistDbContext context, string state, int epicNumber)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"Issues\" (\"IssueId\", \"State\", \"EpicNumber\") VALUES ($issueId, $state, $epicNumber)";

        var issueIdParameter = command.CreateParameter();
        issueIdParameter.ParameterName = "$issueId";
        issueIdParameter.Value = "issue_42";
        command.Parameters.Add(issueIdParameter);

        var stateParameter = command.CreateParameter();
        stateParameter.ParameterName = "$state";
        stateParameter.Value = state;
        command.Parameters.Add(stateParameter);

        var epicParameter = command.CreateParameter();
        epicParameter.ParameterName = "$epicNumber";
        epicParameter.Value = epicNumber;
        command.Parameters.Add(epicParameter);

        await command.ExecuteNonQueryAsync();
    }

    private static TestDatabase CreateDatabase(string targetMigration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, targetMigration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        : IAsyncDisposable
    {
        public MohistDbContext CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
