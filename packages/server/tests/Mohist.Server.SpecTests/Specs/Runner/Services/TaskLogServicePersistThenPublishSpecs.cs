using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

/// <summary>
/// Locks in the persist-then-publish contract of
/// <see cref="TaskLogService.AppendAsync"/>.
/// </summary>
public class TaskLogServicePersistThenPublishSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private readonly TestSqliteDatabase _database;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskLogStore _store;
    private readonly IAgentJobStore _agentJobs;
    private readonly FakeWorkflowRunWorkProjection _workProjection = new();

    public TaskLogServicePersistThenPublishSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(_database.Options);
        _store = new TaskLogStore(factory, _timeProvider);
        _agentJobs = new AgentJobStore(factory, NullLogger<AgentJobStore>.Instance, _timeProvider);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
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

        Assert.Equal(TaskLogAppendResult.Changed, ok);
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

        Assert.Equal(TaskLogAppendResult.Changed, ok);
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

        Assert.Equal(TaskLogAppendResult.Changed, ok);
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
    public async Task AppendAsync_NonTerminalBatchAfterAgentWorkSettles_ReturnsNotFoundAndIsNotPersisted()
    {
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-4");
        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.AgentJobs.SingleAsync(r => r.JobKey == "owner-1");
            row.State = "{\"status\":\"completed\",\"runnerId\":\"runner-A\",\"workId\":\"work-4\"}";
            await db.SaveChangesAsync();
        }

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var ok = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-4",
            NewEntries(1, 1),
            truncated: false,
            terminal: false);

        Assert.Equal(TaskLogAppendResult.NotFound, ok);
        Assert.Empty(publisher.Published);
        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-4",
            afterSeq: null,
            limit: null,
            default);
        Assert.Empty(page.Lines);

        var ledger = await _agentJobs.LoadLedgerAsync("owner-1");
        Assert.NotNull(ledger);
        Assert.Contains("\"status\":\"completed\"", ledger!.StateJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendAsync_TerminalBatchIsAcceptedAfterAgentWorkSettles()
    {
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-terminal");
        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.AgentJobs.SingleAsync(r => r.JobKey == "owner-1");
            row.State = "{\"status\":\"completed\",\"runnerId\":\"runner-A\",\"workId\":\"work-terminal\"}";
            await db.SaveChangesAsync();
        }

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);
        var entries = NewEntries(1, 2);

        var accepted = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-terminal",
            entries,
            truncated: false,
            terminal: true);
        var duplicate = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-terminal",
            entries,
            truncated: false,
            terminal: true);
        var different = await service.AppendAsync(
            "runner-A",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-terminal",
            NewEntries(2, 1),
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.Changed, accepted);
        Assert.Equal(TaskLogAppendResult.Duplicate, duplicate);
        Assert.Equal(TaskLogAppendResult.Conflict, different);
        Assert.Single(publisher.Published);
        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-terminal",
            afterSeq: null,
            limit: null,
            default);
        Assert.Equal([1, 2], page.Lines.Select(line => line.Seq).ToArray());

        var ledger = await _agentJobs.LoadLedgerAsync("owner-1");
        Assert.NotNull(ledger);
        Assert.Contains("\"status\":\"completed\"", ledger!.StateJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendAsync_TerminalBatchFromAnotherRunnerReturnsNotFound()
    {
        await SeedOutstandingWorkAsync("runner-A", TaskLogOwnershipKinds.AgentJob, "owner-1", "work-terminal-owner");
        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.AgentJobs.SingleAsync(r => r.JobKey == "owner-1");
            row.State = "{\"status\":\"completed\",\"runnerId\":\"runner-A\",\"workId\":\"work-terminal-owner\"}";
            await db.SaveChangesAsync();
        }

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var accepted = await service.AppendAsync(
            "runner-B",
            TaskLogOwnershipKinds.AgentJob,
            "owner-1",
            "work-terminal-owner",
            NewEntries(1, 1),
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.NotFound, accepted);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AppendAsync_StampsTaskIdAndProjectIdFromWorkProjection_OnWorkflowOwner()
    {
        // When the work item is owned by a workflow run, the
        // publisher's envelope must carry the resolved taskId
        // so the Web can route the delta to the expanded task.
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
        Assert.All(publisher.Published, e => Assert.Equal("proj-1", e.ProjectId));
    }

    [Fact]
    public async Task AppendAsync_ResolvesTaskIdFromCurrentWorkProjection_WhenWorkMappingChanges()
    {
        await SeedWorkflowRunAsync("wf-remap", "task-old", "w-reused");

        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-remap", "w-reused",
            NewEntries(1, 1), truncated: false);

        await SeedWorkflowRunAsync("wf-remap", "task-new", "w-reused");

        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-remap", "w-reused",
            NewEntries(2, 1), truncated: false);

        Assert.Collection(
            publisher.Published,
            first => Assert.Equal("task-old", first.TaskId),
            second => Assert.Equal("task-new", second.TaskId));
    }

    [Fact]
    public async Task AppendAsync_RejectsWorkflowWorkUntilItIsActive()
    {
        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var beforeActivation = await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-missing-first", "w-late",
            NewEntries(1, 1), truncated: false);

        await SeedWorkflowRunAsync("wf-missing-first", "task-late", "w-late");

        var afterActivation = await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-missing-first", "w-late",
            NewEntries(2, 1), truncated: false);

        Assert.Equal(TaskLogAppendResult.NotFound, beforeActivation);
        Assert.Equal(TaskLogAppendResult.Changed, afterActivation);
        var envelope = Assert.Single(publisher.Published);
        Assert.Equal("task-late", envelope.TaskId);
    }

    [Fact]
    public async Task AppendAsync_WorkflowWorkFromAnotherRunner_ReturnsFalse()
    {
        await SeedWorkflowRunAsync("wf-owned", "task-1", "w-1");
        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var ok = await service.AppendAsync(
            "runner-B", TaskLogOwnershipKinds.Workflow, "wf-owned", "w-1",
            NewEntries(1, 1), truncated: false);

        Assert.Equal(TaskLogAppendResult.NotFound, ok);
        Assert.Empty(publisher.Published);
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

    [Theory]
    [InlineData("checks-build")]
    [InlineData("unmapped-work")]
    public async Task AppendAsync_UnmappableWorkflowWork_PersistsWithoutPublishScope(string workId)
    {
        _workProjection.SetActiveWorkflow("wf-unmapped", workId, "runner-A", "proj-1");
        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        var ok = await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-unmapped", workId,
            NewEntries(1, 1), truncated: false);

        Assert.Equal(TaskLogAppendResult.Changed, ok);
        var envelope = Assert.Single(publisher.Published);
        Assert.Null(envelope.TaskId);
        Assert.Null(envelope.ProjectId);
        Assert.Equal(0, _workProjection.ProjectIdLookups);

        var page = await _store.QueryAsync(
            TaskLogOwnershipKinds.Workflow, "wf-unmapped", workId,
            afterSeq: null, limit: null, default);
        Assert.Single(page.Lines);
    }

    [Fact]
    public async Task QueryByTaskIdAsync_UsesWorkProjection_AndReturnsNullForMiss()
    {
        await SeedWorkflowRunAsync("wf-query", "task-query", "work-query");
        var publisher = new RecordingPublisher();
        var service = NewService(publisher);

        await service.AppendAsync(
            "runner-A", TaskLogOwnershipKinds.Workflow, "wf-query", "work-query",
            NewEntries(1, 2), truncated: true);

        var page = await service.QueryByTaskIdAsync("wf-query", "task-query", null, null);
        Assert.NotNull(page);
        Assert.Equal(2, page.Lines.Count);
        Assert.True(page.Truncated);

        Assert.Null(await service.QueryByTaskIdAsync("wf-query", "missing-task", null, null));
        Assert.Null(await service.QueryByTaskIdAsync("missing-run", "task-query", null, null));
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
            _agentJobs,
            _workProjection,
            publisher,
            NullLogger<TaskLogService>.Instance);
    }

    private async Task SeedOutstandingWorkAsync(string runnerId, string ownerKind, string ownerId, string workId)
    {
        Assert.Equal(TaskLogOwnershipKinds.AgentJob, ownerKind);
        var now = _timeProvider.GetUtcNow();
        await _agentJobs.InsertLedgerAsync(new AgentJobLedgerRecord(
            JobKey: ownerId,
            StateJson: $"{{\"status\":\"running\",\"runnerId\":\"{runnerId}\",\"workId\":\"{workId}\"}}",
            Revision: 0,
            AssignedRunnerId: runnerId,
            WorkId: workId,
            ReadySince: null,
            RunningSince: now,
            DispatchJson: null,
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            IssueProjectId: null,
            IssueNumber: null,
            AgentSessionId: null,
            InitialInputId: null,
            InitialTurnId: null));
    }

    private Task SeedWorkflowRunAsync(string workflowRunId, string taskId, string workId)
    {
        _workProjection.SetWorkflow(workflowRunId, taskId, workId, "runner-A", "proj-1");
        return Task.CompletedTask;
    }

    private static IReadOnlyList<TaskLogLine> NewEntries(long startSeq, int count)
    {
        var now = FixedNow;
        return Enumerable.Range(0, count)
            .Select(i => new TaskLogLine(startSeq + i, now.AddMilliseconds(i), "stdout", $"line-{startSeq + i}"))
            .ToList();
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

    private sealed class FakeWorkflowRunWorkProjection : IWorkflowRunWorkProjection
    {
        private readonly Dictionary<(string RunId, string TaskId), string> _taskToWork = [];
        private readonly Dictionary<(string RunId, string WorkId), string> _workToTask = [];
        private readonly Dictionary<string, (string WorkId, string RunnerId)> _active = [];
        private readonly Dictionary<string, string?> _projectIds = [];

        public int ProjectIdLookups { get; private set; }

        public void SetWorkflow(string runId, string taskId, string workId, string runnerId, string projectId)
        {
            foreach (var key in _taskToWork.Keys.Where(key => key.RunId == runId).ToList())
                _taskToWork.Remove(key);
            foreach (var key in _workToTask.Keys.Where(key => key.RunId == runId).ToList())
                _workToTask.Remove(key);

            _taskToWork[(runId, taskId)] = workId;
            _workToTask[(runId, workId)] = taskId;
            SetActiveWorkflow(runId, workId, runnerId, projectId);
        }

        public void SetActiveWorkflow(string runId, string workId, string runnerId, string projectId)
        {
            _active[runId] = (workId, runnerId);
            _projectIds[runId] = projectId;
        }

        public Task<string?> ResolveWorkIdAsync(string workflowRunId, string taskId, CancellationToken ct = default) =>
            Task.FromResult(_taskToWork.TryGetValue((workflowRunId, taskId), out var workId) ? workId : null);

        public Task<string?> ResolveTaskIdAsync(string workflowRunId, string workId, CancellationToken ct = default) =>
            Task.FromResult(_workToTask.TryGetValue((workflowRunId, workId), out var taskId) ? taskId : null);

        public Task<bool> IsActiveWorkAsync(string workflowRunId, string workId, string runnerId, CancellationToken ct = default) =>
            Task.FromResult(_active.TryGetValue(workflowRunId, out var active)
                && active.WorkId == workId
                && active.RunnerId == runnerId);

        public Task<string?> GetProjectIdAsync(string workflowRunId, CancellationToken ct = default)
        {
            ProjectIdLookups++;
            return Task.FromResult(_projectIds.TryGetValue(workflowRunId, out var projectId) ? projectId : null);
        }
    }
}
