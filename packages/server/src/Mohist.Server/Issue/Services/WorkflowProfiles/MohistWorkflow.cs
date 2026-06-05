using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string DefinitionFileName = "mohist-default.workflow.yaml";
    private static readonly Lazy<WorkflowDefinition> DefaultDefinition = new(LoadDefaultDefinition);

    public static WorkflowDefinition Definition => DefaultDefinition.Value;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, IssueWorkflowProfiles.DefaultId);

    private static WorkflowDefinition LoadDefaultDefinition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Issue", "Services", "WorkflowProfiles", DefinitionFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Default Mohist workflow definition not found: {path}", path);
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.DefaultId);
    }
}
