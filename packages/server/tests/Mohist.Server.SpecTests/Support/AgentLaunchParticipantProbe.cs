using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Identifies the coordinator participant call a
/// <see cref="AgentLaunchParticipantProbe"/> failure is armed against.
/// One gate per fence in the launch sequence.
/// </summary>
public enum LaunchParticipantGate
{
    PrepareJob,
    EnsureInitialLaunch,
    ParentLinkCommitted,
    SubmitJob,
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

    public void FailNext(LaunchParticipantGate gate, int times = 1)
    {
        _commandIds.TryRemove(gate, out _);
        _participantIds.TryRemove(gate, out _);
        if (times <= 0)
        {
            StopFailing(gate);
            return;
        }
        _remaining[gate] = times;
    }

    public void StopFailing(LaunchParticipantGate gate) =>
        _remaining.TryRemove(gate, out _);

    public void ClearObservations()
    {
        _commandIds.Clear();
        _participantIds.Clear();
    }

    public IReadOnlyList<string> CommandIds(LaunchParticipantGate gate) =>
        _commandIds.TryGetValue(gate, out var commandIds) ? commandIds.ToArray() : [];

    public IReadOnlyList<string> ParticipantIds(LaunchParticipantGate gate) =>
        _participantIds.TryGetValue(gate, out var participantIds) ? participantIds.ToArray() : [];

    public Task OnPrepareJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.PrepareJob, jobKey, commandId);

    public Task OnEnsureInitialLaunchAsync(string sessionId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.EnsureInitialLaunch, sessionId, commandId);

    public Task OnParentLinkCommittedAsync(string edgeId, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.ParentLinkCommitted, edgeId, commandId);

    public Task OnSubmitJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(LaunchParticipantGate.SubmitJob, jobKey, commandId);

    private Task RecordAndMaybeThrow(LaunchParticipantGate gate, string participantId, string commandId)
    {
        _participantIds.GetOrAdd(gate, _ => new()).Enqueue(participantId);
        _commandIds.GetOrAdd(gate, _ => new()).Enqueue(commandId);
        while (_remaining.TryGetValue(gate, out var current) && current > 0)
        {
            if (_remaining.TryUpdate(gate, current - 1, current))
            {
                throw new InvalidOperationException(
                    $"Simulated participant failure at {gate}.");
            }
        }
        return Task.CompletedTask;
    }
}
