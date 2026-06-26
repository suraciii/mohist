using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Single source of truth for an issue's effective workflow profile id.
///
/// Resolves the effective profile id via the precedence:
///   1. Issue-level <see cref="Domain.Issue.WorkflowProfileId"/> when set and
///      present in the registry.
///   2. Project default template id (from
///      <c>ProjectWorkflowProfile.DefaultTemplateId</c>) when set and present
///      in the registry.
///   3. System default (<see cref="IssueWorkflowProfiles.LocalId"/>).
///
/// Every read surface (issue detail, list, workflow-profile endpoint,
/// <c>mo issue show</c>) MUST go through this resolver so they cannot diverge.
/// Unknown ids never throw at read time — they are treated as "no selection"
/// and the resolver falls through to the next layer, mirroring the spec's
/// requirement that no read surface invents a default independent of the
/// resolved value.
/// </summary>
public sealed class EffectiveWorkflowProfileResolver : IScopedService
{
    private readonly IssueWorkflowProfileRegistry _registry;

    public EffectiveWorkflowProfileResolver(IssueWorkflowProfileRegistry registry)
    {
        _registry = registry;
    }

    public string Resolve(string? issueSelection, string? projectDefaultId) =>
        ResolveCore(issueSelection, projectDefaultId, _registry.Exists);

    /// <summary>
    /// Pure helper used by tests and callers that already hold an
    /// existence check (e.g. when calling from a static method or a
    /// non-DI context).
    /// </summary>
    public static string ResolveCore(
        string? issueSelection,
        string? projectDefaultId,
        Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(issueSelection) && exists(issueSelection))
            return issueSelection;
        if (!string.IsNullOrWhiteSpace(projectDefaultId) && exists(projectDefaultId))
            return projectDefaultId;
        return IssueWorkflowProfiles.LocalId;
    }
}