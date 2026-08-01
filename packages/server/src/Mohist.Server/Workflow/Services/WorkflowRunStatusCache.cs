using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowRunStatusCache : ISingletonService
{
    public const int DefaultCapacity = 256;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<QueueEntry> _insertionOrder = new();
    private long _nextSequence;

    public WorkflowRunStatusCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool TryGet(string workflowRunId, long etag, out WorkflowRun? run)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(workflowRunId, out var entry) && entry.ETag == etag)
            {
                run = entry.Run;
                return true;
            }
        }

        run = null;
        return false;
    }

    public void Store(string workflowRunId, long etag, WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(workflowRunId);
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            if (_entries.TryGetValue(workflowRunId, out var existing))
            {
                _entries[workflowRunId] = new CacheEntry(etag, run, existing.Sequence);
            }
            else
            {
                var sequence = ++_nextSequence;
                _entries.Add(workflowRunId, new CacheEntry(etag, run, sequence));
                _insertionOrder.Enqueue(new QueueEntry(workflowRunId, sequence));
            }

            EvictIfNeeded();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _insertionOrder.Clear();
        }
    }

    private void EvictIfNeeded()
    {
        while (_entries.Count > _capacity)
        {
            var candidate = _insertionOrder.Dequeue();
            if (_entries.TryGetValue(candidate.WorkflowRunId, out var entry)
                && entry.Sequence == candidate.Sequence)
            {
                _entries.Remove(candidate.WorkflowRunId);
            }
        }
    }

    private sealed record CacheEntry(long ETag, WorkflowRun Run, long Sequence);

    private sealed record QueueEntry(string WorkflowRunId, long Sequence);
}
