using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Resolves the effective next-start Workflow Profile selection for an
/// Issue and, alongside it, the inheritance source the effective id came
/// from: an explicit Issue selection, the Project default, or the first
/// enabled system Profile. The source distinguishes the next-start
/// selection from the binding of a run that already started (#1099).
/// </summary>
public sealed class EffectiveWorkflowProfileResolver : IScopedService
{
    public const string SourceIssue = "issue";
    public const string SourceProjectDefault = "project-default";
    public const string SourceSystem = "system";

    private readonly IssueWorkflowProfileRegistry _registry;

    public EffectiveWorkflowProfileResolver(IssueWorkflowProfileRegistry registry)
    {
        _registry = registry;
    }

    public string? Resolve(string? issueSelection, string? projectDefaultId) =>
        ResolveWithSource(issueSelection, projectDefaultId).ProfileId;

    public string? Resolve(string? issueSelection, string? projectDefaultId, IReadOnlyCollection<string>? disabledIds) =>
        ResolveWithSource(issueSelection, projectDefaultId, disabledIds).ProfileId;

    public (string? ProfileId, string? Source) ResolveWithSource(string? issueSelection, string? projectDefaultId) =>
        ResolveCoreWithSource(
            issueSelection,
            projectDefaultId,
            _registry.Exists,
            systemProfileIds: _registry.List().Select(p => p.Id).ToList());

    public (string? ProfileId, string? Source) ResolveWithSource(
        string? issueSelection,
        string? projectDefaultId,
        IReadOnlyCollection<string>? disabledIds) =>
        ResolveCoreWithSource(
            issueSelection,
            projectDefaultId,
            _registry.Exists,
            disabledIds,
            _registry.List().Select(p => p.Id).ToList());

    public static string? ResolveCore(
        string? issueSelection,
        string? projectDefaultId,
        Func<string, bool> exists,
        IReadOnlyCollection<string>? disabledIds = null,
        IReadOnlyCollection<string>? systemProfileIds = null) =>
        ResolveCoreWithSource(issueSelection, projectDefaultId, exists, disabledIds, systemProfileIds).ProfileId;

    public static (string? ProfileId, string? Source) ResolveCoreWithSource(
        string? issueSelection,
        string? projectDefaultId,
        Func<string, bool> exists,
        IReadOnlyCollection<string>? disabledIds = null,
        IReadOnlyCollection<string>? systemProfileIds = null)
    {
        var disabledSet = disabledIds is null
            ? null
            : new HashSet<string>(disabledIds, IssueWorkflowProfiles.IdComparer);
        bool isEnabled(string id) => exists(id) && (disabledSet == null || !disabledSet.Contains(id));

        if (!string.IsNullOrWhiteSpace(issueSelection) && isEnabled(issueSelection))
            return (issueSelection, SourceIssue);

        if (!string.IsNullOrWhiteSpace(projectDefaultId) && isEnabled(projectDefaultId))
            return (projectDefaultId, SourceProjectDefault);

        if (systemProfileIds is not null)
        {
            foreach (var profileId in systemProfileIds)
            {
                if (isEnabled(profileId))
                    return (profileId, SourceSystem);
            }
            return (null, null);
        }

        if (disabledSet is not null)
            return (null, null);

        return (IssueWorkflowProfiles.LocalId, SourceSystem);
    }
}
