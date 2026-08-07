using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.TestSupport;

public sealed class SharedReminderTable : IReminderTable
{
    private readonly Dictionary<(GrainId GrainId, string ReminderName), ReminderEntry> _entries = [];
    private readonly object _gate = new();
    private TaskCompletionSource _rangeReadSignal = NewSignal();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

#pragma warning disable CS0618
    public Task Init() => Task.CompletedTask;
#pragma warning restore CS0618

    public Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        lock (_gate)
        {
            return Task.FromResult(new ReminderTableData(
                _entries.Values.Where(entry => entry.GrainId.Equals(grainId)).Select(Clone).ToList()));
        }
    }

    public Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        lock (_gate)
        {
            var entries =
                _entries.Values
                    .Where(entry => IsInRange(entry.GrainId.GetUniformHashCode(), begin, end))
                    .Select(Clone)
                    .ToList();
            if (entries.Count > 0)
                _rangeReadSignal.TrySetResult();
            return Task.FromResult(new ReminderTableData(entries));
        }
    }

    public Task PrepareRangeReadSignal()
    {
        lock (_gate)
        {
            _rangeReadSignal = NewSignal();
            return _rangeReadSignal.Task;
        }
    }

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _entries.TryGetValue((grainId, reminderName), out var entry)
                    ? Clone(entry)
                    : null);
        }
    }

    public Task<string> UpsertRow(ReminderEntry entry)
    {
        lock (_gate)
        {
            var stored = Clone(entry);
            stored.ETag = Guid.NewGuid().ToString("N");
            _entries[(stored.GrainId, stored.ReminderName)] = stored;
            return Task.FromResult(stored.ETag);
        }
    }

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        lock (_gate)
        {
            var key = (grainId, reminderName);
            if (!_entries.TryGetValue(key, out var entry)
                || !string.Equals(entry.ETag, eTag, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _entries.Remove(key);
            return Task.FromResult(true);
        }
    }

    public Task TestOnlyClearTable()
    {
        lock (_gate)
            _entries.Clear();
        return Task.CompletedTask;
    }

    private static bool IsInRange(uint hash, uint begin, uint end) =>
        begin < end
            ? hash > begin && hash <= end
            : hash > begin || hash <= end;

    private static ReminderEntry Clone(ReminderEntry entry) => new()
    {
        GrainId = entry.GrainId,
        ReminderName = entry.ReminderName,
        StartAt = entry.StartAt,
        Period = entry.Period,
        ETag = entry.ETag,
    };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
