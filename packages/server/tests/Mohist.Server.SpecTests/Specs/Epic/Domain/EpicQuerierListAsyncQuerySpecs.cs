using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicQuerierListAsyncQuerySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_WithNoParams_SqlIsByteIdenticalToLegacyQuery()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);

        var (querier, commands) = CreateCountingQuerier(database);
        var result = await querier.ListAsync("proj_1");

        var only = Assert.Single(commands, c => c.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ORDER BY e.\"Priority\" ASC, e.\"UpdatedAt\" DESC, li.\"CreatedAt\"", only);
        Assert.DoesNotContain("@search", only);
        Assert.DoesNotContain("LOWER(e.\"Title\")", only);
        Assert.Single(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_WithSearchTerm_FiltersByCaseInsensitiveTitleSubstring()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_auth", 1, title: "Authentication overhaul");
        await SeedEpicAsync(database, "proj_1", "epic_billing", 2, title: "Billing dunning");
        await SeedEpicAsync(database, "proj_1", "epic_oauth", 3, title: "OAuth integration");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var authResult = await querier.ListAsync("proj_1", search: "auth");
        Assert.Equal(2, authResult.Count);
        Assert.Contains(authResult, e => e.Id == "epic_auth");
        Assert.Contains(authResult, e => e.Id == "epic_oauth");

        var uppercaseResult = await querier.ListAsync("proj_1", search: "AUTH");
        Assert.Equal(2, uppercaseResult.Count);
        Assert.Contains(uppercaseResult, e => e.Id == "epic_auth");

        var mixedCaseResult = await querier.ListAsync("proj_1", search: "AuTh");
        Assert.Equal(2, mixedCaseResult.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_WithSearchTerm_PassesBoundParameter_NotInterpolatedLiteral()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1, title: "Authentication");

        var (querier, commands) = CreateCountingQuerier(database);
        await querier.ListAsync("proj_1", search: "Auth");

        var only = Assert.Single(commands, c => c.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LOWER(e.\"Title\") LIKE LOWER('%' || @search || '%') ESCAPE '\\'", only);
        Assert.Contains("@search", only);
        Assert.Contains("@projectId", only);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_SearchTreatsPercentAndUnderscoreAsLiteralText()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_percent", 1, title: "Discount 100% ready");
        await SeedEpicAsync(database, "proj_1", "epic_underscore", 2, title: "Auth_token cleanup");
        await SeedEpicAsync(database, "proj_1", "epic_unrelated", 3, title: "Plain unrelated title");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var percentResult = await querier.ListAsync("proj_1", search: "%");
        Assert.Single(percentResult);
        Assert.Equal("epic_percent", percentResult[0].Id);

        var underscoreResult = await querier.ListAsync("proj_1", search: "_");
        Assert.Single(underscoreResult);
        Assert.Equal("epic_underscore", underscoreResult[0].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_WithEmptyOrWhitespaceSearch_OmitsFilterClause()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedEpicAsync(database, "proj_1", "epic_2", 2, title: "Other");

        var (querier, commands) = CreateCountingQuerier(database);

        var emptyResult = await querier.ListAsync("proj_1", search: "");
        Assert.Equal(2, emptyResult.Count);

        var whitespaceResult = await querier.ListAsync("proj_1", search: "   ");
        Assert.Equal(2, whitespaceResult.Count);

        foreach (var command in commands.Where(c => c.Contains("SELECT", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain("@search", command);
            Assert.DoesNotContain("LOWER(e.\"Title\")", command);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_WithNullSearch_ReturnsAllEpicsRegardlessOfTitle()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedEpicAsync(database, "proj_1", "epic_2", 2, title: "Anything");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1", search: null);

        Assert.Equal(2, result.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_SortPriorityAsc_OrdersP0BeforeP4()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_p4", 1, priority: "p4");
        await SeedEpicAsync(database, "proj_1", "epic_p0", 2, priority: "p0");
        await SeedEpicAsync(database, "proj_1", "epic_p2", 3, priority: "p2");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1", sortBy: "priority", sortDir: "asc");

        Assert.Equal(3, result.Count);
        Assert.Equal("epic_p0", result[0].Id);
        Assert.Equal("epic_p2", result[1].Id);
        Assert.Equal("epic_p4", result[2].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_SortUpdatedDesc_OrdersMostRecentlyUpdatedFirst()
    {
        await using var database = CreateDatabase();
        var old = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var mid = old.AddDays(1);
        var recent = old.AddDays(7);
        await SeedEpicAsync(database, "proj_1", "epic_old", 1, updatedAt: old);
        await SeedEpicAsync(database, "proj_1", "epic_recent", 2, updatedAt: recent);
        await SeedEpicAsync(database, "proj_1", "epic_mid", 3, updatedAt: mid);

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1", sortBy: "updated", sortDir: "desc");

        Assert.Equal("epic_recent", result[0].Id);
        Assert.Equal("epic_mid", result[1].Id);
        Assert.Equal("epic_old", result[2].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_NoSortParams_YieldsLegacyPriorityAscThenUpdatedDesc()
    {
        await using var database = CreateDatabase();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        await SeedEpicAsync(database, "proj_1", "epic_p2_old", 1, priority: "p2", updatedAt: now);
        await SeedEpicAsync(database, "proj_1", "epic_p2_new", 2, priority: "p2", updatedAt: now.AddMinutes(1));
        await SeedEpicAsync(database, "proj_1", "epic_p0", 3, priority: "p0");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1");

        Assert.Equal("epic_p0", result[0].Id);
        Assert.Equal("epic_p2_new", result[1].Id);
        Assert.Equal("epic_p2_old", result[2].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_SearchAndSortCompose_FiltersAndOrdersTogether()
    {
        await using var database = CreateDatabase();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        await SeedEpicAsync(database, "proj_1", "epic_auth_old", 1, title: "Authentication legacy", priority: "p1", updatedAt: now);
        await SeedEpicAsync(database, "proj_1", "epic_auth_new", 2, title: "Authentication modern", priority: "p3", updatedAt: now.AddDays(1));
        await SeedEpicAsync(database, "proj_1", "epic_billing", 3, title: "Billing dunning", priority: "p0");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1", search: "auth", sortBy: "updated", sortDir: "desc");

        Assert.Equal(2, result.Count);
        Assert.Equal("epic_auth_new", result[0].Id);
        Assert.Equal("epic_auth_old", result[1].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_UnknownSortOrDir_FallsBackToDefaultOrdering()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_p0", 1, priority: "p0");
        await SeedEpicAsync(database, "proj_1", "epic_p2", 2, priority: "p2");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var unknownSort = await querier.ListAsync("proj_1", sortBy: "garbage", sortDir: "asc");
        Assert.Equal("epic_p0", unknownSort[0].Id);
        Assert.Equal("epic_p2", unknownSort[1].Id);

        var unknownDir = await querier.ListAsync("proj_1", sortBy: "priority", sortDir: "sideways");
        Assert.Equal("epic_p0", unknownDir[0].Id);
        Assert.Equal("epic_p2", unknownDir[1].Id);

        var bothUnknown = await querier.ListAsync("proj_1", sortBy: "totally-bogus", sortDir: "nope");
        Assert.Equal("epic_p0", bothUnknown[0].Id);
        Assert.Equal("epic_p2", bothUnknown[1].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void ListAsync_SortTokensNonAlphanumeric_AreRejectedAndFallBackToDefault()
    {
        // Malicious / accidental non-alpha tokens must never reach SQL.
        // NormalizeSortToken rejects anything that isn't letters/digits,
        // returning null -> default ordering.
        var orderBy = EpicQuerier.ResolveOrderBy("priority'); DROP TABLE Epics;--", "asc");
        Assert.Equal(EpicQuerier.DefaultOrderBy, orderBy);

        var dirOnly = EpicQuerier.ResolveOrderBy("priority", "asc); DROP TABLE--");
        Assert.Equal(EpicQuerier.DefaultOrderBy, dirOnly);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void ListAsync_CreatedSortIsNotPartOfIssue94Contract()
    {
        Assert.Equal(EpicQuerier.DefaultOrderBy, EpicQuerier.ResolveOrderBy("created", "asc"));
        Assert.Equal(EpicQuerier.DefaultOrderBy, EpicQuerier.ResolveOrderBy("created", "desc"));
    }

    private static (EpicQuerier Querier, List<string> Commands) CreateCountingQuerier(TestDatabase database)
    {
        // Eagerly migrate the shared in-memory connection before the
        // querier opens its own context so the migration is not counted
        // as a SELECT and not interleaved with the assertions' reads.
        var commands = new List<string>();
        var originalConnection = database.Connection;
        var countingOptions = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(originalConnection)
            .EnableSensitiveDataLogging()
            .LogTo(
                message => commands.Add(message),
                new[] { RelationalEventId.CommandExecuted },
                LogLevel.Information)
            .Options;
        var factory = new CountingFactory(countingOptions);
        return (new EpicQuerier(factory, new ThrowingIssueQuerier()), commands);
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId,
        string epicId,
        int number,
        string priority = "p2",
        DateTimeOffset? updatedAt = null,
        string title = "")
    {
        var now = updatedAt ?? TestTime.UtcNow;
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = title.Length > 0 ? title : $"Epic {number}",
            Description = "",
            Priority = priority,
            Status = "idle",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        public TestDatabase(SqliteConnection connection, IDbContextFactory<MohistDbContext> factory)
        {
            Connection = connection;
            Factory = factory;
        }

        public SqliteConnection Connection { get; }

        public IDbContextFactory<MohistDbContext> Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class CountingFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public CountingFactory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowingIssueQuerier : IssueQuerier
    {
        public ThrowingIssueQuerier()
            : base(null!, null!, null!, null!, null!, null!)
        {
        }

        public new Task<List<IssueReadModel>> ListAsync(
            string projectId,
            ProjectInfo? project = null,
            string? stage = null,
            string? label = null,
            string? priority = null,
            bool? archived = null,
            bool? all = null) =>
            throw new InvalidOperationException("IssueQuerier.ListAsync should not be invoked on the epic list path.");
    }
}
