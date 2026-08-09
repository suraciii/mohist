using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        Assert.Contains("20260902000000_AddRoutingRuleIdempotencyKey", applied);
    }

    [Fact]
    public void MigratedSqliteTemplate_WhenMigrationFails_DisposesConnection()
    {
        using var connection = new TrackingSqliteConnection();
        connection.Open();

        var error = Assert.Throws<InvalidOperationException>(() =>
            MigratedSqliteTemplate.CreateTemplate(
                () => connection,
                "migration-that-does-not-exist"));

        Assert.Contains("migration-that-does-not-exist", error.Message);
        Assert.True(connection.WasDisposed);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void TestSqliteDatabase_WhenOpenFails_DisposesKeeper()
    {
        var openFailure = new InvalidOperationException("open failure");
        using var connection = new TrackingSqliteConnection(openFailure);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestSqliteDatabase.Create(() => connection, static _ => { }));

        Assert.Same(openFailure, error);
        Assert.True(connection.WasDisposed);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void TestSqliteDatabase_WhenCopySchemaFails_DisposesKeeper()
    {
        using var connection = new TrackingSqliteConnection();
        var copyFailure = new InvalidOperationException("copy failure");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestSqliteDatabase.Create(
                () => connection,
                _ => throw copyFailure));

        Assert.Same(copyFailure, error);
        Assert.True(connection.WasDisposed);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private sealed class TrackingSqliteConnection : SqliteConnection
    {
        private readonly Exception? _openFailure;

        public TrackingSqliteConnection(Exception? openFailure = null)
            : base("Data Source=:memory:")
        {
            _openFailure = openFailure;
        }

        public bool WasDisposed { get; private set; }

        public override void Open()
        {
            if (_openFailure is not null)
                throw _openFailure;

            base.Open();
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
