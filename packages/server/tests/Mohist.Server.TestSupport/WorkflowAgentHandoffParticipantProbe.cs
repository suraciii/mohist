using System.Collections.Concurrent;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Identifies the participant call a
/// <see cref="WorkflowAgentHandoffParticipantProbe"/> failure is armed
/// against. One gate per step in the activation sequence.
/// </summary>
public enum WorkflowAgentHandoffGate
{
    PrepareJob,
    EnsureInitialLaunch,
    SubmitJob,
}

/// <summary>
/// Test-only <see cref="IWorkflowAgentHandoffParticipantProbe"/> that throws
/// on the next N acknowledgements at an armed gate, then succeeds. The
/// handoff grain calls the probe after each participant grain call succeeds
/// and before it advances the durable activation cursor, so a thrown probe
/// simulates a lost acknowledgement while leaving the participant's write
/// durable. Recovery specs arm the gate, observe the pending exception, then
/// <see cref="StopFailing"/> and retry ActivateAsync on the same command.
/// </summary>
public sealed class WorkflowAgentHandoffParticipantProbe : IWorkflowAgentHandoffParticipantProbe
{
    private readonly ConcurrentDictionary<WorkflowAgentHandoffGate, int> _remaining = new();
    private readonly ConcurrentDictionary<WorkflowAgentHandoffGate, ConcurrentQueue<string>> _commandIds = new();
    private readonly ConcurrentDictionary<WorkflowAgentHandoffGate, ConcurrentQueue<string>> _participantIds = new();

    public void FailNext(WorkflowAgentHandoffGate gate, int times = 1)
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

    public void StopFailing(WorkflowAgentHandoffGate gate) =>
        _remaining.TryRemove(gate, out _);

    public void ClearObservations()
    {
        _commandIds.Clear();
        _participantIds.Clear();
    }

    public IReadOnlyList<string> CommandIds(WorkflowAgentHandoffGate gate) =>
        _commandIds.TryGetValue(gate, out var commandIds) ? commandIds.ToArray() : [];

    public IReadOnlyList<string> ParticipantIds(WorkflowAgentHandoffGate gate) =>
        _participantIds.TryGetValue(gate, out var participantIds) ? participantIds.ToArray() : [];

    public Task OnPrepareJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(WorkflowAgentHandoffGate.PrepareJob, jobKey, commandId);

    public Task OnEnsureInitialLaunchAsync(string sessionId, string commandId) =>
        RecordAndMaybeThrow(WorkflowAgentHandoffGate.EnsureInitialLaunch, sessionId, commandId);

    public Task OnSubmitJobAsync(string jobKey, string commandId) =>
        RecordAndMaybeThrow(WorkflowAgentHandoffGate.SubmitJob, jobKey, commandId);

    private Task RecordAndMaybeThrow(WorkflowAgentHandoffGate gate, string participantId, string commandId)
    {
        _participantIds.GetOrAdd(gate, _ => new()).Enqueue(participantId);
        _commandIds.GetOrAdd(gate, _ => new()).Enqueue(commandId);
        while (_remaining.TryGetValue(gate, out var current) && current > 0)
        {
            if (_remaining.TryUpdate(gate, current - 1, current))
            {
                throw new InvalidOperationException(
                    $"Simulated workflow handoff participant failure at {gate}.");
            }
        }
        return Task.CompletedTask;
    }
}
