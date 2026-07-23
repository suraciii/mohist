namespace Mohist.Server.Workflow.Grains.Coordinator;

/// <summary>
/// issue-477 T-001: narrow Project-side participant that commits the
/// Project default WorkflowProfile binding. Consumed only by the
/// <c>WorkflowProfileReferenceCoordinator</c>; the coordinator captures
/// the Project's binding revision, the participant re-validates Profile
/// membership, and the participant writes the nullable backing key
/// only when the Profile is a custom (non-built-in) row. Built-in
/// bindings leave the backing key null so the FK backstop has no insert
/// to mediate.
/// </summary>
public interface IProjectWorkflowProfileBindingParticipant : IGrainWithStringKey
{
    Task<ProjectWorkflowProfileBindingOutcome> SetDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision);

    Task<long> GetWorkflowProfileBindingRevisionAsync();
}

public enum ProjectWorkflowProfileBindingOutcome
{
    Applied = 0,
    AlreadyApplied = 1,
    ProjectNotFound = 2,
    ProfileUnknown = 3,
}
