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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Migration_DropsLegacyMembershipTables_WithoutChangingIssueAffiliation()
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
            seed.Issues.Add(new IssueRow
            {
                State = IssueStore.Serialize(issue),
                EpicNumber = 7,
            });
            await seed.SaveChangesAsync();
            Assert.True(await TableExistsAsync(seed, "EpicIssues"));
            Assert.True(await TableExistsAsync(seed, "EpicActiveIssues"));

            await seed.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using var verify = database.CreateDbContext();
        Assert.False(await TableExistsAsync(verify, "EpicIssues"));
        Assert.False(await TableExistsAsync(verify, "EpicActiveIssues"));
        Assert.Contains(TargetMigration, await verify.Database.GetAppliedMigrationsAsync());

        var row = await verify.Issues.SingleAsync();
        Assert.Equal(7, row.EpicNumber);
        var issue = IssueStore.Deserialize(row.State);
        Assert.NotNull(issue);
        Assert.Equal(7, issue!.EpicNumber);
    }

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
