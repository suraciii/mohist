using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Data;

public partial class TaskLogStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly TaskLogStore _store;
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public TaskLogStoreSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _store = new TaskLogStore(new TestDbContextFactory(_database.Options), _timeProvider);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AppendAsync_PersistsEntriesAndBatchMetadata()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var entries = new List<TaskLogLine>
        {
            new(1, _timeProvider.GetUtcNow(), "workspace-prep", "Cloning repo..."),
            new(2, _timeProvider.GetUtcNow(), "workspace-prep", "Checkout done"),
        };

        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated: false);

        await using var db = new MohistDbContext(_database.Options);
        var rows = await db.TaskLogEntries.AsNoTracking()
            .Where(e => e.OwnerKind == ownerKind && e.OwnerId == ownerId && e.WorkId == workId)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Seq);
        Assert.Equal(2, rows[1].Seq);
        Assert.Equal("workspace-prep", rows[0].Source);
        Assert.Equal("Checkout done", rows[1].Text);

        var batch = await db.TaskLogBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.OwnerKind == ownerKind && b.OwnerId == ownerId && b.WorkId == workId);
        Assert.NotNull(batch);
        Assert.False(batch!.Truncated);
        Assert.Equal(_timeProvider.GetUtcNow(), batch.UploadedAt);
    }

    [Fact]
    public async Task QueryAsync_OrdersEntriesByAscendingSeq()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var now = _timeProvider.GetUtcNow();
        var entries = new List<TaskLogLine>
        {
            new(1, now, "workspace-prep", "Cloning"),
            new(2, now.AddSeconds(1), "branch-check", "Stable"),
            new(3, now.AddSeconds(2), "action:rebase", "Applying: fix-bug"),
        };

        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated: false);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);

        Assert.Equal(3, page.Lines.Count);
        Assert.Equal(1, page.Lines[0].Seq);
        Assert.Equal(2, page.Lines[1].Seq);
        Assert.Equal(3, page.Lines[2].Seq);
        Assert.Equal("workspace-prep", page.Lines[0].Source);
        Assert.Equal(now, page.Lines[0].Timestamp);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task QueryAsync_CursorPaginatesAndReturnsNullCursorAtEnd()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var entries = Enumerable.Range(1, 7)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", $"line {seq}"))
            .ToList();

        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated: false);

        var first = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 3);
        Assert.Equal(3, first.Lines.Count);
        Assert.Equal(1, first.Lines[0].Seq);
        Assert.Equal(3, first.Lines[2].Seq);
        Assert.Equal(3L, first.NextCursor);

        var second = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: 3, limit: 3);
        Assert.Equal(3, second.Lines.Count);
        Assert.Equal(4, second.Lines[0].Seq);
        Assert.Equal(6, second.Lines[2].Seq);
        Assert.Equal(6L, second.NextCursor);

        var final = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: 6, limit: 3);
        Assert.Single(final.Lines);
        Assert.Equal(7, final.Lines[0].Seq);
        Assert.Null(final.NextCursor);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyPageForUnknownWorkItem()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";

        var page = await _store.QueryAsync(ownerKind, ownerId, "missing-work", afterSeq: null, limit: 10);

        Assert.Empty(page.Lines);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task QueryAsync_ReportsTruncatedFlagFromStoredBatch()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var entries = Enumerable.Range(1, 3)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", $"tail {seq}"))
            .ToList();

        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated: true);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);
        Assert.True(page.Truncated);
        Assert.Equal(3, page.Lines.Count);
    }

    [Fact]
    public async Task AppendAsync_SecondIncrementalBatchKeepsEarlierLines()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        var first = new List<TaskLogLine>
        {
            new(1, _timeProvider.GetUtcNow(), "workspace-prep", "first"),
            new(2, _timeProvider.GetUtcNow(), "workspace-prep", "second"),
        };
        await _store.AppendAsync(ownerKind, ownerId, workId, first, truncated: false);

        _timeProvider.Advance(TimeSpan.FromSeconds(5));

        var second = new List<TaskLogLine>
        {
            new(3, _timeProvider.GetUtcNow(), "action", "third"),
            new(4, _timeProvider.GetUtcNow(), "action", "fourth"),
        };
        await _store.AppendAsync(ownerKind, ownerId, workId, second, truncated: true);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);
        Assert.Equal([1, 2, 3, 4], page.Lines.Select(l => l.Seq).ToArray());
        Assert.Equal(["first", "second", "third", "fourth"], page.Lines.Select(l => l.Text).ToArray());
        Assert.True(page.Truncated);

        await using var db = new MohistDbContext(_database.Options);
        var batchCount = await db.TaskLogBatches.AsNoTracking()
            .CountAsync(b => b.OwnerKind == ownerKind && b.OwnerId == ownerId && b.WorkId == workId);
        Assert.Equal(1, batchCount);
    }

    [Fact]
    public async Task AppendAsync_TerminalBatchReconcilesOverlappingIncrementalSeqs()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        await _store.AppendAsync(ownerKind, ownerId, workId,
            Enumerable.Range(1, 10)
                .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "incremental", $"incremental-{seq}"))
                .ToList(),
            truncated: false);

        var terminalNow = _timeProvider.GetUtcNow().AddSeconds(10);
        var terminalEntries = Enumerable.Range(1, 15)
            .Select(seq => new TaskLogLine(seq, terminalNow, "terminal", $"terminal-{seq}"))
            .ToList();
        await _store.AppendAsync(ownerKind, ownerId, workId,
            terminalEntries,
            truncated: true,
            terminal: true);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 20);

        Assert.Equal(Enumerable.Range(1, 15).Select(i => (long)i).ToArray(), page.Lines.Select(l => l.Seq).ToArray());
        Assert.Equal(15, page.Lines.Select(l => l.Seq).Distinct().Count());
        Assert.All(page.Lines, line => Assert.Equal("terminal", line.Source));
        Assert.All(page.Lines, line => Assert.Equal(terminalNow, line.Timestamp));
        Assert.Equal("terminal-1", page.Lines[0].Text);
        Assert.Equal("terminal-15", page.Lines[^1].Text);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task AppendAsync_TerminalReceiptReturnsConflictWithoutChangingRows()
    {
        var ownerKind = "agent-job";
        var ownerId = $"aj-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var first = Enumerable.Range(1, 2)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "terminal", $"line-{seq}"))
            .ToList();

        var changed = await _store.AppendAsync(ownerKind, ownerId, workId, first, truncated: false, terminal: true);
        var duplicate = await _store.AppendAsync(ownerKind, ownerId, workId, first, truncated: false, terminal: true);
        var conflict = await _store.AppendAsync(
            ownerKind,
            ownerId,
            workId,
            [first[1]],
            truncated: false,
            terminal: true);

        Assert.Equal(TaskLogAppendResult.Changed, changed);
        Assert.Equal(TaskLogAppendResult.Duplicate, duplicate);
        Assert.Equal(TaskLogAppendResult.Conflict, conflict);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);
        Assert.Equal([1, 2], page.Lines.Select(line => line.Seq).ToArray());
        Assert.Equal(["line-1", "line-2"], page.Lines.Select(line => line.Text).ToArray());

        await using var db = new MohistDbContext(_database.Options);
        var batch = await db.TaskLogBatches.AsNoTracking()
            .SingleAsync(b => b.OwnerKind == ownerKind && b.OwnerId == ownerId && b.WorkId == workId);
        Assert.True(batch.Terminal);
        Assert.NotNull(batch.TerminalDigest);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentTerminalUploadsResolveToOneReceipt()
    {
        var firstStore = new TaskLogStore(new TestDbContextFactory(_database.Options), _timeProvider);
        var secondStore = new TaskLogStore(new TestDbContextFactory(_database.Options), _timeProvider);
        var sameOwner = $"aj-{Guid.NewGuid():N}";
        var sameWork = $"work-{Guid.NewGuid():N}";
        var samePayload = new List<TaskLogLine>
        {
            new(1, _timeProvider.GetUtcNow(), "terminal", "same"),
        };

        var sameResults = await Task.WhenAll(
            Task.Run(() => firstStore.AppendAsync("agent-job", sameOwner, sameWork, samePayload, false, true)),
            Task.Run(() => secondStore.AppendAsync("agent-job", sameOwner, sameWork, samePayload, false, true)));

        Assert.Equal(1, sameResults.Count(result => result == TaskLogAppendResult.Changed));
        Assert.Equal(1, sameResults.Count(result => result == TaskLogAppendResult.Duplicate));

        var differentOwner = $"aj-{Guid.NewGuid():N}";
        var differentWork = $"work-{Guid.NewGuid():N}";
        var firstPayload = new List<TaskLogLine>
        {
            new(1, _timeProvider.GetUtcNow(), "terminal", "first"),
        };
        var secondPayload = new List<TaskLogLine>
        {
            new(1, _timeProvider.GetUtcNow(), "terminal", "second"),
        };

        var differentResults = await Task.WhenAll(
            Task.Run(() => firstStore.AppendAsync("agent-job", differentOwner, differentWork, firstPayload, false, true)),
            Task.Run(() => secondStore.AppendAsync("agent-job", differentOwner, differentWork, secondPayload, false, true)));

        Assert.Equal(1, differentResults.Count(result => result == TaskLogAppendResult.Changed));
        Assert.Equal(1, differentResults.Count(result => result == TaskLogAppendResult.Conflict));

        var page = await firstStore.QueryAsync("agent-job", differentOwner, differentWork, null, 10);
        Assert.Single(page.Lines);
        Assert.Contains(page.Lines[0].Text, new[] { "first", "second" });
    }

    [Fact]
    public async Task AppendAsync_TerminalBatchPrunesRowsOutsideRetainedTail()
    {
        const int retainedTailSize = TaskLogStore.MaxLimit;
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        await _store.AppendAsync(ownerKind, ownerId, workId,
            Enumerable.Range(1, 1)
                .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "incremental", $"line-{seq}"))
                .ToList(),
            truncated: false);
        await _store.AppendAsync(ownerKind, ownerId, workId,
            Enumerable.Range(2, retainedTailSize)
                .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "incremental", $"line-{seq}"))
                .ToList(),
            truncated: true);

        await _store.AppendAsync(ownerKind, ownerId, workId,
            Enumerable.Range(2, retainedTailSize)
                .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "terminal", $"line-{seq}"))
                .ToList(),
            truncated: true,
            terminal: true);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: retainedTailSize);

        Assert.Equal(retainedTailSize, page.Lines.Count);
        Assert.Equal(2, page.Lines[0].Seq);
        Assert.Equal(retainedTailSize + 1, page.Lines[^1].Seq);
        Assert.True(page.Truncated);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task AppendAsync_TerminalBatchRestoresLinesMissingFromFailedIncrement()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        await _store.AppendAsync(ownerKind, ownerId, workId,
            new List<TaskLogLine>
            {
                new(1, _timeProvider.GetUtcNow(), "incremental", "line-1"),
                new(2, _timeProvider.GetUtcNow(), "incremental", "line-2"),
            },
            truncated: false);

        await _store.AppendAsync(ownerKind, ownerId, workId,
            Enumerable.Range(1, 5)
                .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "terminal", $"line-{seq}"))
                .ToList(),
            truncated: false);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);

        Assert.Equal([1, 2, 3, 4, 5], page.Lines.Select(l => l.Seq).ToArray());
        Assert.Equal(["line-1", "line-2", "line-3", "line-4", "line-5"], page.Lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public async Task AppendAsync_DifferentOwnerKinds_KeepEntriesSeparate()
    {
        var workflowRunId = $"wr-{Guid.NewGuid():N}";
        var agentJobId = $"aj-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var now = _timeProvider.GetUtcNow();

        await _store.AppendAsync("workflow", workflowRunId, workId,
            new List<TaskLogLine> { new(1, now, "action", "from-workflow") },
            truncated: false);

        await _store.AppendAsync("agent-job", agentJobId, workId,
            new List<TaskLogLine> { new(1, now, "action", "from-agent-job") },
            truncated: false);

        var workflowPage = await _store.QueryAsync("workflow", workflowRunId, workId, afterSeq: null, limit: 10);
        var agentPage = await _store.QueryAsync("agent-job", agentJobId, workId, afterSeq: null, limit: 10);

        Assert.Single(workflowPage.Lines);
        Assert.Equal("from-workflow", workflowPage.Lines[0].Text);
        Assert.Single(agentPage.Lines);
        Assert.Equal("from-agent-job", agentPage.Lines[0].Text);
    }

    [Fact]
    public async Task AppendAsync_NullOrEmptyArgs_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AppendAsync("", "owner", "work", new List<TaskLogLine>(), false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AppendAsync("workflow", "", "work", new List<TaskLogLine>(), false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AppendAsync("workflow", "owner", "", new List<TaskLogLine>(), false));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.AppendAsync("workflow", "owner", "work", null!, false));
    }

    [Fact]
    public async Task AppendAsync_RejectsDuplicateOrNonPositiveSeqValues()
    {
        var now = _timeProvider.GetUtcNow();
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            new List<TaskLogLine>
            {
                new(1, now, "action", "first"),
                new(1, now, "action", "duplicate"),
            },
            false));

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            new List<TaskLogLine> { new(0, now, "action", "zero") },
            false));
    }

    [Fact]
    public async Task AppendAsync_RejectsInvalidMetadataAndOversizedText()
    {
        var now = _timeProvider.GetUtcNow();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            new List<TaskLogLine> { new(1, default, "action", "line") },
            false));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            new List<TaskLogLine> { new(1, now, "", "line") },
            false));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            new List<TaskLogLine> { new(1, now, "action", new string('x', TaskLogUploadLimits.MaxTextLength + 1)) },
            false));
    }

    [Fact]
    public async Task AppendAsync_RejectsBatchOverEntryLimit()
    {
        var entries = Enumerable.Range(1, TaskLogUploadLimits.MaxEntries + 1)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", "line"))
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            entries,
            false));
    }

    [Fact]
    public async Task AppendAsync_RejectsBatchOverTotalTextLimit()
    {
        var now = _timeProvider.GetUtcNow();
        var entries = Enumerable.Range(1, 31)
            .Select(seq => new TaskLogLine(seq, now, "action", new string('x', TaskLogUploadLimits.MaxTextLength)))
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(
            "workflow",
            "owner",
            "work",
            entries,
            false));
    }

    [Fact]
    public async Task QueryAsync_ClampsHugeLimitsToMaximum()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";
        var entries = Enumerable.Range(1, TaskLogStore.MaxLimit + 2)
            .Select(seq => new TaskLogLine(seq, _timeProvider.GetUtcNow(), "action", $"line {seq}"))
            .ToList();
        await _store.AppendAsync(ownerKind, ownerId, workId, entries, truncated: false);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: int.MaxValue);

        Assert.Equal(TaskLogStore.MaxLimit, page.Lines.Count);
        Assert.Equal(TaskLogStore.MaxLimit, page.NextCursor);
    }

    [Fact]
    public async Task AppendAsync_EmptyEntriesList_StillRecordsBatchMetadata()
    {
        var ownerKind = "workflow";
        var ownerId = $"wr-{Guid.NewGuid():N}";
        var workId = $"work-{Guid.NewGuid():N}";

        await _store.AppendAsync(ownerKind, ownerId, workId, new List<TaskLogLine>(), truncated: false);

        var page = await _store.QueryAsync(ownerKind, ownerId, workId, afterSeq: null, limit: 10);
        Assert.Empty(page.Lines);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
    }
}
