using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

// Activity convergence: the durable probe capture and the fenced settlement
// of Session Activity from Runner lifecycle evidence. Extracted from
// AgentSessionGrain to keep the main partial within the file-size ratchet.
public sealed partial class AgentSessionGrain
{
    /// <summary>
    /// Captures the outstanding activity observation for a Runner probe and
    /// returns the wire request, or null when this Session has nothing the
    /// Runner can account for. A re-registration probe captures an
    /// <c>unknown</c> Activity; an administrative removal may also capture an
    /// active one. Re-capturing unchanged state reuses the pending
    /// observation identity instead of re-snapshotting it.
    /// </summary>
    public async Task<RunnerSessionActivityProbeRequest?> PrepareActivityProbeAsync(
        string runnerId,
        bool runnerRemoved = false)
    {
        var session = await GetRequiredAsync();
        var capturedBefore = session.Status.PendingActivityObservation;
        var observation = session.CaptureActivityObservation(
            runnerId,
            Guid.NewGuid().ToString("N"),
            runnerRemoved,
            Now());
        if (observation is null)
            return null;

        if (!ReferenceEquals(capturedBefore, observation))
            await CommitAsync(session, []);

        return new RunnerSessionActivityProbeRequest(
            SessionId: session.Id,
            ObservationId: observation.ObservationId,
            RunnerId: observation.RunnerId,
            Runtime: observation.Runtime,
            RuntimeSessionId: observation.RuntimeSessionId,
            WorkDir: observation.WorkDir,
            BindingEpoch: observation.BindingEpoch,
            ContextGeneration: observation.ContextGeneration);
    }

    /// <summary>
    /// Applies one Runner answer to the captured observation. Returns false
    /// for every invalid, stale, superseded or post-capture answer: those are
    /// discarded without a partial settlement. The state transition and any
    /// Job settlement fact are persisted atomically, and the Session never
    /// awaits a Job that calls back into it.
    /// </summary>
    public async Task<bool> ApplyActivityProbeAsync(RunnerSessionActivityProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var session = await GetRequiredAsync();
        var settlement = session.SettleActivityFromEvidence(result, Now());
        if (!settlement.Applied)
            return false;

        await CommitAsync(session, settlement.Events);
        await PublishCanonicalRefreshAsync(session);
        return true;
    }
}
