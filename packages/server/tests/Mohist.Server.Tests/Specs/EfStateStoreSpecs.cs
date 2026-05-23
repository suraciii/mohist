using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class EfStateStoreSpecs : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<MohistDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new MohistDbContext(options);
        await db.Database.EnsureCreatedAsync();

        _factory = new TestDbContextFactory(options);
    }

    public async Task DisposeAsync()
    {
        await _connection.CloseAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Save_Load_ReturnsState()
    {
        var store = new EfStateStore<TestState>(_factory);
        var state = new TestState { Name = "hello", Value = 42 };

        await store.SaveAsync("key-1", state);
        var loaded = await store.LoadAsync("key-1");

        Assert.NotNull(loaded);
        Assert.Equal("hello", loaded!.Name);
        Assert.Equal(42, loaded.Value);
    }

    [Fact]
    public async Task Load_MissingKey_ReturnsNull()
    {
        var store = new EfStateStore<TestState>(_factory);
        var loaded = await store.LoadAsync("missing");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_Overwrite_UpdatesState()
    {
        var store = new EfStateStore<TestState>(_factory);
        await store.SaveAsync("key-2", new TestState { Name = "v1" });
        await store.SaveAsync("key-2", new TestState { Name = "v2" });

        var loaded = await store.LoadAsync("key-2");
        Assert.Equal("v2", loaded!.Name);
    }

    [Fact]
    public async Task Delete_RemovesState()
    {
        var store = new EfStateStore<TestState>(_factory);
        await store.SaveAsync("key-3", new TestState { Name = "x" });
        await store.DeleteAsync("key-3");

        var loaded = await store.LoadAsync("key-3");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task MultipleTypes_SameKey_Isolated()
    {
        var storeA = new EfStateStore<TestState>(_factory);
        var storeB = new EfStateStore<OtherState>(_factory);

        await storeA.SaveAsync("shared", new TestState { Name = "A" });
        await storeB.SaveAsync("shared", new OtherState { Label = "B" });

        var loadedA = await storeA.LoadAsync("shared");
        var loadedB = await storeB.LoadAsync("shared");

        Assert.Equal("A", loadedA!.Name);
        Assert.Equal("B", loadedB!.Label);
    }

    private class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }

    private class TestState
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private class OtherState
    {
        public string Label { get; set; } = "";
    }
}
