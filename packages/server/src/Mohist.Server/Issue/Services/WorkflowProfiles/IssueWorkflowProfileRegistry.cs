using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class IssueWorkflowProfileRegistry : IScopedService
{
    private readonly Dictionary<string, IIssueWorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry(ProjectPromptStore promptStore)
    {
        var defaults = new MohistLocalIssueWorkflowProfile(promptStore);
        var githubPr = new MohistGithubPrIssueWorkflowProfile(promptStore);
        _profiles = new Dictionary<string, IIssueWorkflowProfile>(IssueWorkflowProfiles.IdComparer)
        {
            [defaults.Id] = defaults,
            [githubPr.Id] = githubPr,
        };
    }

    public IIssueWorkflowProfile Get(string? id)
    {
        var profileId = string.IsNullOrWhiteSpace(id) ? IssueWorkflowProfiles.LocalId : id;
        if (_profiles.TryGetValue(profileId, out var profile)) return profile;
        throw new KeyNotFoundException($"WorkflowProfile '{profileId}' not found");
    }

    public IReadOnlyList<WorkflowProfileInfo> List() => _profiles.Values
        .OrderByDescending(p => p.IsDefault)
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileInfo(p.Id, p.DisplayName, p.Description, p.IsDefault))
        .ToList();

    public IReadOnlyList<WorkflowProfileDescription> ListDescribed() => _profiles.Values
        .OrderByDescending(p => p.IsDefault)
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileDescription(p.Id, p.DisplayName, p.Description))
        .ToList();

    public WorkflowProfileInfo Default => ToInfo(Get(IssueWorkflowProfiles.LocalId));

    public bool Exists(string? id) => !string.IsNullOrWhiteSpace(id) && _profiles.ContainsKey(id);

    public string? CanonicalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _profiles.TryGetValue(id, out var profile) ? profile.Id : null;
    }

    private static WorkflowProfileInfo ToInfo(IIssueWorkflowProfile profile) =>
        new(profile.Id, profile.DisplayName, profile.Description, profile.IsDefault);
}

public sealed record WorkflowProfileInfo(string Id, string DisplayName, string Description, bool IsDefault);

public sealed record WorkflowProfileDescription(string Id, string DisplayName, string Description);
