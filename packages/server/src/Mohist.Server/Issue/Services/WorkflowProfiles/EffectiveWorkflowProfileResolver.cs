using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class EffectiveWorkflowProfileResolver : IScopedService
{
    private readonly IssueWorkflowProfileRegistry _registry;

    public EffectiveWorkflowProfileResolver(IssueWorkflowProfileRegistry registry)
    {
        _registry = registry;
    }

    public string? Resolve(string? issueSelection, string? projectDefaultId) =>
        ResolveCore(issueSelection, projectDefaultId, _registry.Exists, systemProfileIds: _registry.List().Select(p => p.Id).ToList());

    public string? Resolve(string? issueSelection, string? projectDefaultId, IReadOnlyCollection<string>? disabledIds) =>
        ResolveCore(issueSelection, projectDefaultId, _registry.Exists, disabledIds, _registry.List().Select(p => p.Id).ToList());

    public static string? ResolveCore(
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
            return issueSelection;

        if (!string.IsNullOrWhiteSpace(projectDefaultId) && isEnabled(projectDefaultId))
            return projectDefaultId;

        if (systemProfileIds is not null)
        {
            foreach (var profileId in systemProfileIds)
            {
                if (isEnabled(profileId))
                    return profileId;
            }
            return null;
        }

        if (disabledSet is not null)
            return null;

        return IssueWorkflowProfiles.LocalId;
    }
}
