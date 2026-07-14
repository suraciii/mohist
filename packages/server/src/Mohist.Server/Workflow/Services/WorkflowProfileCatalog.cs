using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowProfileCatalog
{
    public const string LocalId = "mohist/local";
    public const string GithubPrId = "mohist/github-pr";

    private const string LocalDefinitionFileName = "mohist-local.workflow.yaml";
    private const string GithubPrDefinitionFileName = "mohist-github-pr.workflow.yaml";
    private const string MissingDescriptionFallback = "No description provided";
    private static readonly Lazy<WorkflowDefinition> LocalDefinition = new(LoadLocalDefinition);
    private static readonly Lazy<WorkflowDefinition> GithubPrDefinition = new(LoadGithubPrDefinition);

    public static StringComparer IdComparer { get; } = StringComparer.OrdinalIgnoreCase;
    public static WorkflowDefinition Definition => LocalDefinition.Value;
    public static WorkflowDefinition GithubPrWorkflowDefinition => GithubPrDefinition.Value;
    public static IReadOnlyList<string> SystemProfileIds { get; } = [LocalId, GithubPrId];

    public static bool IsSystemProfile(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && SystemProfileIds.Contains(profileId, IdComparer);

    public static string? ResolveEffectiveProfileId(
        string? issueSelection,
        string? projectDefaultId,
        IReadOnlyCollection<string>? disabledIds)
    {
        var disabled = disabledIds is null
            ? null
            : new HashSet<string>(disabledIds, IdComparer);

        bool isEnabled(string id) => IsSystemProfile(id) && (disabled is null || !disabled.Contains(id));

        if (!string.IsNullOrWhiteSpace(issueSelection) && isEnabled(issueSelection))
            return issueSelection;

        if (!string.IsNullOrWhiteSpace(projectDefaultId) && isEnabled(projectDefaultId))
            return projectDefaultId;

        return SystemProfileIds.FirstOrDefault(isEnabled);
    }

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml, LocalId);

    public static WorkflowDefinition? GetDefinition(string profileId)
    {
        if (IdComparer.Equals(profileId, LocalId)) return Definition;
        if (IdComparer.Equals(profileId, GithubPrId)) return GithubPrWorkflowDefinition;
        return null;
    }

    public static string ResolveDescription(WorkflowDefinition definition)
    {
        var description = definition?.Description;
        return string.IsNullOrWhiteSpace(description) ? MissingDescriptionFallback : description.TrimEnd();
    }

    private static WorkflowDefinition LoadLocalDefinition() => LoadDefinition(LocalDefinitionFileName, LocalId);

    private static WorkflowDefinition LoadGithubPrDefinition() => LoadDefinition(GithubPrDefinitionFileName, GithubPrId);

    private static WorkflowDefinition LoadDefinition(string fileName, string profileId)
    {
        var path = ResolveDefinitionPath(fileName)
            ?? throw new FileNotFoundException($"Workflow definition not found: {fileName}");
        return WorkflowYamlSerializer.FromYaml(File.ReadAllText(path), profileId);
    }

    private static string? ResolveDefinitionPath(string fileName)
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "Workflow", "Services", "Profiles", fileName);
        if (File.Exists(primary)) return primary;

        var sourceProbe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Workflow", "Services", "Profiles", fileName);
        return File.Exists(sourceProbe) ? Path.GetFullPath(sourceProbe) : null;
    }
}
