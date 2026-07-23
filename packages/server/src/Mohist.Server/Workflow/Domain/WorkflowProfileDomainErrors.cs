namespace Mohist.Server.Workflow.Domain;

/// <summary>
/// issue-477 T-001: domain error carried by the WorkflowProfile collection
/// when callers attempt to mutate a built-in Profile. Distinct from the
/// Definition / Action-contract validation surface and the
/// deletion-blocker surface so the API can map them to the right error
/// envelope in spec tests.
/// </summary>
public sealed class WorkflowProfileReadOnlyException : Exception
{
    public WorkflowProfileReadOnlyException(string profileId)
        : base($"WorkflowProfile '{profileId}' is a built-in and cannot be modified.")
    {
        ProfileId = profileId;
    }

    public string ProfileId { get; }
}

/// <summary>
/// issue-477 T-001: thrown by the collection provider when a save request
/// collides with an existing Profile at the same <c>(ProjectId, ProfileId)</c>.
/// </summary>
public sealed class WorkflowProfileAlreadyExistsException : Exception
{
    public WorkflowProfileAlreadyExistsException(string projectId, string profileId)
        : base($"WorkflowProfile '{profileId}' already exists in project '{projectId}'.")
    {
        ProjectId = projectId;
        ProfileId = profileId;
    }

    public string ProjectId { get; }
    public string ProfileId { get; }
}

/// <summary>
/// issue-477 T-001: thrown when a custom update / delete cannot find the
/// target <c>(ProjectId, ProfileId)</c> row. The custom-Profile foreign-key
/// backstop on Issue selection / Run binding translates this into a
/// retryable workflow-profile-not-found conflict for the caller that lost
/// the race.
/// </summary>
public sealed class WorkflowProfileNotFoundException : Exception
{
    public WorkflowProfileNotFoundException(string projectId, string profileId)
        : base($"WorkflowProfile '{profileId}' not found in project '{projectId}'.")
    {
        ProjectId = projectId;
        ProfileId = profileId;
    }

    public string ProjectId { get; }
    public string ProfileId { get; }
}
