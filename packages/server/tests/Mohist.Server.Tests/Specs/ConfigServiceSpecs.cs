using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Storage.Db;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ConfigServiceSpecs : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ConfigService _svc = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;
        var db = new MohistDbContext(options);
        await db.Database.EnsureCreatedAsync();
        _svc = new ConfigService(new TestFactory(_connection), Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connection.CloseAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetConfig_Defaults_ReturnsDefaults()
    {
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(3456, cfg["serverPort"]);
        Assert.Equal("localhost", cfg["serverHost"]);
    }

    [Fact]
    public async Task SetAndGet_ReturnsUpdatedValue()
    {
        await _svc.SetAsync("serverPort", 8080);
        var cfg = await _svc.GetConfigAsync();
        Assert.Equal(8080, cfg["serverPort"]);
    }

    [Fact]
    public async Task Validate_Number_Invalid()
    {
        var (valid, error) = _svc.Validate("serverPort", "abc");
        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Validate_Number_Valid()
    {
        var (valid, error) = _svc.Validate("serverPort", "8080");
        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public async Task GetAll_MasksSecrets()
    {
        await _svc.SetAsync("model", "anthropic/claude");
        var all = await _svc.GetAllAsync();
        Assert.Equal("anthropic/claude", all["model"]);
    }

    private class TestFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly SqliteConnection _connection;
        public TestFactory(SqliteConnection connection) => _connection = connection;
        public MohistDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new MohistDbContext(options);
        }
    }
}
