using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Data;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Runner)]
public class RunnerDefinitionStoreSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly RunnerDefinitionStore _store;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public RunnerDefinitionStoreSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"runner-definition-store-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _store = new RunnerDefinitionStore(new Factory(_options), _timeProvider);

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = new MohistDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task GetOrInitAsync_UnknownRunner_InitializesSlotsToOneAndPersists()
    {
        var runnerId = $"runner-init-{Guid.NewGuid():N}";

        var slots = await _store.GetOrInitAsync(runnerId);

        Assert.Equal(1, slots);

        await using var db = new MohistDbContext(_options);
        var row = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId);
        Assert.NotNull(row);
        Assert.Equal(1, row.Slots);
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task GetOrInitAsync_KnownRunner_ReturnsPersistedSlotsAndDoesNotOverwrite()
    {
        var runnerId = $"runner-known-{Guid.NewGuid():N}";
        await _store.GetOrInitAsync(runnerId);
        await _store.UpdateSlotsAsync(runnerId, 5);

        var slots = await _store.GetOrInitAsync(runnerId);

        Assert.Equal(5, slots);

        await using var db = new MohistDbContext(_options);
        var row = await db.Runners.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runnerId);
        Assert.NotNull(row);
        Assert.Equal(5, row.Slots);
    }

    [Fact]
    public async Task GetOrInitAsync_CalledTwiceForUnknownRunner_IsIdempotent()
    {
        var runnerId = $"runner-twice-{Guid.NewGuid():N}";

        var first = await _store.GetOrInitAsync(runnerId);
        var second = await _store.GetOrInitAsync(runnerId);

        Assert.Equal(first, second);

        await using var db = new MohistDbContext(_options);
        var rows = await db.Runners.AsNoTracking().Where(r => r.Id == runnerId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(RunnerDefinitionStore.DefaultSlots, rows[0].Slots);
    }

    [Fact]
    public async Task UpdateSlotsAsync_UpdatesSlotsAndBumpsUpdatedAt()
    {
        var runnerId = $"runner-update-{Guid.NewGuid():N}";
        await _store.GetOrInitAsync(runnerId);

        await using (var db = new MohistDbContext(_options))
        {
            var initial = await db.Runners.AsNoTracking().FirstAsync(r => r.Id == runnerId);
            Assert.Equal(RunnerDefinitionStore.DefaultSlots, initial.Slots);
            Assert.Equal(initial.CreatedAt, initial.UpdatedAt);
        }

        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        await _store.UpdateSlotsAsync(runnerId, 7);

        await using (var db = new MohistDbContext(_options))
        {
            var after = await db.Runners.AsNoTracking().FirstAsync(r => r.Id == runnerId);
            Assert.Equal(7, after.Slots);
            Assert.True(after.UpdatedAt > after.CreatedAt,
                $"UpdatedAt ({after.UpdatedAt:O}) should be greater than CreatedAt ({after.CreatedAt:O})");
        }
    }

    [Fact]
    public async Task UpdateSlotsAsync_NonPositive_Throws()
    {
        var runnerId = $"runner-zero-{Guid.NewGuid():N}";
        await _store.GetOrInitAsync(runnerId);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.UpdateSlotsAsync(runnerId, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.UpdateSlotsAsync(runnerId, -3));

        await using var db = new MohistDbContext(_options);
        var row = await db.Runners.AsNoTracking().FirstAsync(r => r.Id == runnerId);
        Assert.Equal(RunnerDefinitionStore.DefaultSlots, row.Slots);
    }

    [Fact]
    public async Task UpdateSlotsAsync_UnknownRunner_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.UpdateSlotsAsync($"runner-missing-{Guid.NewGuid():N}", 2));
    }

    [Fact]
    public async Task GetOrInitAsync_EmptyRunnerId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetOrInitAsync(string.Empty));
    }

    [Fact]
    public async Task UpdateSlotsAsync_RoundTrip_PersistsAcrossNewStoreInstance()
    {
        var runnerId = $"runner-roundtrip-{Guid.NewGuid():N}";
        await _store.GetOrInitAsync(runnerId);
        await _store.UpdateSlotsAsync(runnerId, 4);

        await using var verifyDb = new MohistDbContext(_options);
        var persisted = await verifyDb.Runners.AsNoTracking().FirstAsync(r => r.Id == runnerId);
        Assert.Equal(4, persisted.Slots);

        var freshStore = new RunnerDefinitionStore(new Factory(_options), _timeProvider);
        var slots = await freshStore.GetOrInitAsync(runnerId);
        Assert.Equal(4, slots);
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
