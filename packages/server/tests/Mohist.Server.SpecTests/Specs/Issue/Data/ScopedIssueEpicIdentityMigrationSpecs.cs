using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Data;

/// <summary>
/// Issue #412 T-001 specs: prove that the current-state migration
/// (<c>20260716120000_AdoptScopedIssueEpicIdentity</c>) re-resolves every
/// reference row to the canonical (ProjectId, Number) Issue / Epic pair,
/// runs twice without producing duplicates, and preserves cross-Project
/// isolation when the same number lives in two Projects.
///
/// The data migration applies Up SQL against existing rows. Each spec
/// follows the same shape as <c>EpicIdleRenameMigrationSpecs</c>:
/// build the schema once with <c>EnsureCreated</c>, seed rows that carry
/// the legacy gaps the migration is supposed to close, then run the
/// migration's Up() SQL directly. A separate spec
/// (<c>Migration_IsRegisteredInEfmigrationsHistory</c>) proves the
/// migration class is wired into EF's migration assembly.
/// </summary>
public class ScopedIssueEpicIdentityMigrationSpecs
{
    private const string MigrationName = "20260716120000_AdoptScopedIssueEpicIdentity";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Migration_RunsAfterSeedAndAdoptsCanonicalReferences()
    {
        await using var database = await OpenSeededAsync();

        await ExecuteMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();

        var issueA = await verify.Issues.AsNoTracking()
            .SingleAsync(i => i.ProjectId == "proj_a" && i.Number == 42);
        var issueB = await verify.Issues.AsNoTracking()
            .SingleAsync(i => i.ProjectId == "proj_b" && i.Number == 42);
        Assert.NotEqual(issueA.IssueId, issueB.IssueId);

        var commentsByProject = await verify.IssueComments.AsNoTracking()
            .Where(c => c.IssueNumber == 42)
            .GroupBy(c => c.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync();

        Assert.Equal(2, commentsByProject.Count);
        Assert.Single(commentsByProject, x => x.ProjectId == "proj_a" && x.Count == 2);
        Assert.Single(commentsByProject, x => x.ProjectId == "proj_b" && x.Count == 1);

        // cmt_a_2 was seeded with an empty IssueId — the migration must
        // backfill it from the canonical Issue row of the same Project.
        var projACommentIds = await verify.IssueComments.AsNoTracking()
            .Where(c => c.ProjectId == "proj_a" && c.IssueNumber == 42)
            .Select(c => c.IssueId)
            .ToListAsync();
        Assert.Equal(2, projACommentIds.Count);
        Assert.All(projACommentIds, id => Assert.Equal("issue_a_42", id));

        var inboxByProject = await verify.InboxItems.AsNoTracking()
            .Where(i => i.IssueNumber == 42)
            .GroupBy(i => i.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync();

        Assert.Equal(2, inboxByProject.Count);
        Assert.Single(inboxByProject, x => x.ProjectId == "proj_a" && x.Count == 1);
        Assert.Single(inboxByProject, x => x.ProjectId == "proj_b" && x.Count == 1);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Migration_AfterRunningOnce_ConvergesAndIsNoOpOnSecondRun()
    {
        await using var database = await OpenSeededAsync();

        await ExecuteMigrationUpAsync(database);

        await using (var verifyOnce = database.CreateDbContext())
        {
            Assert.Equal(2, await verifyOnce.Issues.AsNoTracking().CountAsync(i => i.Number == 42));
            Assert.Equal(3, await verifyOnce.IssueComments.AsNoTracking().CountAsync(c => c.IssueNumber == 42));
            Assert.Equal(2, await verifyOnce.InboxItems.AsNoTracking().CountAsync(i => i.IssueNumber == 42));
            Assert.Equal(3, await verifyOnce.EpicIssues.AsNoTracking().CountAsync());
        }

        // Idempotency: a second application of the same Up() SQL must not
        // duplicate anything or change the canonical references.
        await ExecuteMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.Issues.AsNoTracking().CountAsync(i => i.Number == 42));
        Assert.Equal(3, await verify.IssueComments.AsNoTracking().CountAsync(c => c.IssueNumber == 42));
        Assert.Equal(2, await verify.InboxItems.AsNoTracking().CountAsync(i => i.IssueNumber == 42));
        Assert.Equal(3, await verify.EpicIssues.AsNoTracking().CountAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Migration_DropsOrphanMembershipRows()
    {
        await using var database = await OpenSeededAsync(seedOrphanMembership: true);

        await ExecuteMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var orphans = await verify.EpicIssues.AsNoTracking()
            .Where(row => row.IssueNumber == 9999)
            .ToListAsync();
        Assert.Empty(orphans);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Migration_IsRegisteredInEfmigrationsHistory()
    {
        // Fresh schema on an empty DB; full Migrate() runs every migration
        // including the new one. Proves the migration class is wired into
        // EF's migration assembly and recorded in __EFMigrationsHistory.
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new MohistDbContext(options))
        {
            db.GetService<IMigrator>().Migrate(MigrationName);
        }

        await using var verify = new MohistDbContext(options);
        var applied = await verify.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationName, applied);
    }

    private static async Task<TestDatabase> OpenSeededAsync(bool seedOrphanMembership = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new MohistDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await SeedAsync(db, seedOrphanMembership);
        }

        return new TestDatabase(connection, factory);
    }

    private static async Task ExecuteMigrationUpAsync(TestDatabase database)
    {
        // Apply the migration's Up() SQL directly. The literal text must
        // stay in sync with
        // Infrastructure/Data/Migrations/20260716120000_AdoptScopedIssueEpicIdentity.cs
        // — the schema-only Migration_IsRegisteredInEfmigrationsHistory spec
        // guarantees EF finds the same migration class on full Migrate().
        var statements = new[]
        {
            """
            UPDATE IssueComments
            SET "IssueId" = COALESCE(NULLIF(IssueComments."IssueId", ''), i."IssueId"),
                "IssueNumber" = COALESCE(NULLIF(IssueComments."IssueNumber", 0), i."Number")
            FROM Issues i
            WHERE i."ProjectId" = IssueComments."ProjectId"
              AND i."Number" = IssueComments."IssueNumber"
              AND (IssueComments."IssueId" = '' OR IssueComments."IssueNumber" = 0);
            """,
            """
            UPDATE InboxItems
            SET "IssueId" = COALESCE(NULLIF(InboxItems."IssueId", ''), i."IssueId")
            FROM Issues i
            WHERE i."ProjectId" = InboxItems."ProjectId"
              AND i."Number" = InboxItems."IssueNumber"
              AND InboxItems."IssueId" = '';
            """,
            """
            DELETE FROM EpicIssues
            WHERE NOT EXISTS (
                SELECT 1 FROM Issues i
                WHERE i."ProjectId" = EpicIssues."ProjectId"
                  AND i."Number" = EpicIssues."IssueNumber"
            )
            OR NOT EXISTS (
                SELECT 1 FROM Epics e
                WHERE e."ProjectId" = EpicIssues."ProjectId"
                  AND e."Id" = EpicIssues."EpicId"
            );
            """,
            """
            DELETE FROM EpicActiveIssues
            WHERE NOT EXISTS (
                SELECT 1 FROM Issues i
                WHERE i."ProjectId" = EpicActiveIssues."ProjectId"
                  AND i."Number" = EpicActiveIssues."IssueNumber"
            )
            OR NOT EXISTS (
                SELECT 1 FROM Epics e
                WHERE e."ProjectId" = EpicActiveIssues."ProjectId"
                  AND e."Id" = EpicActiveIssues."EpicId"
            );
            """,
        };

        await using var db = database.CreateDbContext();
        foreach (var statement in statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    private static async Task SeedAsync(MohistDbContext db, bool seedOrphan)
    {
        db.Projects.Add(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = "proj_a",
            Name = "project-a",
            RepositoriesJson = "[]",
        });
        db.Projects.Add(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = "proj_b",
            Name = "project-b",
            RepositoriesJson = "[]",
        });

        db.Issues.AddRange(
            NewIssue("issue_a_42", "proj_a", 42, "Auth epic detail"),
            NewIssue("issue_a_1", "proj_a", 1, "Setup"),
            NewIssue("issue_b_42", "proj_b", 42, "Other project 42"),
            NewIssue("issue_b_1", "proj_b", 1, "B-side"));

        db.Epics.Add(NewEpic("epic_a_7", "proj_a", 7, "Auth epic"));
        db.Epics.Add(NewEpic("epic_b_7", "proj_b", 7, "B-side epic"));

        db.IssueComments.AddRange(
            NewComment("cmt_a_1", "proj_a", 42, "issue_a_42", "first"),
            // cmt_a_2 carries empty IssueId — the migration must backfill it.
            NewComment("cmt_a_2", "proj_a", 42, "", "second"),
            NewComment("cmt_b_1", "proj_b", 42, "issue_b_42", "first"));

        db.InboxItems.AddRange(
            new InboxItemRow
            {
                Id = "inb_a",
                ProjectId = "proj_a",
                IssueId = "issue_a_42",
                IssueNumber = 42,
                NotificationKind = "issue_completed",
                SourceEventSource = "/mohist/issues/issue_a_42",
                SourceEventId = "evt_a_1",
            },
            new InboxItemRow
            {
                Id = "inb_b",
                ProjectId = "proj_b",
                IssueId = "",
                IssueNumber = 42,
                NotificationKind = "issue_completed",
                SourceEventSource = "/mohist/issues/issue_b_42",
                SourceEventId = "evt_b_1",
            });

        db.EpicIssues.AddRange(
            new EpicIssueRow
            {
                EpicId = "epic_a_7",
                ProjectId = "proj_a",
                IssueId = "issue_a_42",
                IssueNumber = 42,
            },
            new EpicIssueRow
            {
                EpicId = "epic_a_7",
                ProjectId = "proj_a",
                IssueId = "issue_a_1",
                IssueNumber = 1,
            },
            new EpicIssueRow
            {
                EpicId = "epic_b_7",
                ProjectId = "proj_b",
                IssueId = "issue_b_42",
                IssueNumber = 42,
            });

        if (seedOrphan)
        {
            db.EpicIssues.Add(new EpicIssueRow
            {
                EpicId = "epic_a_7",
                ProjectId = "proj_a",
                IssueId = "issue_phantom",
                IssueNumber = 9999,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private static IssueRow NewIssue(string id, string projectId, int number, string title)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            projectId,
            number,
            title,
            status = "active",
            priority = "p2",
            isDraft = false,
            prerequisiteNumbers = Array.Empty<int>(),
        });
        return new IssueRow { IssueId = id, State = stateJson };
    }

    private static Mohist.Server.Infrastructure.Data.Epic.EpicRow NewEpic(string id, string projectId, int number, string title) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            Number = number,
            Title = title,
            Priority = "p2",
            Status = "active",
        };

    private static IssueCommentRow NewComment(string id, string projectId, int issueNumber, string issueId, string body) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            Body = body,
        };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, IDbContextFactory<MohistDbContext> factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public IDbContextFactory<MohistDbContext> Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public ValueTask DisposeAsync()
        {
            _connection.Dispose();
            return ValueTask.CompletedTask;
        }
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
