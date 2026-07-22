using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    public static WorkflowDefinition Definition => WorkflowProfileCatalog.Definition;
    public static WorkflowDefinition GithubPrWorkflowDefinition => WorkflowProfileCatalog.GithubPrWorkflowDefinition;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowProfileCatalog.ParseYaml(yaml);

    public static WorkflowDefinition LoadDefinitionForProfile(string profileId)
    {
        if (string.Equals(profileId, IssueWorkflowProfiles.GithubPrId, StringComparison.OrdinalIgnoreCase))
            return GithubPrWorkflowDefinition;
        return Definition;
    }

}
