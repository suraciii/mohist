using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Specs.Sessions;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public sealed class SqliteResourceCleanupSpecs
{
    [Fact]
    public async Task MigratedSqliteTemplate_CopyTo_CopiesCurrentMigratedSchema()
    {
        using var database = TestSqliteDatabase.CreateEmpty();

        MigratedSqliteTemplate.CopyTo(database.Keeper);

        await using var db = database.CreateContext();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
        Assert.Contains(SquashedMigrationHistory.BaselineId, applied);
    }

    [Fact]
    public void MigratedSqliteTemplate_WhenMigrationFails_DisposesConnection()
    {
        var connection = new TrackingSqliteConnection();
        connection.Open();

        var error = Assert.Throws<InvalidOperationException>(() =>
            MigratedSqliteTemplate.CreateTemplate(
                () => connection,
                "migration-that-does-not-exist"));

        Assert.Contains("migration-that-does-not-exist", error.Message);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void MigratedSqliteTemplate_WarmForTest_WhenFullMigrationFails_RethrowsOriginalAndDisposesConnection()
    {
        var connection = new TrackingSqliteConnection();
        connection.Open();
        var migrationFailure = new InvalidOperationException("full migration failure");

        var error = Assert.Throws<InvalidOperationException>(() =>
            MigratedSqliteTemplate.WarmForTest(
                () => connection,
                _ => throw migrationFailure));

        Assert.Same(migrationFailure, error);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void TestSqliteDatabase_WhenOpenFails_DisposesKeeperOnce()
    {
        var openFailure = new InvalidOperationException("open failure");
        var connection = new TrackingSqliteConnection(openFailure);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestSqliteDatabase.Create(() => connection, static _ => { }));

        Assert.Same(openFailure, error);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void TestSqliteDatabase_WhenCopySchemaFails_DisposesKeeperOnce()
    {
        var connection = new TrackingSqliteConnection();
        var copyFailure = new InvalidOperationException("copy failure");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestSqliteDatabase.Create(
                () => connection,
                _ => throw copyFailure));

        Assert.Same(copyFailure, error);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void TestSqliteDatabase_WhenConstructionFails_DisposesKeeperOnce()
    {
        var connection = new TrackingSqliteConnection();
        var constructionFailure = new InvalidOperationException("construction failure");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestSqliteDatabase.Create(
                () => connection,
                static _ => { },
                _ => throw constructionFailure));

        Assert.Same(constructionFailure, error);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task UnifiedSessionRoutesSpecs_WhenSeedFails_DisposesDatabaseOnce()
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var seedFailure = new InvalidOperationException("seed failure");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UnifiedSessionRoutesSpecs.BuildDbAsyncForTest(
                () => database,
                _ => Task.FromException(seedFailure)));

        Assert.Same(seedFailure, error);
        Assert.Equal(1, database.DisposeCount);
        Assert.Equal(ConnectionState.Closed, database.Keeper.State);
    }

    private sealed class TrackingSqliteConnection : SqliteConnection
    {
        private readonly Exception? _openFailure;

        public TrackingSqliteConnection(Exception? openFailure = null)
            : base("Data Source=:memory:")
        {
            _openFailure = openFailure;
        }

        public int DisposeCount { get; private set; }

        public override void Open()
        {
            if (_openFailure is not null)
                throw _openFailure;

            base.Open();
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
