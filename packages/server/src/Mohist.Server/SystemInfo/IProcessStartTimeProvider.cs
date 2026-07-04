namespace Mohist.Server.SystemInfo;

/// <summary>
/// Source of the current process start time, used as the stale-job
/// threshold by <see cref="SystemUpdateRecoveryService"/>. The default
/// production implementation reads the actual process start time; tests
/// substitute a fake so the reconciler never touches
/// <c>DateTimeOffset.UtcNow</c>, <c>Environment.TickCount</c>, or
/// process-info APIs directly.
/// </summary>
public interface IProcessStartTimeProvider
{
    DateTimeOffset GetStartTime();
}