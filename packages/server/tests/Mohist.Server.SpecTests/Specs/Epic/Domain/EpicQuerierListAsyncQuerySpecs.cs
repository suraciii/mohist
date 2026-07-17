using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicQuerierListAsyncQuerySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_SearchesTitlesCaseInsensitively()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, 1, "Authentication overhaul");
        await SeedEpicAsync(database, 2, "Billing dunning");
        await SeedEpicAsync(database, 3, "OAuth integration");
        var querier = new EpicQuerier(database.Factory, null!);

        var matches = await querier.ListAsync("proj_1", search: "AUTH");

        Assert.Equal(new[] { 1, 3 }, matches.Select(epic => epic.Number).Order());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_TreatsLikeMetacharactersAsLiterals()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, 1, "Discount 100% ready");
        await SeedEpicAsync(database, 2, "Auth_token cleanup");
        await SeedEpicAsync(database, 3, "Plain title");
        var querier = new EpicQuerier(database.Factory, null!);

        var percent = await querier.ListAsync("proj_1", search: "%");
        var underscore = await querier.ListAsync("proj_1", search: "_");

        Assert.Equal(1, Assert.Single(percent).Number);
        Assert.Equal(2, Assert.Single(underscore).Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_AppliesSupportedSortAndRejectsUnknownSelectors()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, 1, "Low", priority: "p4");
        await SeedEpicAsync(database, 2, "High", priority: "p0");
        var querier = new EpicQuerier(database.Factory, null!);

        var sorted = await querier.ListAsync("proj_1", sortBy: "priority", sortDir: "asc");

        Assert.Equal(new[] { 2, 1 }, sorted.Select(epic => epic.Number));
        Assert.Equal(EpicQuerier.DefaultOrderBy, EpicQuerier.ResolveOrderBy("priority", "asc); DROP TABLE--"));
    }

    private static async Task SeedEpicAsync(
        TestDatabase database,
        int number,
        string title,
        string priority = "p2")
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            ProjectId = "proj_1",
            Number = number,
            Title = title,
            Description = "",
            Priority = priority,
            Status = "idle",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, new TestDbContextFactory(options));
    }

    private sealed class TestDatabase(SqliteConnection connection, TestDbContextFactory factory) : IAsyncDisposable
    {
        public IDbContextFactory<MohistDbContext> Factory => factory;
        public MohistDbContext CreateDbContext() => factory.CreateDbContext();
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
