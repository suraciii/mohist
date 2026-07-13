using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string LocalDefinitionFileName = "mohist-local.workflow.yaml";
    private const string GithubPrDefinitionFileName = "mohist-github-pr.workflow.yaml";
    private static readonly Lazy<WorkflowDefinition> LocalDefinition = new(LoadLocalDefinition);
    private static readonly Lazy<WorkflowDefinition> GithubPrDefinition = new(LoadGithubPrDefinition);

    public static WorkflowDefinition Definition => LocalDefinition.Value;
    public static WorkflowDefinition GithubPrWorkflowDefinition => GithubPrDefinition.Value;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, IssueWorkflowProfiles.LocalId);

    public static WorkflowDefinition LoadDefinitionForProfile(string profileId)
    {
        if (string.Equals(profileId, IssueWorkflowProfiles.GithubPrId, StringComparison.OrdinalIgnoreCase))
            return GithubPrWorkflowDefinition;
        return Definition;
    }

    private const string MissingDescriptionFallback = "No description provided";

    public static string ResolveDescription(WorkflowDefinition definition)
    {
        var description = definition?.Description;
        return string.IsNullOrWhiteSpace(description) ? MissingDescriptionFallback : description!.TrimEnd();
    }

    private static WorkflowDefinition LoadLocalDefinition()
    {
        return WorkflowYamlSerializer.FromYaml(
            ReadDefinitionResource(LocalDefinitionFileName),
            IssueWorkflowProfiles.LocalId);
    }

    private static WorkflowDefinition LoadGithubPrDefinition()
    {
        return WorkflowYamlSerializer.FromYaml(
            ReadDefinitionResource(GithubPrDefinitionFileName),
            IssueWorkflowProfiles.GithubPrId);
    }

    private static string ReadDefinitionResource(string fileName) =>
        AssemblyTextResources.Read(
            typeof(MohistWorkflow).Assembly,
            $"Mohist.Server.WorkflowProfiles.{fileName}");
}
