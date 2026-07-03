using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Services;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Runner)]
public class TaskLogServiceSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskLogService _service;

    public TaskLogServiceSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"task-log-service-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var factory = new Factory(_options);
        _service = new TaskLogService(
            new TaskLogStore(factory, _timeProvider),
            new RunnerWorkStore(factory),
            new WorkflowRunQuerier(factory));

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
    public async Task AppendAsync_RejectsBatchOverEntryLimitBeforePersistence()
    {
        var entries = Enumerable.Range(1, TaskLogUploadLimits.MaxEntries + 1)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", "line"))
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.AppendAsync(
            "runner",
            TaskLogOwnershipKinds.Workflow,
            "owner",
            "work",
            entries,
            truncated: false));
    }

    [Fact]
    public async Task AppendAsync_RejectsBatchOverTotalTextLimitBeforePersistence()
    {
        var now = _timeProvider.GetUtcNow();
        var entries = Enumerable.Range(1, 31)
            .Select(seq => new TaskLogLine(seq, now, "action", new string('x', TaskLogUploadLimits.MaxTextLength)))
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.AppendAsync(
            "runner",
            TaskLogOwnershipKinds.Workflow,
            "owner",
            "work",
            entries,
            truncated: false));
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
