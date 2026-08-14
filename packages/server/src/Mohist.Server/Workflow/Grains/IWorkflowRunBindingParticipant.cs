namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// WorkflowRun-side participant that creates the complete durable startup
/// binding in one store transaction. Consumed only by the
/// <c>WorkflowProfileReferenceCoordinator</c>.
/// </summary>
public interface IWorkflowRunBindingParticipant : IGrainWithStringKey
{
    Task<WorkflowRunBindingResult> GetBindingAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun request);

    Task<WorkflowRunBindingResult> BindAsync(
        BoundWorkflowStart payload,
        string commandId,
        long? expectedRevision);
}

public enum WorkflowRunBindingOutcome
{
    Applied = 0,
    AlreadyApplied = 1,
    RunNotFound = 2,
    ProfileUnknown = 3,
    Conflict = 4,
}

[GenerateSerializer]
public sealed record WorkflowRunBindingResult(
    [property: Id(0)] WorkflowRunBindingOutcome Outcome,
    [property: Id(1)] BoundWorkflowStart? Binding = null,
    [property: Id(2)] string? Message = null);
