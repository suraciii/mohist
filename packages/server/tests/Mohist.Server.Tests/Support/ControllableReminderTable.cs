using Orleans.Runtime;

namespace Mohist.Server.Tests.Support;

public sealed class ControllableReminderTable : IReminderTable
{
    private readonly IReminderTable _inner;
    private readonly object _lock = new();
    private ReminderRemovePause? _nextRemovePause;

    public ControllableReminderTable(IReminderTable inner)
    {
        _inner = inner;
    }

    public ReminderRemovePause PauseNextRemove(GrainId grainId, string reminderName)
    {
        var pause = new ReminderRemovePause(grainId, reminderName);
        lock (_lock)
        {
            _nextRemovePause = pause;
        }
        return pause;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

#pragma warning disable CS0618 // IReminderTable still requires Init; Orleans prefers StartAsync for new implementations.
    public Task Init() => _inner.Init();
#pragma warning restore CS0618

    public Task<ReminderTableData> ReadRows(GrainId grainId) => _inner.ReadRows(grainId);

    public Task<ReminderTableData> ReadRows(uint begin, uint end) => _inner.ReadRows(begin, end);

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => _inner.ReadRow(grainId, reminderName);

    public Task<string> UpsertRow(ReminderEntry entry) => _inner.UpsertRow(entry);

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        ReminderRemovePause? pause = null;
        lock (_lock)
        {
            if (_nextRemovePause?.Matches(grainId, reminderName) == true)
            {
                pause = _nextRemovePause;
                _nextRemovePause = null;
            }
        }

        if (pause is not null)
        {
            pause.MarkStarted();
            await pause.WaitForReleaseAsync();
        }

        return await _inner.RemoveRow(grainId, reminderName, eTag);
    }

    public Task TestOnlyClearTable() => _inner.TestOnlyClearTable();
}

public sealed class ReminderRemovePause
{
    private readonly GrainId _grainId;
    private readonly string _reminderName;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ReminderRemovePause(GrainId grainId, string reminderName)
    {
        _grainId = grainId;
        _reminderName = reminderName;
    }

    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult();

    internal bool Matches(GrainId grainId, string reminderName)
    {
        return _grainId.Equals(grainId)
            && string.Equals(_reminderName, reminderName, StringComparison.Ordinal);
    }

    internal void MarkStarted() => _started.TrySetResult();

    internal Task WaitForReleaseAsync() => _release.Task;
}
