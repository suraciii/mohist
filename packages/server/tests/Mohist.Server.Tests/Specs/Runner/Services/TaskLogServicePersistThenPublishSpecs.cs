using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Services;

/// <summary>
/// Locks in the persist-then-publish contract of
/// <see cref="TaskLogService.AppendAsync"/>.
/// </summary>
[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.Runner)]
public class TaskLogServicePersistThenPublishSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskLogStore _store;
    private readonly RunnerWorkStore _runnerWorks;
    private readonly WorkflowRunQuerier _runQuerier;

    public TaskLogServicePersistThenPublishSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"task-log-ptp-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var factory = new Factory(_options);
        _store = new TaskLogStore(factory, _timeProvider);
        _runnerWorks = new RunnerWorkStore(factory);
        _runQuerier = new WorkflowRunQuerier(factory);

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
    public async Task AppendAsync_PersistsBeforePublishing()
    {
        // The persist-then-publish invariant must hold even when
        // the publisher throws: the authoritative log is already
        // committed, so a thrown envelope is logged and swallowed
        // without affecting persistence.
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-1");
        var publisher = new RecordingPublisher { ThrowOnPublish = new InvalidOperationException("simulated network drop") };
        var service = NewService(publisher);

        var entries = NewEntries(1, 3);
        var ok = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-1",
            entries,
            truncated: false);

        Assert.True(ok);
        Assert.NotEmpty(publisher.Published); // publisher WAS called

        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.AgentJob, "owner-1", "work-1",
            afterSeq: null, limit: null, default);
        Assert.Equal(3, page.Lines.Count);
    }

    [Fact]
    public async Task AppendAsync_PublisherThrows_PersistenceIsComplete()
    {
        // The publisher throws on every call — the service must
        // catch and continue, the work item must remain
        // outstanding, and the persisted log must contain the
        // complete batch (no rows dropped).
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-2");
        var publisher = new RecordingPublisher { ThrowOnPublish = new InvalidOperationException("kaboom") };
        var service = NewService(publisher);

        var entries = NewEntries(10, 5);
        var ok = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-2",
            entries,
            truncated: true);

        Assert.True(ok);
        Assert.Single(publisher.Published);

        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.AgentJob, "owner-1", "work-2",
            afterSeq: null, limit: null, default);
        Assert.Equal(5, page.Lines.Count);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task AppendAsync_NoSubscribers_PublisherCompletesAndPersistenceSucceeds()
    {
        // The on-demand / no-subscriber case: a publish call
        // against the in-process publisher completes without
        // throwing (no clients want this task), and the
        // authoritative store still has the full batch.
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-3");
        var publisher = new RecordingPublisher(); // no throw, just records
        var service = NewService(publisher);

        var entries = NewEntries(100, 2);
        var ok = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-3",
            entries,
            truncated: false);

        Assert.True(ok);
        Assert.Single(publisher.Published);
        Assert.Equal("owner-1", publisher.Published[0].OwnerId);
        Assert.Equal("work-3", publisher.Published[0].WorkId);
        Assert.Equal(2, publisher.Published[0].Entries.Count);

        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.AgentJob, "owner-1", "work-3",
            afterSeq: null, limit: null, default);
        Assert.Equal(2, page.Lines.Count);
    }

    [Fact]
    public async Task AppendAsync_WorkItemTerminal_PersistenceIsBypassed_NoPublish()
    {
        // Phase 1 contract: a non-Outstanding work item (e.g. a
        // Completed / Failed one) is rejected BEFORE any
        // persistence or publish attempt.
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-4");
        await _runnerWorks.TryMarkTerminalAsync(
            "runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-4",
            RunnerWorkStatus.Completed, "ok", _timeProvider.GetUtcNow());

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var ok = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-4",
            NewEntries(1, 1),
            truncated: false);

        Assert.False(ok);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AppendAsync_StampsTaskIdFromWorkflowRunState_OnWorkflowOwner()
    {
        // When the work item is owned by a workflow run, the
        // publisher's envelope must carry the resolved taskId
        // (so the Web can route the delta to the expanded task)
        // and the corresponding cache must produce a stable
        // stamp on subsequent appends without re-loading the
        // workflow-run state.
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.Workflow, "wf-1", "w-x");
        await SeedWorkflowRunAsync("wf-1", "task-X", "w-x");

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-1", "w-x",
            NewEntries(1, 1), truncated: false);
        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-1", "w-x",
            NewEntries(2, 1), truncated: false);

        Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, e => Assert.Equal("task-X", e.TaskId));
    }

    [Fact]
    public async Task AppendAsync_AgentJobOwner_StampsNullTaskId()
    {
        // Agent-job owned work has no taskId mapping; the
        // publisher envelopes null and the on-demand scope
        // filter treats that as "no fan-out".
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-5");
        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-5",
            NewEntries(1, 1), truncated: false);

        var envelope = Assert.Single(publisher.Published);
        Assert.Null(envelope.TaskId);
    }

    [Fact]
    public async Task AppendAsync_RejectsBatchOverEntryLimit_BeforeAnyPublishAttempt()
    {
        // Validation must happen BEFORE store.AppendAsync / publish,
        // so a malformed batch never produces a partial publish.
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-6");

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var entries = Enumerable.Range(1, TaskLogUploadLimits.MaxEntries + 1)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", "x"))
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-6", entries, truncated: false));

        Assert.Empty(publisher.Published);
    }

    private TaskLogService NewService(ITaskLogDeltaPublisher publisher)
    {
        return new TaskLogService(
            _store,
            _runnerWorks,
            _runQuerier,
            publisher,
            NullLogger<TaskLogService>.Instance);
    }

    private async Task SeedOutstandingWorkAsync(string runnerId, string ownerKind, string ownerId, string workId)
    {
        await _runnerWorks.InsertOutstandingAsync(new RunnerWork(
            RunnerId: runnerId,
            OwnerKind: ownerKind,
            OwnerId: ownerId,
            WorkId: workId,
            TakenAt: _timeProvider.GetUtcNow(),
            Status: RunnerWorkStatus.Outstanding));
    }

    private async Task SeedWorkflowRunAsync(string workflowRunId, string taskId, string workId)
    {
        var now = _timeProvider.GetUtcNow();
        var run = new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(Name: null, CreatedAt: now),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "stage-1",
            Stages = new List<StageRun>
            {
                new()
                {
                    Id = "stage-1",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Initialized = true,
                    Tasks = new List<TaskRun>
                    {
                        new()
                        {
                            Id = taskId,
                            DefinitionId = "def-1",
                            Attempt = 1,
                            Title = "do thing",
                            Status = TaskRunStatus.Running,
                            RunnerId = "runner-A",
                            WorkId = workId,
                        },
                    },
                },
            },
        };

        await using var db = new MohistDbContext(_options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<TaskLogLine> NewEntries(long startSeq, int count)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, count)
            .Select(i => new TaskLogLine(startSeq + i, now.AddMilliseconds(i), "stdout", $"line-{startSeq + i}"))
            .ToList();
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    private sealed class RecordingPublisher : ITaskLogDeltaPublisher
    {
        private readonly List<TaskLogDeltaEnvelope> _published = [];
        public IReadOnlyList<TaskLogDeltaEnvelope> Published => _published;
        public Exception? ThrowOnPublish { get; set; }

        public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default)
        {
            _published.Add(envelope);
            if (ThrowOnPublish is not null) throw ThrowOnPublish;
            return Task.CompletedTask;
        }
    }
}
