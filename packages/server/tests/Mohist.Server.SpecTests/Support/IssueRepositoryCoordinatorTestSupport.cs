using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.SpecTests.Support;

public static class IssueRepositoryCoordinatorTestSupport
{
    public static async Task CreateIssueThroughCoordinatorAsync(
        this IGrainFactory grains,
        string projectId,
        int number,
        string issueId,
        string title,
        string? body = null,
        IReadOnlyDictionary<string, string>? labels = null,
        string? priority = null,
        string? repositoryName = null,
        string? risk = null,
        bool isDraft = false,
        string[]? attachmentIds = null,
        string? workflowProfileId = null,
        int[]? prerequisiteNumbers = null)
    {
        var project = await grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        var repository = project?.GetRepository(repositoryName);
        var result = await grains.GetGrain<IIssueRepositoryCoordinatorGrain>(projectId)
            .CreateIssueAsync(
                new RepositoryCommandPayload.Create(
                    projectId,
                    number,
                    issueId,
                    repository?.Name ?? repositoryName ?? string.Empty,
                    title,
                    body,
                    labels,
                    priority,
                    risk,
                    isDraft,
                    attachmentIds,
                    workflowProfileId,
                    prerequisiteNumbers),
                $"create:{projectId}:{issueId}",
                expectedRevision: null);

        if (!result.IsApplied)
            throw new InvalidOperationException(result.Message ?? "Issue creation rejected");
    }
}
