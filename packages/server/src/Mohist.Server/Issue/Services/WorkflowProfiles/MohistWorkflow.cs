using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string DefaultDefinitionFileName = "mohist-default.workflow.yaml";
    private const string GithubPrDefinitionFileName = "mohist-github-pr.workflow.yaml";
    private static readonly Lazy<WorkflowDefinition> DefaultDefinition = new(LoadDefaultDefinition);
    private static readonly Lazy<WorkflowDefinition> GithubPrDefinition = new(LoadGithubPrDefinition);

    public static WorkflowDefinition Definition => DefaultDefinition.Value;
    public static WorkflowDefinition GithubPrWorkflowDefinition => GithubPrDefinition.Value;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, IssueWorkflowProfiles.DefaultId);

    public static WorkflowDefinition LoadDefinitionForProfile(string profileId)
    {
        if (string.Equals(profileId, IssueWorkflowProfiles.GithubPrId, StringComparison.OrdinalIgnoreCase))
            return GithubPrWorkflowDefinition;
        return Definition;
    }

    private static WorkflowDefinition LoadDefaultDefinition()
    {
        var path = ResolveDefinitionPath(DefaultDefinitionFileName);
        if (path is null)
            throw new FileNotFoundException($"Default Mohist workflow definition not found: {DefaultDefinitionFileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.DefaultId);
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
