namespace Mohist.Server.Otel;

public sealed class RequestWorkScope
{
    private static readonly AsyncLocal<RequestWorkScope?> Ambient = new();
    private readonly object _gate = new();
    private long _databaseCalls;
    private long _downstreamCalls;
    private string? _path;
    private long _candidates;
    private long _processed;
    private long _transcriptRecords;
    private bool _closed;

    public static RequestWorkScope? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    public static IDisposable Push(RequestWorkScope? scope)
    {
        var previous = Current;
        Current = scope;
        return new AmbientRestore(previous);
    }

    public void AddDatabaseCalls(long count = 1)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (!_closed) _databaseCalls = RuntimeValueRules.Add(_databaseCalls, count);
        }
    }

    public void AddDownstreamCalls(long count = 1)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (!_closed) _downstreamCalls = RuntimeValueRules.Add(_downstreamCalls, count);
        }
    }

    public bool SetAgentPath(string? path)
    {
        var normalized = RuntimeObservability.NormalizeAgentPath(path);
        if (normalized is null) return false;
        lock (_gate)
        {
            if (_closed || _path is not null) return false;
            _path = normalized;
            return true;
        }
    }

    public void AddCandidates(long count)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (!_closed) _candidates = RuntimeValueRules.Add(_candidates, count);
        }
    }

    public void AddProcessed(long count)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (!_closed) _processed = RuntimeValueRules.Add(_processed, count);
        }
    }

    public void AddTranscriptRecords(long count)
    {
        if (count < 0) return;
        lock (_gate)
        {
            if (!_closed) _transcriptRecords = RuntimeValueRules.Add(_transcriptRecords, count);
        }
    }

    public RequestWorkSnapshot Snapshot()
    {
        lock (_gate)
            return SnapshotLocked();
    }

    public RequestWorkSnapshot CloseAndSnapshot()
    {
        lock (_gate)
        {
            if (_closed) return SnapshotLocked();
            _closed = true;
            return SnapshotLocked();
        }
    }

    private RequestWorkSnapshot SnapshotLocked() => new(
        _databaseCalls,
        _downstreamCalls,
        _path,
        _candidates,
        _processed,
        _transcriptRecords);

    private sealed class AmbientRestore(RequestWorkScope? previous) : IDisposable
    {
        public void Dispose() => Current = previous;
    }
}

public readonly record struct RequestWorkSnapshot(
    long DatabaseCalls,
    long DownstreamCalls,
    string? AgentPath,
    long Candidates,
    long Processed,
    long TranscriptRecords);
