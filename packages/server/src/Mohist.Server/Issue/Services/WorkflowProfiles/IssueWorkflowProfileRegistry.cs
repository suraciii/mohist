using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class IssueWorkflowProfileRegistry
{
    private readonly Dictionary<string, IIssueWorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        var defaults = new MohistDefaultIssueWorkflowProfile(promptLoader, dbFactory);
        var quickFix = new MohistQuickFixIssueWorkflowProfile(promptLoader, dbFactory);
        var experiment = new MohistExperimentIssueWorkflowProfile(promptLoader, dbFactory);
        _profiles = new Dictionary<string, IIssueWorkflowProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [defaults.Id] = defaults,
            [quickFix.Id] = quickFix,
            [experiment.Id] = experiment,
        };
    }

    public IIssueWorkflowProfile Get(string? id)
    {
        var profileId = string.IsNullOrWhiteSpace(id) ? IssueWorkflowProfiles.DefaultId : id;
        if (_profiles.TryGetValue(profileId, out var profile)) return profile;
        throw new KeyNotFoundException($"WorkflowProfile '{profileId}' not found");
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
