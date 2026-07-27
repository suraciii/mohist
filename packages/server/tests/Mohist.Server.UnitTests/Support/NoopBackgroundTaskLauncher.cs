using Mohist.Server.Infrastructure;

namespace Mohist.Server.UnitTests.Support;

/// <summary>
/// No-op <see cref="IBackgroundTaskLauncher"/> for unit tests that
/// exercise production components in isolation. Records every launched
/// work item so a test can still assert the component scheduled its
/// follow-up without depending on a real background scheduler.
/// </summary>
public sealed class NoopBackgroundTaskLauncher : IBackgroundTaskLauncher
{
    public static NoopBackgroundTaskLauncher Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<LaunchRecord> _launched = [];

    private NoopBackgroundTaskLauncher()
    {
    }

    public IReadOnlyList<LaunchRecord> Launched
    {
        get
        {
            lock (_gate)
            {
                return _launched.ToList();
            }
        }
    }

    public void Launch(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            _launched.Add(new LaunchRecord(cancellationToken));
        }
    }

    public sealed record LaunchRecord(CancellationToken CancellationToken);
}
