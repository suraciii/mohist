using Mohist.Server.Infrastructure.Hosting;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class IssueWorkflowProfileRegistry : IScopedService
{
    private readonly Dictionary<string, WorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry()
    {
        var defaults = new MohistLocalIssueWorkflowProfile().Profile;
        var githubPr = new MohistGithubPrIssueWorkflowProfile().Profile;
        _profiles = new Dictionary<string, WorkflowProfile>(IssueWorkflowProfiles.IdComparer)
        {
            [defaults.Id] = defaults,
            [githubPr.Id] = githubPr,
        };
    }

    public WorkflowProfile Get(string? id)
    {
        var profileId = string.IsNullOrWhiteSpace(id) ? IssueWorkflowProfiles.LocalId : id;
        if (_profiles.TryGetValue(profileId, out var profile)) return profile;
        throw new KeyNotFoundException($"WorkflowProfile '{profileId}' not found");
    }

    public IReadOnlyList<WorkflowProfileInfo> List() => _profiles.Values
        .OrderByDescending(p => IsDefault(p.Id))
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileInfo(p.Id, p.Name, p.Description, IsDefault(p.Id)))
        .ToList();

    public IReadOnlyList<WorkflowProfileDescription> ListDescribed() => _profiles.Values
        .OrderByDescending(p => IsDefault(p.Id))
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileDescription(p.Id, p.Name, p.Description))
        .ToList();

    public WorkflowProfileInfo Default => ToInfo(Get(IssueWorkflowProfiles.LocalId));

    public bool Exists(string? id) => !string.IsNullOrWhiteSpace(id) && _profiles.ContainsKey(id);

    public string? CanonicalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _profiles.TryGetValue(id, out var profile) ? profile.Id : null;
    }

    private static WorkflowProfileInfo ToInfo(WorkflowProfile profile) =>
        new(profile.Id, profile.Name, profile.Description, IsDefault(profile.Id));

    private static bool IsDefault(string profileId) =>
        IssueWorkflowProfiles.IdComparer.Equals(profileId, IssueWorkflowProfiles.LocalId);
}

public sealed record WorkflowProfileInfo(string Id, string DisplayName, string Description, bool IsDefault);

public sealed record WorkflowProfileDescription(string Id, string DisplayName, string Description);