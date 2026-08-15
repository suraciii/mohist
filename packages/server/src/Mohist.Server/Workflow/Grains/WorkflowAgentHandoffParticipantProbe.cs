namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Optional seam invoked by <see cref="WorkflowAgentHandoffGrain"/>
/// after each participant call succeeds and before the activation cursor
/// advances. The production default is
/// <see cref="NoopWorkflowAgentHandoffParticipantProbe"/>, which makes the
/// seam invisible; tests substitute a probe that throws to simulate
/// acknowledgement loss after a durable participant write. The grain does
/// not advance past a throwing probe, so the persisted activation cursor
/// stays on the current step and the next retry resumes it.
/// </summary>
public interface IWorkflowAgentHandoffParticipantProbe
{
    Task OnPrepareJobAsync(string jobKey, string commandId);
    Task OnEnsureInitialLaunchAsync(string sessionId, string commandId);
    Task OnSubmitJobAsync(string jobKey, string commandId);
}

public sealed class NoopWorkflowAgentHandoffParticipantProbe : IWorkflowAgentHandoffParticipantProbe
{
    public static readonly NoopWorkflowAgentHandoffParticipantProbe Instance = new();

    private NoopWorkflowAgentHandoffParticipantProbe()
    {
    }

    public Task OnPrepareJobAsync(string jobKey, string commandId) => Task.CompletedTask;
    public Task OnEnsureInitialLaunchAsync(string sessionId, string commandId) => Task.CompletedTask;
    public Task OnSubmitJobAsync(string jobKey, string commandId) => Task.CompletedTask;
}
