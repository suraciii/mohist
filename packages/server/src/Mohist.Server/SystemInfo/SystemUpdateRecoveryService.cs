using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.SystemInfo;

/// <summary>
/// One-shot startup reconciler that recovers system-update jobs left
/// active (running / waiting-for-reconnect) by an interrupted prior
/// server process. Registered as a direct <see cref="IHostedService"/>
/// so the host awaits <see cref="StartAsync"/> before opening the HTTP
/// listener — no early request can race past recovery and see
/// <c>update_in_progress</c> for a job nobody is executing.
///
/// A job whose <c>UpdatedAt</c> strictly precedes the injected process
/// start time is transitioned to <c>failed</c> with the literal reason
    /// <c>"interrupted by process restart"</c> after its lock is released
    /// via <see cref="ISystemUpdateStore.ReleaseStaleLockAsync"/>. Fresh
/// active jobs (<c>UpdatedAt &gt;=</c> process start) and all terminal
/// jobs (<c>succeeded</c>/<c>failed</c>/<c>recovered</c>/<c>superseded</c>/<c>cancelled</c>)
/// are never modified.
///
/// All time inputs come through <see cref="TimeProvider"/> (for the
/// <c>failed</c> transition timestamps) and
/// <see cref="IProcessStartTimeProvider"/> (for the stale threshold);
/// this service never reads wall-clock / process info directly and
/// never depends on <see cref="SystemUpdateService"/>.
/// </summary>
public sealed class SystemUpdateRecoveryService : IHostedService
{
    public const string InterruptedByProcessRestartReason = "interrupted by process restart";
    private const int MaxLogEntries = 200;

    private readonly ISystemUpdateStore _store;
    private readonly TimeProvider _time;
    private readonly IProcessStartTimeProvider _processStart;
    private readonly ILogger<SystemUpdateRecoveryService> _logger;

    public SystemUpdateRecoveryService(
        ISystemUpdateStore store,
        TimeProvider time,
        IProcessStartTimeProvider processStart,
        ILogger<SystemUpdateRecoveryService> logger)
    {
        _store = store;
        _time = time;
        _processStart = processStart;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var latest = await _store.GetLatestAsync(cancellationToken);
        if (latest is null)
            return;

        if (IsInterruptedRecoveryState(latest))
        {
            await RetryInterruptedRecoveryLockReleaseAsync(latest, cancellationToken);
            return;
        }

        if (SystemUpdateJobState.TerminalStatuses.Contains(latest.Status))
            return;

        if (!SystemUpdateJobState.ActiveStatuses.Contains(latest.Status))
            return;

        var processStart = _processStart.GetStartTime();
        if (latest.UpdatedAt >= processStart)
            return;

        var releasedStaleLock = await _store.ReleaseStaleLockAsync(latest.JobId, cancellationToken);
        if (!releasedStaleLock)
        {
            _logger.LogWarning(
                "Skipped interrupted system-update recovery for job {JobId}: stale lock was not released.",
                latest.JobId);
            return;
        }

        var recoveredAt = _time.GetUtcNow();
        var logEntry = new SystemUpdateLogEntry(
            recoveredAt,
            "Failed",
            $"{InterruptedByProcessRestartReason}; releasing stale lock.");
        var next = latest with
        {
            Status = "failed",
            Stage = "Failed",
            Reason = InterruptedByProcessRestartReason,
            CompletedAt = recoveredAt,
            UpdatedAt = recoveredAt,
            Logs = AppendLog(latest.Logs, logEntry)
        };

        await _store.SaveAsync(next, cancellationToken);

        _logger.LogWarning(
            "Recovered interrupted system-update job {JobId}: marked failed (UpdatedAt {UpdatedAt} predates process start {ProcessStart}); released stale lock.",
            latest.JobId, latest.UpdatedAt, processStart);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyList<SystemUpdateLogEntry> AppendLog(
        IReadOnlyList<SystemUpdateLogEntry> logs,
        SystemUpdateLogEntry entry)
    {
        var next = logs.ToList();
        next.Add(entry);
        if (next.Count > MaxLogEntries)
            next = next[^MaxLogEntries..];
        return next;
    }

    private static bool IsInterruptedRecoveryState(SystemUpdateJobState state)
    {
        return state.Status == "failed"
            && state.Reason == InterruptedByProcessRestartReason;
    }

    private async Task RetryInterruptedRecoveryLockReleaseAsync(
        SystemUpdateJobState state,
        CancellationToken cancellationToken)
    {
        var releasedStaleLock = await _store.ReleaseStaleLockAsync(state.JobId, cancellationToken);
        if (releasedStaleLock)
        {
            _logger.LogWarning(
                "Retried stale lock release for interrupted system-update job {JobId}.",
                state.JobId);
        }
    }
}
