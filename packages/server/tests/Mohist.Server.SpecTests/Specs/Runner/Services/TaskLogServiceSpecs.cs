using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
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
            new AgentJobStore(factory, NullLogger<AgentJobStore>.Instance, _timeProvider),
            new WorkflowRunWorkProjection(factory),
            new NoopTaskLogDeltaPublisher(),
            NullLogger<TaskLogService>.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
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

    [Fact]
    public async Task AppendAsync_WorkflowTerminalSnapshotSurvivesSettledRunAndIsIdempotent()
    {
        const string workflowRunId = "wr-terminal-task-log";
        const string workId = "work-terminal-task-log";
        const string runnerId = "runner-terminal-task-log";
        await InsertTerminalWorkflowRunAsync(workflowRunId, workId, runnerId);

        var publisher = new RecordingTaskLogDeltaPublisher();
        var service = NewService(publisher);
        IReadOnlyList<TaskLogLine> entries = [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "complete")];

        var changed = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            entries,
            truncated: false,
            terminal: true);
        var duplicate = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            entries,
            truncated: false,
            terminal: true);
        var conflict = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "different")],
            truncated: false,
            terminal: true);
        var nonterminal = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            entries,
            truncated: false,
            terminal: false);
        var wrongRunner = await service.AppendAsync(
            "runner-other",
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            entries,
            truncated: false,
            terminal: true);
        var wrongWork = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            "work-other",
            entries,
            truncated: false,
            terminal: true);
        var previousWork = await service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            "work-previous",
            entries,
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.Changed, changed);
        Assert.Equal(TaskLogAppendResult.Duplicate, duplicate);
        Assert.Equal(TaskLogAppendResult.Conflict, conflict);
        Assert.Equal(TaskLogAppendResult.NotFound, nonterminal);
        Assert.Equal(TaskLogAppendResult.NotFound, wrongRunner);
        Assert.Equal(TaskLogAppendResult.NotFound, wrongWork);
        Assert.Equal(TaskLogAppendResult.NotFound, previousWork);
        Assert.Single(publisher.Published);

        await using var db = new MohistDbContext(_database.Options);
        var run = await db.WorkflowRuns.AsNoTracking()
            .SingleAsync(row => row.WorkflowRunId == workflowRunId);
        Assert.Null(run.ActiveWorkId);
        Assert.Null(run.ActiveWorkerId);
        Assert.Equal(WorkflowRunStatus.Completed, JSON.Deserialize<WorkflowRun>(run.State)!.Status);
    }

    [Fact]
    public async Task AppendAsync_WorkflowTerminalSnapshotRequiresAuthoritativeTerminalTask()
    {
        const string workflowRunId = "wr-active-terminal-task-log";
        const string workId = "work-active-terminal-task-log";
        const string runnerId = "runner-active-terminal-task-log";
        await InsertActiveWorkflowRunAsync(workflowRunId, workId, runnerId);

        var result = await _service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            workId,
            [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "too-early")],
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.NotFound, result);
    }

    [Fact]
    public async Task AppendAsync_WorkflowTerminalSnapshotRequiresRecordedOwnership_WhenTasksWereRetried()
    {
        const string workflowRunId = "wr-terminal-task-log-retries";
        const string runnerId = "runner-terminal-task-log-retries";
        const string requestedWorkId = "work-retry-2";

        await using (var db = new MohistDbContext(_database.Options))
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = workflowRunId,
                State = JSON.Serialize(new WorkflowRun
                {
                    Id = workflowRunId,
                    Metadata = new WorkflowRunMetadata("retried", _timeProvider.GetUtcNow()),
                    Status = WorkflowRunStatus.Completed,
                    CurrentStageId = "build",
                    Stages =
                    [
                        new StageRun
                        {
                            Id = "build",
                            Attempt = 1,
                            RequiresApproval = false,
                            Status = StageRunStatus.Completed,
                            Tasks =
                            [
                                new TaskRun
                                {
                                    Id = "task-retry-1",
                                    DefinitionId = "task-build",
                                    Attempt = 1,
                                    Title = "First attempt",
                                    WorkId = "work-retry-1",
                                    WorkerId = runnerId,
                                    Status = TaskRunStatus.Failed,
                                },
                                new TaskRun
                                {
                                    Id = "task-retry-2",
                                    DefinitionId = "task-build",
                                    Attempt = 2,
                                    Title = "Retry attempt",
                                    WorkId = requestedWorkId,
                                    WorkerId = runnerId,
                                    Status = TaskRunStatus.Completed,
                                },
                            ],
                        },
                    ],
                }),
            });
            await db.SaveChangesAsync();
        }

        var result = await _service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            requestedWorkId,
            [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "unrecorded")],
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.NotFound, result);
    }

    [Fact]
    public async Task AppendAsync_StoppedWorkflowWithMultipleInterruptedTasks_UsesRecordedOwner()
    {
        const string workflowRunId = "wr-stopped-task-log-multiple";
        const string runnerId = "runner-stopped-task-log-multiple";
        const string recordedWorkId = "work-interrupted-1";
        const string unrecordedWorkId = "work-interrupted-2";

        await using (var db = new MohistDbContext(_database.Options))
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = workflowRunId,
                State = JSON.Serialize(new WorkflowRun
                {
                    Id = workflowRunId,
                    Metadata = new WorkflowRunMetadata("stopped", _timeProvider.GetUtcNow()),
                    Status = WorkflowRunStatus.Stopped,
                    CurrentStageId = "build",
                    Stages =
                    [
                        new StageRun
                        {
                            Id = "build",
                            Attempt = 1,
                            RequiresApproval = false,
                            Status = StageRunStatus.Running,
                            Tasks =
                            [
                                new TaskRun
                                {
                                    Id = "task-interrupted-1",
                                    DefinitionId = "task-1",
                                    Attempt = 1,
                                    Title = "Interrupted first",
                                    WorkId = recordedWorkId,
                                    WorkerId = runnerId,
                                    Status = TaskRunStatus.Cancelled,
                                },
                                new TaskRun
                                {
                                    Id = "task-interrupted-2",
                                    DefinitionId = "task-2",
                                    Attempt = 1,
                                    Title = "Interrupted second",
                                    WorkId = unrecordedWorkId,
                                    WorkerId = runnerId,
                                    Status = TaskRunStatus.Running,
                                },
                            ],
                        },
                    ],
                }),
            });
            db.TerminalLogOwnerships.Add(new TerminalLogOwnershipRow
            {
                OwnerKind = TerminalLogOwnerKinds.Workflow,
                OwnerId = workflowRunId,
                WorkId = recordedWorkId,
                RunnerId = runnerId,
            });
            await db.SaveChangesAsync();
        }

        var recorded = await _service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            recordedWorkId,
            [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "recorded")],
            truncated: false,
            terminal: true);
        var unrecorded = await _service.AppendAsync(
            runnerId,
            TaskLogOwnershipKinds.Workflow,
            workflowRunId,
            unrecordedWorkId,
            [new TaskLogLine(1, _timeProvider.GetUtcNow(), "terminal", "missing")],
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.Changed, recorded);
        Assert.Equal(TaskLogAppendResult.NotFound, unrecorded);
    }

    private TaskLogService NewService(ITaskLogDeltaPublisher publisher) => new(
        new TaskLogStore(new TestDbContextFactory(_database.Options), _timeProvider),
        new AgentJobStore(new TestDbContextFactory(_database.Options), NullLogger<AgentJobStore>.Instance, _timeProvider),
        new WorkflowRunWorkProjection(new TestDbContextFactory(_database.Options)),
        publisher,
        NullLogger<TaskLogService>.Instance);

    private async Task InsertTerminalWorkflowRunAsync(string workflowRunId, string workId, string runnerId)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(new WorkflowRun
            {
                Id = workflowRunId,
                Metadata = new WorkflowRunMetadata("terminal", _timeProvider.GetUtcNow()),
                Status = WorkflowRunStatus.Completed,
                CurrentStageId = "build",
                Assignment = new WorkflowAssignment(runnerId, _timeProvider.GetUtcNow()),
                Stages =
                [
                    new StageRun
                    {
                        Id = "build",
                        Attempt = 1,
                        RequiresApproval = false,
                        Status = StageRunStatus.Completed,
                        Tasks =
                        [
                            new TaskRun
                            {
                                Id = "task-previous",
                                DefinitionId = "task-previous",
                                Attempt = 1,
                                Title = "Previous task",
                                Uses = "core/script",
                                WorkId = "work-previous",
                                WorkerId = runnerId,
                                Status = TaskRunStatus.Completed,
                                Classification = TaskClassification.Orchestration,
                            },
                            new TaskRun
                            {
                                Id = "task-terminal",
                                DefinitionId = "task-terminal",
                                Attempt = 1,
                                Title = "Terminal task",
                                Uses = "core/script",
                                WorkId = workId,
                                WorkerId = runnerId,
                                Status = TaskRunStatus.Completed,
                                TerminalLogOwnership = new TerminalLogOwnership(
                                    TerminalLogOwnerKinds.Workflow,
                                    workflowRunId,
                                    workId,
                                    runnerId),
                                Classification = TaskClassification.Orchestration,
                            },
                        ],
                    },
                ],
            }),
        });
        db.TerminalLogOwnerships.Add(new TerminalLogOwnershipRow
        {
            OwnerKind = TerminalLogOwnerKinds.Workflow,
            OwnerId = workflowRunId,
            WorkId = workId,
            RunnerId = runnerId,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertActiveWorkflowRunAsync(string workflowRunId, string workId, string runnerId)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(new WorkflowRun
            {
                Id = workflowRunId,
                Metadata = new WorkflowRunMetadata("active", _timeProvider.GetUtcNow()),
                Status = WorkflowRunStatus.Running,
                CurrentStageId = "build",
                Assignment = new WorkflowAssignment(runnerId, _timeProvider.GetUtcNow()),
                Stages =
                [
                    new StageRun
                    {
                        Id = "build",
                        Attempt = 1,
                        RequiresApproval = false,
                        Status = StageRunStatus.Running,
                        Tasks =
                        [
                            new TaskRun
                            {
                                Id = "task-active",
                                DefinitionId = "task-active",
                                Attempt = 1,
                                Title = "Active task",
                                Uses = "core/script",
                                WorkId = workId,
                                WorkerId = runnerId,
                                Status = TaskRunStatus.Running,
                                Classification = TaskClassification.Orchestration,
                            },
                        ],
                    },
                ],
            }),
            ActiveWorkId = workId,
            ActiveWorkerId = runnerId,
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingTaskLogDeltaPublisher : ITaskLogDeltaPublisher
    {
        public List<TaskLogDeltaEnvelope> Published { get; } = [];

        public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopTaskLogDeltaPublisher : ITaskLogDeltaPublisher
    {
        public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
