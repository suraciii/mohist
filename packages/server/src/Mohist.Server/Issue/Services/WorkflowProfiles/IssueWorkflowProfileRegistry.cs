using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class IssueWorkflowProfileRegistry : IScopedService
{
    private readonly Dictionary<string, IIssueWorkflowProfile> _profiles;

    public IssueWorkflowProfileRegistry(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        var defaults = new MohistDefaultIssueWorkflowProfile(promptLoader, dbFactory);
        var pr = new MohistPrIssueWorkflowProfile(promptLoader, dbFactory);
        _profiles = new Dictionary<string, IIssueWorkflowProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [defaults.Id] = defaults,
            [pr.Id] = pr,
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

    public IReadOnlyList<WorkflowProfileDescription> ListDescribed() => _profiles.Values
        .OrderByDescending(p => p.IsDefault)
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new WorkflowProfileDescription(p.Id, p.DisplayName, p.Description, p.SuitableFor))
        .ToList();

    public WorkflowProfileInfo Default => ToInfo(Get(IssueWorkflowProfiles.DefaultId));

    public bool Exists(string? id) => !string.IsNullOrWhiteSpace(id) && _profiles.ContainsKey(id);

    public bool Matches(string profileId, string? context) =>
        SuitableForMatcher.Matches(Get(profileId).SuitableFor, context);

    private static WorkflowProfileInfo ToInfo(IIssueWorkflowProfile profile) =>
        new(profile.Id, profile.DisplayName, profile.Description, profile.IsDefault);
}

public sealed record WorkflowProfileInfo(string Id, string DisplayName, string Description, bool IsDefault);

public sealed record WorkflowProfileDescription(string Id, string DisplayName, string Description, IReadOnlyList<string> SuitableFor);
