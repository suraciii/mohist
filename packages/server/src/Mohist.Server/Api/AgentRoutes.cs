using Microsoft.AspNetCore.Http;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Sessions;

namespace Mohist.Server.Api;

public static class AgentRoutes
{
    public static WebApplication MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agent")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/status", async (HttpContext context, IGrainFactory grains, WorkflowActivityQuerier projection) =>
        {
            var project = context.GetResolvedProject();
            var runners = await ListAvailableRunnersAsync(grains, project.Id);
            var persistedSlots = await LoadPersistedSlotsByRunnerAsync(grains, runners);
            var activeAgents = await projection.ListActiveAgentsAsync(project.Id);

            return ApiResults.Ok(AgentStatusResponse.Create(activeAgents, runners, persistedSlots));
        });

        group.MapGet("/sessions", async (HttpContext context, string? status, int? limit, AgentSessionQuerier sessions) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await sessions.ListCurrentAsync(project.Id, status, limit ?? 50));
        });

        group.MapGet("/activity", async (HttpContext context, int? limit, AgentSessionQuerier sessions, IssueQuerier issues, IGrainFactory grains, WorkflowActivityQuerier projection, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var runnerIds = (await ListAvailableRunnersAsync(grains, project.Id)).Select(r => r.RunnerId).ToArray();
            var waiting = await BuildWaitingCardsAsync(issues, project.Id, ct);
            return ApiResults.Ok(await sessions.GetActivityAsync(project.Id, limit, waiting: waiting, runnerIds: runnerIds, ct: ct));
        });

        group.MapGet("/usage", async (HttpContext context, AgentSessionQuerier sessions, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await sessions.GetUsageTimeseriesAsync(project.Id, ct));
        });

        group.MapGet("/cost", async (HttpContext context, AgentSessionQuerier sessions, IssueQuerier issues, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var cost = await sessions.GetCostRollupAsync(project.Id, ct);
            var projectIssues = await issues.ListAsync(project.Id, project, all: true);
            var doneIssuesCount = projectIssues.Count(i => i.Status == "done");
            var costPerShip = BuildCostPerShip(cost.TotalCost, doneIssuesCount);

            return ApiResults.Ok(new AgentCostRollupDto(
                cost.TotalCost,
                cost.TodayCost,
                doneIssuesCount,
                costPerShip));
        });

        return app;
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
                IssueId: issue.Id,
                IssueNumber: issue.Number,
                IssueTitle: string.IsNullOrWhiteSpace(issue.Title) ? $"Issue #{issue.Number}" : issue.Title,
                Stage: issue.WorkflowStage,
                Label: "Needs Approval",
                RequestedAt: issue.StageApproval is null ? null : issue.StageApproval.RequestedAt.ToString("o"),
                Preview: null))
            .ToList();
    }

    private static async Task<IReadOnlyList<RunnerInfo>> ListAvailableRunnersAsync(IGrainFactory grains, string projectId)
    {
        var globalRunners = await grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListRunnersAsync();
        var available = new List<RunnerInfo>();
        foreach (var runner in globalRunners)
        {
            var grain = grains.GetGrain<IRunnerGrain>(runner.RunnerId);
            RunnerRuntimeState runtime;
            try
            {
                runtime = await grain.GetRuntimeStateAsync();
            }
            catch
            {
                continue;
            }
            if (runtime.Status == RunnerStatus.Online)
                available.Add(runner);
        }
        return available;
    }

    private static async Task<IReadOnlyDictionary<string, int>> LoadPersistedSlotsByRunnerAsync(
        IGrainFactory grains,
        IReadOnlyList<RunnerInfo> runners)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var runner in runners)
        {
            var grain = grains.GetGrain<IRunnerGrain>(runner.RunnerId);
            slots[runner.RunnerId] = await grain.GetSlotsAsync();
        }
        return slots;
    }
}

public sealed record AgentStatusResponse(
    bool Running,
    string? IssueId,
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
        IReadOnlyList<RunnerInfo> runners,
        IReadOnlyDictionary<string, int> persistedSlotsByRunner)
    {
        var runnerAvailable = runners.Count > 0;
        var activeSlotsByRunner = activeAgents
            .GroupBy(a => a.RunnerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var runnerResponses = runners
            .Select(r =>
            {
                // Persisted slots are the sole authoritative source for
                // dispatch capacity.
                var maxSlots = persistedSlotsByRunner.TryGetValue(r.RunnerId, out var slots)
                    ? slots
                    : RunnerCapacity.DefaultMaxWorkflowSlots;
                activeSlotsByRunner.TryGetValue(r.RunnerId, out var activeSlots);
                return new RunnerStatusResponse(r.RunnerId, "external", activeSlots, maxSlots);
            })
            .ToArray();
        return new AgentStatusResponse(
            Running: activeAgents.Count > 0,
            IssueId: activeAgents.FirstOrDefault()?.IssueId,
            IssueNumber: activeAgents.FirstOrDefault()?.IssueNumber,
            ActiveAgents: activeAgents,
            Capacity: new AgentCapacityResponse(runnerResponses.Sum(r => r.Active), runnerResponses.Sum(r => r.Max)),
            RunnerAvailable: runnerAvailable,
            EmbeddedRunnerEnabled: false,
            RunnerMessage: runnerAvailable ? null : "No runner is connected. Start the Mohist runner process.",
            Runners: runnerResponses);
    }
}

public sealed record AgentCapacityResponse(int Active, int Max);
public sealed record RunnerStatusResponse(string Id, string Kind, int Active, int Max);
