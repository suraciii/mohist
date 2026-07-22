using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowProfileCatalog
{
    public const string LocalId = "mohist/local";
    public const string GithubPrId = "mohist/github-pr";

    private const string LocalDefinitionFileName = "mohist-local.workflow.yaml";
    private const string GithubPrDefinitionFileName = "mohist-github-pr.workflow.yaml";
    private const string LocalName = "Mohist Local";
    private const string GithubPrName = "Mohist GitHub PR";
    private const string LocalDescription = "Default general-purpose Mohist pipeline: plan (proposal, specs, design, tasks, self-review) → build → check (AI review, merge readiness) → integrate (archive, merge, push).\nRequires human approval at the plan and check stages, then squashes and pushes the working branch directly into the repository base branch.\nTypical duration: 20-60 minutes for a focused change.\nNot suited for: trivial one-line fixes, throwaway spikes, or quick experiments — these don't warrant a full plan-check-integrate cycle.";
    private const string GithubPrDescription = "Default general-purpose Mohist pipeline that delivers through a GitHub PR: plan (proposal, specs, design, tasks, self-review, open draft PR) → build (load tasks, verify) → check (AI review, push, mark PR ready, verify PR checks) → integrate (archive, push, merge PR).\nRequires human approval at the plan and check stages. The workflow opens a draft PR as the last plan task, marks it ready after the check stage approves it, and squash-merges it into the repository base branch on integrate completion.\nTypical duration: 20-60 minutes for a focused change.\nChoose this over mohist/local when you want each issue to ship as a reviewable, traceable GitHub PR.\nNot suited for: trivial one-line fixes, throwaway spikes, or quick experiments — these don't warrant a full plan-check-integrate cycle.\nRequires the `gh` CLI on the runner host and `gh auth login` against the target repository.";
    private static readonly Lazy<WorkflowProfile> LocalProfile = new(() => LoadProfile(LocalDefinitionFileName, LocalId, LocalName, LocalDescription));
    private static readonly Lazy<WorkflowProfile> GithubPrProfile = new(() => LoadProfile(GithubPrDefinitionFileName, GithubPrId, GithubPrName, GithubPrDescription));

    public static StringComparer IdComparer { get; } = StringComparer.OrdinalIgnoreCase;
    public static WorkflowProfile Profile => LocalProfile.Value;
    public static WorkflowProfile GithubPrProfileAsset => GithubPrProfile.Value;
    public static WorkflowDefinition Definition => Profile.Definition;
    public static WorkflowDefinition GithubPrWorkflowDefinition => GithubPrProfileAsset.Definition;
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

    public static WorkflowDefinition ParseYaml(string yaml) => WorkflowYamlSerializer.FromYaml(yaml);

    public static WorkflowDefinition? GetDefinition(string profileId)
    {
        return GetProfile(profileId)?.Definition;
    }

    public static WorkflowProfile? GetProfile(string profileId) =>
        IdComparer.Equals(profileId, LocalId) ? Profile :
        IdComparer.Equals(profileId, GithubPrId) ? GithubPrProfileAsset : null;

    private static WorkflowProfile LoadProfile(string fileName, string id, string name, string description)
    {
        var path = ResolveDefinitionPath(fileName)
            ?? throw new FileNotFoundException($"Workflow definition not found: {fileName}");
        return new WorkflowProfile(id, name, description, WorkflowYamlSerializer.FromYaml(File.ReadAllText(path)));
    }

    private static string? ResolveDefinitionPath(string fileName)
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "Workflow", "Services", "Profiles", fileName);
        if (File.Exists(primary)) return primary;

        var sourceProbe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Workflow", "Services", "Profiles", fileName);
        return File.Exists(sourceProbe) ? Path.GetFullPath(sourceProbe) : null;
    }
}
