using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string DefaultDefinitionFileName = "mohist-default.workflow.yaml";
    private const string PrDefinitionFileName = "mohist-pr.workflow.yaml";
    private static readonly Lazy<WorkflowDefinition> DefaultDefinition = new(LoadDefaultDefinition);
    private static readonly Lazy<WorkflowDefinition> PrDefinition = new(LoadPrDefinition);

    public static WorkflowDefinition Definition => DefaultDefinition.Value;
    public static WorkflowDefinition PrWorkflowDefinition => PrDefinition.Value;

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, IssueWorkflowProfiles.DefaultId);

    public static WorkflowDefinition LoadDefinitionForProfile(string profileId)
    {
        if (string.Equals(profileId, IssueWorkflowProfiles.PrId, StringComparison.OrdinalIgnoreCase))
            return PrWorkflowDefinition;
        return Definition;
    }

    private static WorkflowDefinition LoadDefaultDefinition()
    {
        var path = ResolveDefinitionPath(DefaultDefinitionFileName);
        if (path is null)
            throw new FileNotFoundException($"Default Mohist workflow definition not found: {DefaultDefinitionFileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.DefaultId);
    }

    private static WorkflowDefinition LoadPrDefinition()
    {
        var path = ResolveDefinitionPath(PrDefinitionFileName);
        if (path is null)
            throw new FileNotFoundException($"Mohist PR workflow definition not found: {PrDefinitionFileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), IssueWorkflowProfiles.PrId);
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
