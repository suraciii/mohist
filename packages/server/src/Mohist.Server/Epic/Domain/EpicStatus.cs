namespace Mohist.Server.Epic.Domain;

public enum EpicStatus
{
    Idle,
    Running,
    Paused,
    Done,
    Closed
}

public static class EpicStatusName
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Done = "done";
    public const string Closed = "closed";

    public static string ToName(EpicStatus status) => status switch
    {
        EpicStatus.Idle => Idle,
        EpicStatus.Running => Running,
        EpicStatus.Paused => Paused,
        EpicStatus.Done => Done,
        EpicStatus.Closed => Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static EpicStatus Parse(string? status) => status?.ToLowerInvariant() switch
    {
        null => EpicStatus.Idle,
        "" => EpicStatus.Idle,
        Running => EpicStatus.Running,
        Paused => EpicStatus.Paused,
        Done => EpicStatus.Done,
        Closed => EpicStatus.Closed,
        // Legacy pre-rename value. Migration backfills 'active' → 'idle';
        // parse keeps it safe for any row that hasn't been migrated yet.
        "active" => EpicStatus.Idle,
        _ => EpicStatus.Idle,
    };
}
