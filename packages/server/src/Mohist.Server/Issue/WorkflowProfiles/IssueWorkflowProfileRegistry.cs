using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.WorkflowProfiles;

public class IssueWorkflowProfileRegistry
{
    private readonly Dictionary<string, IIssueWorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry()
    {
        var defaults = new MohistDefaultIssueWorkflowProfile();
        _profiles = new Dictionary<string, IIssueWorkflowProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [defaults.Id] = defaults,
        };
    }

    public IIssueWorkflowProfile Get(string? id)
    {
        var profileId = string.IsNullOrWhiteSpace(id) ? IssueWorkflowProfiles.DefaultId : id;
        if (_profiles.TryGetValue(profileId, out var profile)) return profile;
        throw new InvalidOperationException($"Workflow profile '{profileId}' not found");
    }
}
