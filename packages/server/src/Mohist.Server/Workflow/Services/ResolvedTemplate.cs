using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

public sealed record ResolvedTemplate(WorkflowProfile? Profile)
{
    public static readonly ResolvedTemplate None = new((WorkflowProfile?)null);

    public string? Id => Profile?.Id;
    public WorkflowDefinition? Structure => Profile?.Definition;

    public static ResolvedTemplate FromProfile(WorkflowProfile? profile) =>
        profile is null ? None : new(profile);

}
