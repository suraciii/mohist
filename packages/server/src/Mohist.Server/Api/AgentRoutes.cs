using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;

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
            var activeAgents = await projection.ListActiveAgentsAsync(project.Id);

            return ApiResults.Ok(AgentStatusResponse.Create(activeAgents, runners));
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

        return app;
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
        var projectRunners = await grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(projectId)).ListRunnersAsync();
        var globalRunners = await grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListRunnersAsync();
        var candidates = projectRunners
            .Concat(globalRunners)
            .GroupBy(r => r.RunnerId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
        var available = new List<RunnerInfo>();
        foreach (var runner in candidates)
        {
            var grain = grains.GetGrain<IRunnerGrain>(runner.RunnerId);
            if (await grain.IsAvailableAsync())
                available.Add(runner);
        }
        return available;
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
    public static AgentStatusResponse Create(IReadOnlyList<ActiveAgentDto> activeAgents, IReadOnlyList<RunnerInfo> runners)
    {
        var runnerAvailable = runners.Count > 0;
        var activeSlotsByRunner = activeAgents
            .GroupBy(a => a.RunnerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var runnerResponses = runners
            .Select(r =>
            {
                var maxSlots = RunnerCapacity.Normalize(r.MaxWorkflowSlots);
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
