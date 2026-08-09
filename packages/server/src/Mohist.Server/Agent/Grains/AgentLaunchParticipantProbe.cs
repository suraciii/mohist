namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Optional seam invoked by <see cref="AgentLaunchCoordinatorGrain"/>
/// after each participant call succeeds and before the coordinator advances
/// its durable fence. The production default is
/// <see cref="NoopAgentLaunchParticipantProbe"/>, which makes the seam
/// invisible; tests substitute a probe that throws to exercise a
/// acknowledgement loss after a durable participant write and verify same-key
/// recovery. The coordinator does not advance past a throwing probe,
/// so the persisted <c>Pending</c> fence stays on the current step and
/// the next retry resumes it.
/// </summary>
public interface IAgentLaunchParticipantProbe
{
    Task OnPlanPersistedAsync(string idempotencyKey, string inputId);
    Task OnPrepareJobAsync(string jobKey, string commandId);
    Task OnReserveLinkAsync(string edgeId, string commandId);
    Task OnEnsureInitialLaunchAsync(string sessionId, string commandId);
    Task OnParentLinkCommittedAsync(string edgeId, string commandId);
    Task OnSubmitJobAsync(string jobKey, string commandId);
}

public sealed class NoopAgentLaunchParticipantProbe : IAgentLaunchParticipantProbe
{
    public static readonly NoopAgentLaunchParticipantProbe Instance = new();

    private NoopAgentLaunchParticipantProbe()
    {
    }

    public Task OnPlanPersistedAsync(string idempotencyKey, string inputId) => Task.CompletedTask;
    public Task OnPrepareJobAsync(string jobKey, string commandId) => Task.CompletedTask;
    public Task OnReserveLinkAsync(string edgeId, string commandId) => Task.CompletedTask;
    public Task OnEnsureInitialLaunchAsync(string sessionId, string commandId) => Task.CompletedTask;
    public Task OnParentLinkCommittedAsync(string edgeId, string commandId) => Task.CompletedTask;
    public Task OnSubmitJobAsync(string jobKey, string commandId) => Task.CompletedTask;
}
