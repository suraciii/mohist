using Microsoft.AspNetCore.Http;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class AgentRoutes
{
    public static WebApplication MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agent")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/status", async (HttpContext context, RunnerStatusService runnerStatus, WorkflowActivityQuerier projection) =>
        {
            var project = context.GetResolvedProject();
            var runners = await runnerStatus.GetOnlineRunnersAsync(project.Id);
            var activeAgents = await projection.ListActiveAgentsAsync(project.Id);
            var capacity = SumCapacity(runners);

            return ApiResults.Ok(AgentStatusResponse.Create(activeAgents, runners, capacity));
        });

        group.MapGet("/sessions", async (HttpContext context, string? status, int? limit, AgentSessionListAssembler sessions) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await sessions.ListCurrentAsync(project.Id, status, limit ?? 50));
        });

        group.MapGet("/activity", async (HttpContext context, int? limit, AgentActivityFeedAssembler activityFeed, IssueQuerier issues, RunnerStatusService runnerStatus, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var capacity = await runnerStatus.GetCapacityAsync(project.Id);
            var waiting = await BuildWaitingCardsAsync(issues, project.Id, ct);
            return ApiResults.Ok(await activityFeed.GetActivityAsync(project.Id, limit, waiting: waiting, capacity: capacity, ct: ct));
        });

        group.MapGet("/usage", async (HttpContext context, string? range, AgentUsageReporter usage, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            if (!TryParseRange(range, out var windowDays, out var rangeError))
                return rangeError;

            return ApiResults.Ok(await usage.GetUsageTimeseriesAsync(project.Id, windowDays, ct));
        });

        group.MapGet("/cost", async (HttpContext context, string? range, AgentUsageReporter usage, IssueQuerier issues, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            if (!TryParseRange(range, out var windowDays, out var rangeError))
                return rangeError;

            var cost = await usage.GetCostRollupAsync(project.Id, ct);
            var projectIssues = await issues.ListAsync(project.Id, project, all: true);
            var doneIssuesCount = projectIssues.Count(i => i.Status == "done");
            var costPerShip = BuildCostPerShip(cost.TotalCost, doneIssuesCount);
            var windowed = await usage.GetCostWindowedAsync(project.Id, windowDays, ct);

            return ApiResults.Ok(new AgentCostRollupDto(
                cost.TotalCost,
                cost.TodayCost,
                doneIssuesCount,
                costPerShip,
                windowed.CurrentWindow,
                windowed.PreviousWindow));
        });

        return app;
    }

    private static bool TryParseRange(string? range, out int? windowDays, out IResult? error)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            windowDays = null;
            error = null;
            return true;
        }

        if (!MetricsRange.TryParse(range, out var days))
        {
            windowDays = null;
            error = ApiResults.BadRequest(
                "Unsupported range value. Accepted values: '7d', '30d', '90d'.",
                "unsupported_range",
                new { range });
            return false;
        }

        windowDays = days;
        error = null;
        return true;
    }

    private static AgentCostMetricDto BuildCostPerShip(AgentCostMetricDto totalCost, int doneIssuesCount)
    {
        if (doneIssuesCount <= 0) return new AgentCostMetricDto(null, totalCost.Currency, 0);
        if (totalCost.Amount is null) return new AgentCostMetricDto(null, totalCost.Currency, 0);
        return new AgentCostMetricDto(totalCost.Amount.Value / doneIssuesCount, totalCost.Currency, 1);
    }

    private static async Task<IReadOnlyList<ActivityWaitingCardDto>> BuildWaitingCardsAsync(
        IssueQuerier issues,
        string projectId,
        CancellationToken ct)
    {
        var waiting = await issues.ListInProgressWithApprovalGateAsync(projectId);
        if (waiting.Count == 0) return [];

        return waiting
            .Select(issue => new ActivityWaitingCardDto(
                IssueNumber: issue.Number,
                IssueTitle: string.IsNullOrWhiteSpace(issue.Title) ? $"Issue #{issue.Number}" : issue.Title,
                Stage: issue.WorkflowStage,
                Label: "Needs Approval",
                RequestedAt: issue.StageApproval is null ? null : issue.StageApproval.RequestedAt.ToString("o"),
                Preview: null))
            .ToList();
    }

    private static RunnerCapacityView SumCapacity(IReadOnlyList<RunnerStatusView> runners)
    {
        var used = 0;
        var total = 0;
        foreach (var runner in runners)
        {
            var capacity = runner.Capacity;
            if (capacity is null)
                continue;

            used += capacity.UsedSlots;
            total += capacity.TotalSlots;
        }
        return new RunnerCapacityView(used, total);
    }
}

public sealed record AgentStatusResponse(
    bool Running,
    int? IssueNumber,
    IReadOnlyList<ActiveAgentDto> ActiveAgents,
    AgentCapacityResponse Capacity,
    bool RunnerAvailable,
    bool EmbeddedRunnerEnabled,
    string? RunnerMessage,
    IReadOnlyList<RunnerStatusResponse> Runners)
{
    public static AgentStatusResponse Create(
        IReadOnlyList<ActiveAgentDto> activeAgents,
        IReadOnlyList<RunnerStatusView> runners,
        RunnerCapacityView capacity)
    {
        var runnerAvailable = runners.Count > 0;
        var runnerResponses = runners
            .Select(r => new RunnerStatusResponse(
                r.Id,
                r.Kind,
                Active: r.Capacity?.UsedSlots ?? 0,
                Max: r.Capacity?.TotalSlots ?? 0))
            .ToArray();
        // Both the per-runner list and the top-level Capacity are derived from the
        // same RunnerStatusService projection so the two views are guaranteed to
        // agree. activeAgents retains its AgentSession visibility semantics and
        // is intentionally NOT used for any slot count.
        return new AgentStatusResponse(
            Running: activeAgents.Count > 0,
            IssueNumber: activeAgents.FirstOrDefault()?.IssueNumber,
            ActiveAgents: activeAgents,
            Capacity: new AgentCapacityResponse(capacity.UsedSlots, capacity.TotalSlots),
            RunnerAvailable: runnerAvailable,
            EmbeddedRunnerEnabled: false,
            RunnerMessage: runnerAvailable ? null : "No runner is connected. Start the Mohist runner process.",
            Runners: runnerResponses);
    }
}

public sealed record AgentCapacityResponse(int Active, int Max);
public sealed record RunnerStatusResponse(string Id, string Kind, int Active, int Max);
