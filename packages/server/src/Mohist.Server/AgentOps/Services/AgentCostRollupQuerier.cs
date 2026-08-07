using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions;

namespace Mohist.Server.AgentOps.Services;

/// <summary>
/// Composes the read-only cost rollup behind
/// <c>GET /api/projects/{projectRef}/agent/cost</c>: the all-time
/// cumulative cost (total + today) from <see cref="AgentUsageReporter"/>,
/// the project-scoped done-issue count from <see cref="IssueQuerier"/>,
/// the derived cost-per-ship, and the windowed current/previous-window
/// spend + per-issue-cost from <see cref="AgentUsageReporter"/>. Pure
/// refactor: the response shape is byte-for-byte identical to the
/// pre-split route handler.
/// </summary>
public sealed class AgentCostRollupQuerier : IScopedService
{
    private readonly AgentUsageReporter _usage;
    private readonly IssueQuerier _issues;

    public AgentCostRollupQuerier(AgentUsageReporter usage, IssueQuerier issues)
    {
        _usage = usage;
        _issues = issues;
    }

    public async Task<AgentCostRollupDto> GetCostRollupAsync(
        string projectId,
        int? windowDays = null,
        CancellationToken ct = default)
    {
        var cost = await _usage.GetCostRollupAsync(projectId, ct);
        var projectIssues = await _issues.ListAsync(projectId, project: null, all: true);
        var doneIssuesCount = projectIssues.Count(i => i.Status == "done");
        var costPerShip = BuildCostPerShip(cost.TotalCost, doneIssuesCount);
        var windowed = await _usage.GetCostWindowedAsync(projectId, windowDays, ct);

        return new AgentCostRollupDto(
            cost.TotalCost,
            cost.TodayCost,
            doneIssuesCount,
            costPerShip,
            windowed.CurrentWindow,
            windowed.PreviousWindow);
    }

    private static AgentCostMetricDto BuildCostPerShip(AgentCostMetricDto totalCost, int doneIssuesCount)
    {
        if (doneIssuesCount <= 0) return new AgentCostMetricDto(null, totalCost.Currency, 0);
        if (totalCost.Amount is null) return new AgentCostMetricDto(null, totalCost.Currency, 0);
        return new AgentCostMetricDto(totalCost.Amount.Value / doneIssuesCount, totalCost.Currency, 1);
    }
}
