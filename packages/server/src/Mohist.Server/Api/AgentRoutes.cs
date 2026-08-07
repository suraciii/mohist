using Microsoft.AspNetCore.Http;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Otel;
using Mohist.Server.Project.Services;
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

        group.MapGet("/status", async (
            HttpContext context,
            RunnerStatusService runnerStatus,
            WorkflowActivityQuerier projection,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await AgentStatusHandlers.GetStatusAsync(project, runnerStatus, projection, ct);
        })
        .WithMetadata(new AgentPathEndpointMetadata("agent.status"));

        group.MapGet("/sessions", async (HttpContext context, string? status, int? limit, AgentSessionListAssembler sessions) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await sessions.ListCurrentAsync(project.Id, status, limit ?? 50));
        });

        group.MapGet("/activity", async (
            HttpContext context,
            int? limit,
            AgentActivityFeedAssembler activityFeed,
            IssueQuerier issues,
            RunnerStatusService runnerStatus,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            return await AgentStatusHandlers.GetActivityAsync(project, limit, activityFeed, issues, runnerStatus, ct);
        })
        .WithMetadata(new AgentPathEndpointMetadata("agent.activity"));

        group.MapGet("/usage", async (HttpContext context, string? range, AgentUsageReporter usage, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            if (!TryParseRange(range, out var windowDays, out var rangeError))
                return rangeError;

            return ApiResults.Ok(await usage.GetUsageTimeseriesAsync(project.Id, windowDays, ct));
        });

        group.MapGet("/cost", async (HttpContext context, string? range, AgentCostRollupQuerier costRollup, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();

            if (!TryParseRange(range, out var windowDays, out var rangeError))
                return rangeError;

            return ApiResults.Ok(await costRollup.GetCostRollupAsync(project.Id, windowDays, ct));
        });

        app.MapGet("/api/agent/status", async (
            HttpContext context,
            ProjectRefResolver resolver,
            RunnerStatusService runnerStatus,
            WorkflowActivityQuerier projection,
            CancellationToken ct) =>
        {
            var (error, project) = await AgentStatusHandlers.ResolveAliasedProjectAsync(context, resolver);
            if (error is not null || project is null) return error!;
            return await AgentStatusHandlers.GetStatusAsync(project, runnerStatus, projection, ct);
        })
        .WithMetadata(new AgentPathEndpointMetadata("agent.status"));

        app.MapGet("/api/agent/activity", async (
            HttpContext context,
            int? limit,
            ProjectRefResolver resolver,
            AgentActivityFeedAssembler activityFeed,
            IssueQuerier issues,
            RunnerStatusService runnerStatus,
            CancellationToken ct) =>
        {
            var (error, project) = await AgentStatusHandlers.ResolveAliasedProjectAsync(context, resolver);
            if (error is not null || project is null) return error!;
            return await AgentStatusHandlers.GetActivityAsync(project, limit, activityFeed, issues, runnerStatus, ct);
        })
        .WithMetadata(new AgentPathEndpointMetadata("agent.activity"));

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
}

public sealed record AgentStatusResponse(
    bool Running,
    int? IssueNumber,
    IReadOnlyList<ActiveAgentDto> ActiveAgents,
    AgentCapacityResponse Capacity,
    bool RunnerAvailable,
    bool EmbeddedRunnerEnabled,
    string? RunnerMessage,
    IReadOnlyList<RunnerStatusResponse> Runners,
    AgentAmplificationDto Amplification)
{
    public static AgentStatusResponse Create(
        IReadOnlyList<ActiveAgentDto> activeAgents,
        IReadOnlyList<RunnerStatusView> runners,
        RunnerCapacityView capacity,
        AgentAmplificationDto amplification)
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
            Runners: runnerResponses,
            Amplification: amplification);
    }
}

public sealed record AgentCapacityResponse(int Active, int Max);
public sealed record RunnerStatusResponse(string Id, string Kind, int Active, int Max);
