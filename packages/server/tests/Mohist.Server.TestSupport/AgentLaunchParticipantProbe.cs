using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Identifies the coordinator participant call a
/// <see cref="AgentLaunchParticipantProbe"/> failure is armed against.
/// One gate per fence in the launch sequence.
/// </summary>
public enum LaunchParticipantGate
{
    PrepareJob,
    ReserveLink,
    EnsureInitialLaunch,
    ParentLinkCommitted,
    SubmitJob,
    ArchiveDefinition,
}

/// <summary>
/// Test-only <see cref="IAgentLaunchParticipantProbe"/> that throws on
/// the next N acknowledgements at an armed gate, then succeeds. The coordinator
/// calls the probe after each participant grain call succeeds and before it
/// advances the durable fence, so a thrown probe simulates a lost acknowledgement
/// while leaving the participant's write durable. Recovery tests arm the
/// gate, observe <c>launch_setup_pending</c> over the route, then
/// <see cref="StopFailing"/> and retry the same Idempotency-Key.
/// </summary>
public sealed class AgentLaunchParticipantProbe : IAgentLaunchParticipantProbe
{
    private readonly ConcurrentDictionary<LaunchParticipantGate, int> _remaining =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, ConcurrentQueue<string>> _commandIds =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, ConcurrentQueue<string>> _participantIds =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, string> _rejections =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, TaskCompletionSource> _blocked =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, TaskCompletionSource> _entered =
        new();
    private readonly ConcurrentDictionary<LaunchParticipantGate, Func<string, bool>> _blockMatchers =
        new();

    public void FailNext(LaunchParticipantGate gate, int times = 1)
    {
        _commandIds.TryRemove(gate, out _);
        _participantIds.TryRemove(gate, out _);
        _rejections.TryRemove(gate, out _);
        if (times <= 0)
        {
            StopFailing(gate);
            return;
        }
        _remaining[gate] = times;
    }

    public void StopFailing(LaunchParticipantGate gate) =>
        _remaining.TryRemove(gate, out _);

    /// <summary>
    /// Arms a one-shot block at <paramref name="gate"/>. Without
    /// <paramref name="match"/> the next participant to reach the gate is
    /// held; with it, only a participant whose id satisfies the predicate
    /// engages the block — unrelated launches (background event dispatch,
    /// concurrently running specs) pass through. The gate fires after the
    /// participant's own grain call, so the participant id is the durable
    /// key of that call (session id, job key, or edge id) and can be
    /// predicted by the arming test.
    /// </summary>
    public void BlockNext(LaunchParticipantGate gate, Func<string, bool>? match = null)
    {
        _entered[gate] = NewSignal();
        _blocked[gate] = NewSignal();
        if (match is null)
            _blockMatchers.TryRemove(gate, out _);
        else
            _blockMatchers[gate] = match;
    }

    public Task WaitUntilBlockedAsync(LaunchParticipantGate gate) =>
        _entered.TryGetValue(gate, out var entered)
            ? entered.Task
            : throw new InvalidOperationException($"No block is armed for {gate}.");

    public void ReleaseBlocked(LaunchParticipantGate gate)
    {
        _blockMatchers.TryRemove(gate, out _);
        if (_blocked.TryRemove(gate, out var blocked))
            blocked.TrySetResult();
        _entered.TryRemove(gate, out _);
    }

    public void RejectNext(LaunchParticipantGate gate, string reason)
    {
        _commandIds.TryRemove(gate, out _);
        _participantIds.TryRemove(gate, out _);
        _rejections[gate] = reason;
    }

    public void StopRejecting(LaunchParticipantGate gate) =>
        _rejections.TryRemove(gate, out _);

    public void ClearObservations()
    {
        _commandIds.Clear();
        _participantIds.Clear();
        _rejections.Clear();
    }

    public IReadOnlyList<string> CommandIds(LaunchParticipantGate gate) =>
        _commandIds.TryGetValue(gate, out var commandIds) ? commandIds.ToArray() : [];

    public IReadOnlyList<string> ParticipantIds(LaunchParticipantGate gate) =>
        _participantIds.TryGetValue(gate, out var participantIds) ? participantIds.ToArray() : [];

    public Task OnPrepareJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.PrepareJob, jobKey, commandId);

    public Task OnReserveLinkAsync(string edgeId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.ReserveLink, edgeId, commandId);

    public Task OnEnsureInitialLaunchAsync(string sessionId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.EnsureInitialLaunch, sessionId, commandId);

    public Task OnParentLinkCommittedAsync(string edgeId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.ParentLinkCommitted, edgeId, commandId);

    public Task OnSubmitJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.SubmitJob, jobKey, commandId);

    public Task OnArchiveDefinitionAsync(string agentId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.ArchiveDefinition, agentId, commandId);

    private async Task RecordAndMaybeThrow(LaunchParticipantGate gate, string participantId, string commandId)
    {
        _participantIds.GetOrAdd(gate, _ => new()).Enqueue(participantId);
        _commandIds.GetOrAdd(gate, _ => new()).Enqueue(commandId);
        if (_blocked.TryGetValue(gate, out var blocked)
            && (!_blockMatchers.TryGetValue(gate, out var match) || match(participantId)))
        {
            _entered.GetOrAdd(gate, _ => NewSignal()).TrySetResult();
            await blocked.Task;
        }
        while (_remaining.TryGetValue(gate, out var current) && current > 0)
        {
            if (_remaining.TryUpdate(gate, current - 1, current))
            {
                throw new InvalidOperationException(
                    $"Simulated participant failure at {gate}.");
            }
        }
        if (_rejections.TryRemove(gate, out var reason))
            throw new AgentSpawnPostPlanRejectedException(reason);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
