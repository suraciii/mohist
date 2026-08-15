using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Inbox;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Xunit;

namespace Mohist.Server.UnitTests.Inbox;

/// <summary>
/// Lower-owner coverage for the unread-count selection behind
/// <c>GET /api/projects/{projectRef}/inbox/unread-count</c>: strict
/// project scoping and read/archived exclusion live here; the HTTP layer
/// keeps only its JSON shape contract.
/// </summary>
public sealed class InboxQuerierTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> CreateOpenConnectionAsync()
    {
        var keeper = new SqliteConnection($"Data Source=inbox-querier-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        SqliteSchemaTemplate.CopyModelSchemaTo(keeper);
        return keeper;
    }

    private static async Task<InboxItemRow> SeedAsync(IDbContextFactory<MohistDbContext> factory, string projectId, string suffix)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = new InboxItemRow
        {
            Id = $"inbox-{projectId}-{suffix}",
            ProjectId = projectId,
            IssueNumber = 1,
            IssueTitle = "title",
            NotificationKind = "issue_started",
            SourceEventSource = "/mohist/spec",
            SourceEventId = Guid.NewGuid().ToString(),
        };
        db.InboxItems.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task CountUnread_IsProjectScoped_AndExcludesReadAndArchived()
    {
        await using var keeper = await CreateOpenConnectionAsync();
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(keeper).Options);
        var querier = new InboxQuerier(factory);

        var projectId = "proj-inbox-count";
        var otherProjectId = "proj-inbox-other";
        var read = await SeedAsync(factory, projectId, "read");
        await SeedAsync(factory, projectId, "archived");
        await SeedAsync(factory, otherProjectId, "unread");

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.InboxItems.Attach(read).Entity.ReadAt = FixedTime;
            var archived = await db.InboxItems.SingleAsync(r => r.Id == $"inbox-{projectId}-archived");
            archived.ArchivedAt = FixedTime;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await querier.CountUnreadAsync(projectId));
        await SeedAsync(factory, projectId, "unread");
        Assert.Equal(1, await querier.CountUnreadAsync(projectId));
        Assert.Equal(1, await querier.CountUnreadAsync(otherProjectId));
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
