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
    private readonly TestSqliteDatabase _database;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskLogService _service;

    public TaskLogServiceSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(_database.Options);
        _service = new TaskLogService(
            new TaskLogStore(factory, _timeProvider),
            new RunnerWorkStore(factory),
            new WorkflowRunQuerier(factory),
            new NoopTaskLogDeltaPublisher(),
            NullLogger<TaskLogService>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _database.Dispose();
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

    private sealed class NoopTaskLogDeltaPublisher : ITaskLogDeltaPublisher
    {
        public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
