namespace Mohist.Server.Issue.WorkflowProfiles;

public class IssueWorkflowProfileRegistry
{
    private readonly Dictionary<string, IIssueWorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry(Workflow.Prompts.IPromptLoader promptLoader)
    {
        var defaults = new MohistDefaultIssueWorkflowProfile(promptLoader);
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

    public IReadOnlyList<WorkflowProfileInfo> List() => _profiles.Values
        .OrderByDescending(p => p.IsDefault)
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileInfo(p.Id, p.DisplayName, p.Description, p.IsDefault))
        .ToList();

    public WorkflowProfileInfo Default => ToInfo(Get(IssueWorkflowProfiles.DefaultId));

    public bool Exists(string? id) => !string.IsNullOrWhiteSpace(id) && _profiles.ContainsKey(id);

    private static WorkflowProfileInfo ToInfo(IIssueWorkflowProfile profile) =>
        new(profile.Id, profile.DisplayName, profile.Description, profile.IsDefault);
}

public sealed record WorkflowProfileInfo(string Id, string DisplayName, string Description, bool IsDefault);
