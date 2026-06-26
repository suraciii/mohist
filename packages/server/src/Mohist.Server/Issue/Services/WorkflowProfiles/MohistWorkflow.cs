using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

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

    private static WorkflowDefinition LoadLocalDefinition()
    {
        var path = ResolveDefinitionPath(LocalDefinitionFileName);
        if (path is null)
            throw new FileNotFoundException($"Local Mohist workflow definition not found: {LocalDefinitionFileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.LocalId);
    }

    private static WorkflowDefinition LoadGithubPrDefinition()
    {
        var path = ResolveDefinitionPath(GithubPrDefinitionFileName);
        if (path is null)
            throw new FileNotFoundException($"Mohist GitHub PR workflow definition not found: {GithubPrDefinitionFileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.GithubPrId);
    }

    private static string? ResolveDefinitionPath(string fileName)
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "Issue", "Services", "WorkflowProfiles", fileName);
        if (File.Exists(primary)) return primary;

        var sourceProbe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Issue", "Services", "WorkflowProfiles", fileName);
        if (File.Exists(sourceProbe)) return Path.GetFullPath(sourceProbe);

        return null;
    }
}
