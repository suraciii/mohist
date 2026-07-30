using Mohist.Workflow.Definition;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Descriptive face of a built-in Workflow Profile. The id, name,
/// description, and definition are sourced from <see cref="WorkflowProfile"/>
/// rather than re-declared here, and the projection of issue workflow state
/// is reached through <see cref="MohistDefaultWorkflowProjection"/> rather
/// than a member of this interface.
/// </summary>
public interface IIssueWorkflowProfile
{
    WorkflowProfile Profile { get; }
}