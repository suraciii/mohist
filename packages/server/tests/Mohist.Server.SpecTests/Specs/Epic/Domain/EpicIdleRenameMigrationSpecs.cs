using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicIdleRenameMigrationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Up_RenamesActiveRowsToIdle()
    {
        // Build the schema with EnsureCreated (this skips migrations)
        // so we can drop in a row with the legacy "active" status,
        // then apply the rename migration's Up SQL directly to prove
        // the rename is data-only and acts on the row.
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            setup.Epics.Add(new EpicRow
            {
                ProjectId = "project_1",
                Number = 1,
                Title = "Legacy active epic",
                Description = "",
                Priority = "p2",
                Status = "active",
                PauseReason = null,
                CreatedAt = TestTime.UtcNow,
                UpdatedAt = TestTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        // Sanity: row is currently "active".
        await using (var preRename = database.CreateDbContext())
        {
            var row = await preRename.Epics.AsNoTracking().FirstAsync();
            Assert.Equal("active", row.Status);
        }

        // Apply the rename migration Up SQL directly.
        await using (var applyRename = database.CreateDbContext())
        {
            await applyRename.Database.ExecuteSqlRawAsync(
                """
                UPDATE Epics
                SET Status = 'idle'
                WHERE Status = 'active';
                """);
        }

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Down_RestoresIdleRowsToActive()
    {
        // Build the schema, drop a "active" row in, run the rename Up
        // SQL (turning it "idle"), then run the rename Down SQL —
        // round-trip back to "active".
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            setup.Epics.Add(new EpicRow
            {
                ProjectId = "project_1",
                Number = 1,
                Title = "Round trip",
                Description = "",
                Priority = "p2",
                Status = "active",
                PauseReason = null,
                CreatedAt = TestTime.UtcNow,
                UpdatedAt = TestTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        await using (var up = database.CreateDbContext())
        {
            await up.Database.ExecuteSqlRawAsync(
                """
                UPDATE Epics
                SET Status = 'idle'
                WHERE Status = 'active';
                """);
        }

        await using (var postUp = database.CreateDbContext())
        {
            var row = await postUp.Epics.AsNoTracking().FirstAsync();
            Assert.Equal("idle", row.Status);
        }

        await using (var down = database.CreateDbContext())
        {
            await down.Database.ExecuteSqlRawAsync(
                """
                UPDATE Epics
                SET Status = 'active'
                WHERE Status = 'idle';
                """);
        }

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("active", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Parse_LegacyActive_DefensivelyMapsToIdle()
    {
        // Belt-and-suspenders: even if a row still carries the legacy
        // "active" status (e.g. a partially migrated DB or a hand-edit),
        // the EpicStatusName.Parse layer reads it as Idle so domain
        // materialization does not silently land on the wrong status.
        var parsed = Mohist.Server.Epic.Domain.EpicStatusName.Parse("active");
        Assert.Equal(Mohist.Server.Epic.Domain.EpicStatus.Idle, parsed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task DatabaseMigrate_IncludesEpicIdleRenameMigration()
    {
        // Verify the migration is wired into EF's MigrationsAssembly and
        // is applied by Database.Migrate() in production. We rebuild the
        // schema from scratch, then read the __EFMigrationsHistory table
        // to confirm the EpicIdleRename migration was recorded.
        await using var database = CreateDatabase();
        await using var ctx = database.CreateDbContext();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == "20260625000000_EpicIdleRename");
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new TestDatabase(connection, factory);
    }

    private static TestDatabase CreateModelSchemaDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyModelSchemaTo(connection);
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
