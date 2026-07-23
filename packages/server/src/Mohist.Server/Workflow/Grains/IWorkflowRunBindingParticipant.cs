namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// issue-477 T-001: narrow WorkflowRun-side participant that commits
/// the Profile ID binding. Consumed only by the
/// <c>WorkflowProfileReferenceCoordinator</c>. The participant writes
/// the nullable backing key only when the Profile is a custom
/// (non-built-in) row; built-in bindings leave the backing key null.
/// </summary>
public interface IWorkflowRunBindingParticipant : IGrainWithStringKey
{
    Task<WorkflowRunBindingOutcome> BindAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision);

    Task<WorkflowRunBindingOutcome> ClearBindingAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision);
}

public enum WorkflowRunBindingOutcome
{
    Applied = 0,
    AlreadyApplied = 1,
    RunNotFound = 2,
    ProfileUnknown = 3,
}
