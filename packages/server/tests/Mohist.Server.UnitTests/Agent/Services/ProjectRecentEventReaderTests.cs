using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class ProjectRecentEventReaderTests
{
    [Fact]
    public async Task ListAsyncFiltersProjectOrdersNewestFirstAndBoundsResults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(connection).Options;
        await using (var db = new MohistDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.IssueEvents.AddRange(
                Row(1, "p", "one", "2026-01-01T00:00:00Z"),
                Row(2, "q", "other", "2026-01-01T00:02:00Z"),
                Row(3, "p", "three", "2026-01-01T00:03:00Z"),
                Row(4, null, "unprojected", "2026-01-01T00:04:00Z"));
            await db.SaveChangesAsync();
        }

        var factory = new TestContextFactory(options);
        var reader = new ProjectRecentEventReader(factory);
        var events = await reader.ListAsync("p", 1);

        var eventEntry = Assert.Single(events);
        Assert.Equal("three", eventEntry.EventId);
        Assert.Equal("p", eventEntry.Input.GetValue("projectid"));
    }

    private static IssueEventRow Row(long id, string? projectId, string eventId, string time) => new()
    {
        Id = id,
        Source = $"/mohist/projects/{projectId ?? "unknown"}/issues/1",
        TimelineSource = $"/mohist/projects/{projectId ?? "unknown"}/issues/1",
        EventId = eventId,
        Type = "test.event",
        Time = DateTimeOffset.Parse(time),
        SpecVersion = "1.0",
        DataContentType = "application/json",
        Data = JsonSerializer.SerializeToElement(new { }),
        ExtensionsJson = projectId is null ? "{}" : JsonSerializer.Serialize(new { projectid = projectId }),
    };

    private sealed class TestContextFactory(DbContextOptions<MohistDbContext> options) : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MohistDbContext(options));
    }
}
