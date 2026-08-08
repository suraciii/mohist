using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;
using Xunit;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Test-only probe for the best-effort AgentJob dispatch-observer side
/// channel. The prepared signal is emitted after the durable assignment
/// ledger write; tests may use it to order a subsequent protocol request
/// while keeping the HTTP poll as the claim assertion.
/// </summary>
public sealed class AgentJobDispatchProbe : IAgentJobDispatchObserver
{
    private readonly ConcurrentDictionary<string, int> _preparedCounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PreparedSignal> _preparedSignals =
        new(StringComparer.Ordinal);

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _preparedCounts.AddOrUpdate(agentJobId, 1, (_, count) => count + 1);
        if (_preparedSignals.TryGetValue(agentJobId, out var signal))
            signal.Completion.TrySetResult();
        return Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId) =>
        Task.CompletedTask;

    public int PreparedCount(string agentJobId) =>
        _preparedCounts.GetValueOrDefault(agentJobId);

    public async Task WaitForAssignmentPreparedAsync(
        string agentJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (_preparedCounts.ContainsKey(agentJobId))
            return;

        var signal = AcquireSignal(agentJobId);
        try
        {
            if (_preparedCounts.ContainsKey(agentJobId))
                signal.Completion.TrySetResult();

            await signal.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"Timed out waiting for AgentJob '{agentJobId}' assignment preparation after {timeout}. "
                + $"PreparedCount={PreparedCount(agentJobId)}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(
                $"Cancelled while waiting for AgentJob '{agentJobId}' assignment preparation. "
                + $"PreparedCount={PreparedCount(agentJobId)}.");
        }
        finally
        {
            if (signal.ReleaseWaiter())
                RemoveSignal(agentJobId, signal);
        }
    }

    private PreparedSignal AcquireSignal(string agentJobId)
    {
        while (true)
        {
            var signal = _preparedSignals.GetOrAdd(
                agentJobId,
                static _ => new PreparedSignal());
            if (signal.TryAcquireWaiter())
                return signal;

            RemoveSignal(agentJobId, signal);
        }
    }

    private void RemoveSignal(string agentJobId, PreparedSignal signal) =>
        ((ICollection<KeyValuePair<string, PreparedSignal>>)_preparedSignals)
            .Remove(new KeyValuePair<string, PreparedSignal>(agentJobId, signal));

    private sealed class PreparedSignal
    {
        private readonly object _gate = new();
        private int _waiters;
        private bool _retired;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
}
