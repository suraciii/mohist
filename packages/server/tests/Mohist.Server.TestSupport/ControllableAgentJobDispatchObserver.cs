using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Test observer that exposes dispatch boundaries and supports deterministic
/// per-job waiter lifecycles for grain specs.
/// </summary>
public sealed class ControllableAgentJobDispatchObserver : IAgentJobDispatchObserver
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, JobSignal<string>> _assignmentPreparedByJob = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobSignal> _runnerAcceptedByJob = new(StringComparer.Ordinal);
    private TaskCompletionSource _assignmentPrepared = NewSignal();
    private TaskCompletionSource _runnerAccepted = NewSignal();
    private TaskCompletionSource? _assignmentPreparedBlock;

    public ControllableAgentJobDispatchObserver(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? new FixedTimeProvider(TestTime.UtcNow);

    public bool FailAssignmentPrepared { get; set; }
    public bool FailRunnerAccepted { get; set; }

    public Task AssignmentPrepared => _assignmentPrepared.Task;

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _assignmentPrepared.TrySetResult();
        CompleteSignal(_assignmentPreparedByJob, agentJobId, runnerId);
        if (_assignmentPreparedBlock is not null)
            return _assignmentPreparedBlock.Task;
        return FailAssignmentPrepared
            ? Task.FromException(new InvalidOperationException("simulated activation loss after assignment preparation"))
            : Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId)
    {
        _runnerAccepted.TrySetResult();
        CompleteSignal(_runnerAcceptedByJob, agentJobId);
        return FailRunnerAccepted
            ? Task.FromException(new InvalidOperationException("simulated activation loss after runner acceptance"))
            : Task.CompletedTask;
    }

    public Task WaitForRunnerAcceptedAsync() => WaitForSignalAsync(
        _runnerAccepted,
        TimeSpan.FromSeconds(5),
        CancellationToken.None,
        "AgentJob dispatch observer runner accepted");

    public Task WaitForRunnerAcceptedAsync(
        string agentJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForSignalAsync(
            _runnerAcceptedByJob,
            agentJobId,
            timeout,
            cancellationToken,
            $"AgentJob {agentJobId} dispatch observer runner accepted");

    public Task WaitForAssignmentPreparedAsync() => WaitForSignalAsync(
        _assignmentPrepared,
        TimeSpan.FromSeconds(5),
        CancellationToken.None,
        "AgentJob dispatch observer assignment prepared");

    public Task<string> WaitForAssignmentPreparedAsync(
        string agentJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitForSignalAsync(
            _assignmentPreparedByJob,
            agentJobId,
            timeout,
            cancellationToken,
            $"AgentJob {agentJobId} dispatch observer assignment prepared");

    public void BlockAssignmentPrepared() => _assignmentPreparedBlock ??= NewSignal();

    public void ReleaseAssignmentPrepared() => _assignmentPreparedBlock?.TrySetResult();

    public void Reset()
    {
        FailAssignmentPrepared = false;
        FailRunnerAccepted = false;
        _assignmentPreparedBlock?.TrySetResult();
        _assignmentPreparedBlock = null;
        _assignmentPreparedByJob.Clear();
        _runnerAcceptedByJob.Clear();
        _assignmentPrepared = NewSignal();
        _runnerAccepted = NewSignal();
    }

    private void CompleteSignal<T>(
        ConcurrentDictionary<string, JobSignal<T>> signals,
        string agentJobId,
        T value)
    {
        while (true)
        {
            var signal = signals.GetOrAdd(agentJobId, static _ => new JobSignal<T>());
            if (signal.TryComplete(value))
                return;

            RemoveSignal(signals, agentJobId, signal);
        }
    }

    private void CompleteSignal(
        ConcurrentDictionary<string, JobSignal> signals,
        string agentJobId)
    {
        while (true)
        {
            var signal = signals.GetOrAdd(agentJobId, static _ => new JobSignal());
            if (signal.TryComplete())
                return;

            RemoveSignal(signals, agentJobId, signal);
        }
    }

    private async Task<T> WaitForSignalAsync<T>(
        ConcurrentDictionary<string, JobSignal<T>> signals,
        string agentJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string description)
    {
        var signal = AcquireSignal(signals, agentJobId);
        try
        {
            return await signal.Completion.Task.WaitAsync(timeout, _timeProvider, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for: {description}", ex);
        }
        finally
        {
            if (signal.ReleaseWaiter())
                RemoveSignal(signals, agentJobId, signal);
        }
    }

    private async Task WaitForSignalAsync(
        ConcurrentDictionary<string, JobSignal> signals,
        string agentJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string description)
    {
        var signal = AcquireSignal(signals, agentJobId);
        try
        {
            await signal.Completion.Task.WaitAsync(timeout, _timeProvider, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for: {description}", ex);
        }
        finally
        {
            if (signal.ReleaseWaiter())
                RemoveSignal(signals, agentJobId, signal);
        }
    }

    private async Task WaitForSignalAsync(
        TaskCompletionSource signal,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string description)
    {
        try
        {
            await signal.Task.WaitAsync(timeout, _timeProvider, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for: {description}", ex);
        }
    }

    private static JobSignal<T> AcquireSignal<T>(
        ConcurrentDictionary<string, JobSignal<T>> signals,
        string agentJobId)
    {
        while (true)
        {
            var signal = signals.GetOrAdd(agentJobId, static _ => new JobSignal<T>());
            if (signal.TryAcquireWaiter())
                return signal;

            RemoveSignal(signals, agentJobId, signal);
        }
    }

    private static JobSignal AcquireSignal(
        ConcurrentDictionary<string, JobSignal> signals,
        string agentJobId)
    {
        while (true)
        {
            var signal = signals.GetOrAdd(agentJobId, static _ => new JobSignal());
            if (signal.TryAcquireWaiter())
                return signal;

            RemoveSignal(signals, agentJobId, signal);
        }
    }

    private static void RemoveSignal<T>(
        ConcurrentDictionary<string, JobSignal<T>> signals,
        string agentJobId,
        JobSignal<T> signal) =>
        ((ICollection<KeyValuePair<string, JobSignal<T>>>)signals)
            .Remove(new KeyValuePair<string, JobSignal<T>>(agentJobId, signal));

    private static void RemoveSignal(
        ConcurrentDictionary<string, JobSignal> signals,
        string agentJobId,
        JobSignal signal) =>
        ((ICollection<KeyValuePair<string, JobSignal>>)signals)
            .Remove(new KeyValuePair<string, JobSignal>(agentJobId, signal));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class JobSignal
    {
        private readonly object _gate = new();
        private int _waiters;
        private bool _retired;

        public TaskCompletionSource Completion { get; } = NewSignal();

        public bool TryAcquireWaiter()
        {
            lock (_gate)
            {
                if (_retired)
                    return false;

                _waiters++;
                return true;
            }
        }

        public bool TryComplete()
        {
            lock (_gate)
            {
                if (_retired)
                    return false;

                Completion.TrySetResult();
                return true;
            }
        }

        public bool ReleaseWaiter()
        {
            lock (_gate)
            {
                _waiters--;
                if (_waiters != 0)
                    return false;

                _retired = true;
                return true;
            }
        }
    }

    private sealed class JobSignal<T>
    {
        private readonly object _gate = new();
        private int _waiters;
        private bool _retired;

        public TaskCompletionSource<T> Completion { get; } = NewSignal<T>();

        public bool TryAcquireWaiter()
        {
            lock (_gate)
            {
                if (_retired)
                    return false;

                _waiters++;
                return true;
            }
        }

        public bool TryComplete(T value)
        {
            lock (_gate)
            {
                if (_retired)
                    return false;

                Completion.TrySetResult(value);
                return true;
            }
        }

        public bool ReleaseWaiter()
        {
            lock (_gate)
            {
                _waiters--;
                if (_waiters != 0)
                    return false;

                _retired = true;
                return true;
            }
        }
    }

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
