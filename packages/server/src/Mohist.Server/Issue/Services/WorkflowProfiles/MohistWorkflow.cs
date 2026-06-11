using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string DefinitionFileName = "mohist-default.workflow.yaml";
    private const string GitHubDefinitionFileName = "mohist-github.workflow.yaml";
    private static readonly Lazy<WorkflowDefinition> DefaultDefinition = new(LoadDefaultDefinition);
    private static readonly Lazy<WorkflowDefinition> GitHubDefinition = new(LoadGitHubDefinition);

    public static WorkflowDefinition Definition => DefaultDefinition.Value;

    public static WorkflowDefinition GitHub => GitHubDefinition.Value;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, IssueWorkflowProfiles.DefaultId);

    private static WorkflowDefinition LoadDefaultDefinition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Issue", "Services", "WorkflowProfiles", DefinitionFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Default Mohist workflow definition not found: {path}", path);
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.DefaultId);
    }

    private static WorkflowDefinition LoadGitHubDefinition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Issue", "Services", "WorkflowProfiles", GitHubDefinitionFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"GitHub Mohist workflow definition not found: {path}", path);
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), "mohist/github");
    }
}
