using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public class TaskLogServiceSpecs : IAsyncLifetime
{
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskLogService _service;
    private readonly SqliteConnection _keeper;

    public TaskLogServiceSpecs()
    {
        var connectionString = $"Data Source=task-log-service-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var factory = new Factory(_options);
        _service = new TaskLogService(
            new TaskLogStore(factory, _timeProvider),
            new RunnerWorkStore(factory),
            new WorkflowRunQuerier(factory),
            new NoopTaskLogDeltaPublisher(),
            NullLogger<TaskLogService>.Instance);

        MigratedSqliteTemplate.CopyTo(_keeper);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
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

    private sealed class NoopTaskLogDeltaPublisher : ITaskLogDeltaPublisher
    {
        public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
