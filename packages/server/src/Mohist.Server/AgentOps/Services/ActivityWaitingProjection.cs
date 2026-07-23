using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Sessions;

namespace Mohist.Server.AgentOps.Services;

public sealed class ActivityWaitingProjection : IScopedService
{
    private readonly IssueQuerier _issues;

    public ActivityWaitingProjection(IssueQuerier issues)
    {
        _issues = issues;
    }

    public async Task<IReadOnlyList<ActivityWaitingCardDto>> ListAsync(string projectId, CancellationToken ct = default)
    {
        var waiting = await _issues.ListInProgressWithApprovalGateAsync(projectId);
        return waiting
            .Select(issue => new ActivityWaitingCardDto(
                issue.Number,
                string.IsNullOrWhiteSpace(issue.Title) ? $"Issue #{issue.Number}" : issue.Title,
                issue.WorkflowStage,
                "Needs Approval",
                issue.StageApproval?.RequestedAt.ToString("o"),
                null))
            .ToList();
    }
}
