using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicActiveIssueMigrationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Migration_BackfillsActiveMembershipSlotsOnlyForNonTerminalOwners()
    {
        await using var database = CreateDatabase("20260626200645_AddEpicListDerivedColumns");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        Assert.False(await TableExistsAsync(context, "EpicActiveIssues"));

        var createdAt = TestTime.UtcNow;
        context.Epics.AddRange(
            NewEpic("epic_idle", 1, "idle", createdAt),
            NewEpic("epic_running", 2, "running", createdAt.AddMinutes(1)),
            NewEpic("epic_paused", 3, "paused", createdAt.AddMinutes(2)),
            NewEpic("epic_done", 4, "done", createdAt.AddMinutes(3)),
            NewEpic("epic_closed", 5, "closed", createdAt.AddMinutes(4)));
        context.EpicIssues.AddRange(
            NewLink("epic_idle", "issue_idle", 101, createdAt),
            NewLink("epic_running", "issue_running", 102, createdAt.AddMinutes(1)),
            NewLink("epic_paused", "issue_paused", 103, createdAt.AddMinutes(2)),
            NewLink("epic_done", "issue_done", 104, createdAt.AddMinutes(3)),
            NewLink("epic_closed", "issue_closed", 105, createdAt.AddMinutes(4)));
        await context.SaveChangesAsync();

        await migrator.MigrateAsync("20260628022822_DropEpicIssueMembershipUniqueIndex");

        var activeSlots = await context.EpicActiveIssues
            .AsNoTracking()
            .OrderBy(row => row.IssueNumber)
            .Select(row => new { row.EpicId, row.IssueId, row.IssueNumber })
            .ToListAsync();

        Assert.Equal(3, activeSlots.Count);
        Assert.Collection(activeSlots,
            row =>
            {
                Assert.Equal("epic_idle", row.EpicId);
                Assert.Equal("issue_idle", row.IssueId);
                Assert.Equal(101, row.IssueNumber);
            },
            row =>
            {
                Assert.Equal("epic_running", row.EpicId);
                Assert.Equal("issue_running", row.IssueId);
                Assert.Equal(102, row.IssueNumber);
            },
            row =>
            {
                Assert.Equal("epic_paused", row.EpicId);
                Assert.Equal("issue_paused", row.IssueId);
                Assert.Equal(103, row.IssueNumber);
            });

        Assert.Equal(5, await context.EpicIssues.AsNoTracking().CountAsync());
        Assert.DoesNotContain(activeSlots, row => row.EpicId is "epic_done" or "epic_closed");
    }

    private static EpicRow NewEpic(string id, int number, string status, DateTimeOffset createdAt) => new()
    {
        Id = id,
        ProjectId = "project_1",
        Number = number,
        Title = $"{status} epic",
        Description = "",
        Priority = "p2",
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static EpicIssueRow NewLink(string epicId, string issueId, int issueNumber, DateTimeOffset createdAt) => new()
    {
        EpicId = epicId,
        ProjectId = "project_1",
        IssueId = issueId,
        IssueNumber = issueNumber,
        CreatedAt = createdAt,
    };

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

    private static TestDatabase CreateDatabase(string migratedTo)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, migratedTo);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }
}
